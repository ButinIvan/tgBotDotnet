using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public class UpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UpdateHandler> _logger;
    // Храним состояния пользователей между сообщениями; TelegramBotService создает новый UpdateHandler на каждое обновление,
    // поэтому словарь должен быть статическим/общим.
    private static readonly ConcurrentDictionary<long, UserState> _userStates = new();

    private sealed class ClassInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    private async Task SendNewsPage(long chatId, int messageId, long userTelegramId, int classId, int page, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        const int maxPages = 10;

        // Проверка доступа: только если пользователь имеет отношение к классу
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == userTelegramId, cancellationToken);
        if (user == null) return;

        var hasAccess = (user.Role == UserRole.Admin && await _dbContext.Classes.AnyAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId, cancellationToken))
                        || (user.Role == UserRole.Moderator && user.ClassId == classId)
                        || (user.Role == UserRole.Parent && user.IsVerified && (
                            user.ClassId == classId ||
                            await _dbContext.ParentClassLinks.AnyAsync(l => l.UserId == user.Id && l.ClassId == classId, cancellationToken)
                        ));
        if (!hasAccess)
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа к новостям этого класса.", cancellationToken: cancellationToken);
            return;
        }

        var newsQuery = _dbContext.News
            .Where(n => n.ClassId == classId)
            .OrderByDescending(n => n.CreatedAt);

        var totalCount = await newsQuery.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        totalPages = Math.Min(totalPages, maxPages);

        if (totalPages == 0)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пока нет новостей или отчетов.", cancellationToken: cancellationToken);
            return;
        }

        page = Math.Max(1, Math.Min(page, totalPages));
        var skip = (page - 1) * pageSize;

        var news = await newsQuery
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var pageText = string.Join("\n\n", news.Select((item, idx) =>
        {
            var localDate = item.CreatedAt.AddHours(3); // UTC+3
            var content = (item.Content ?? string.Empty).TrimEnd();
            return $"<b>{idx + 1}. {item.Title}</b>\n\n{content}\n\nДата: {localDate:dd.MM.yyyy HH:mm}";
        }));

        var keyboard = BuildNewsPaginationKeyboard(classId, page, totalPages);

        if (messageId > 0)
        {
            await _botClient.EditMessageTextAsync(
                chatId,
                messageId: messageId,
                text: pageText,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                pageText,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

    private InlineKeyboardMarkup BuildNewsPaginationKeyboard(int classId, int currentPage, int totalPages)
    {
        if (totalPages <= 1) return null!;

        var buttons = new List<InlineKeyboardButton>();

        if (currentPage > 1)
            buttons.Add(InlineKeyboardButton.WithCallbackData("« Пред", $"news_prev_{classId}_{currentPage}"));
        if (currentPage < totalPages)
            buttons.Add(InlineKeyboardButton.WithCallbackData("След »", $"news_next_{classId}_{currentPage}"));

        return new InlineKeyboardMarkup(buttons);
    }

    public UpdateHandler(
        ITelegramBotClient botClient,
        ApplicationDbContext dbContext,
        ILogger<UpdateHandler> logger)
    {
        _botClient = botClient;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task HandleUpdate(Update update, CancellationToken cancellationToken)
    {
        try
        {
            // Обрабатываем callback queries (нажатия на кнопки)
            if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQuery(callbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { } message)
                return;

            var chatId = message.Chat.Id;
            var userId = message.From?.Id ?? 0;

            // Получаем или создаем пользователя
            var user = await GetOrCreateUser(userId, message.From);

            // Проверяем состояние пользователя (для процесса верификации или создания новости)
            if (_userStates.TryGetValue(userId, out var state))
            {
                await HandleUserState(user, state, message, cancellationToken);
                return;
            }

        // Если пользователь не заполнил ФИО/телефон, а состояния нет — начинаем регистрацию сразу
        if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.PhoneNumber))
        {
            var newState = new UserState { Step = VerificationStep.WaitingForFullName };
            _userStates[user.TelegramUserId] = newState;
            await HandleUserState(user, newState, message, cancellationToken);
            return;
        }

            // Обрабатываем команды
            if (message.Text is { } text && text.StartsWith('/'))
            {
                await HandleCommand(user, text, message, cancellationToken);
            }
            else
            {
                await _botClient.SendTextMessageAsync(
                    chatId,
                    "Используйте команды для взаимодействия с ботом. Введите /help для списка команд.",
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке сообщения");
            if (update.Message?.Chat.Id is { } chatId)
            {
                await _botClient.SendTextMessageAsync(
                    chatId,
                    "Произошла ошибка. Попробуйте позже.",
                    cancellationToken: cancellationToken);
            }
        }
    }

    private async Task<AppUser> GetOrCreateUser(long telegramUserId, BotUser? telegramUser)
    {
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null)
        {
            user = new AppUser
            {
                TelegramUserId = telegramUserId,
                Username = telegramUser?.Username,
                FirstName = telegramUser?.FirstName,
                LastName = telegramUser?.LastName,
                Role = UserRole.Unverified,
                IsVerified = false,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();
        }

        return user;
    }

    private async Task HandleCommand(AppUser user, string command, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "/start":
                await HandleStartCommand(user, message, cancellationToken);
                break;

            case "/help":
                await HandleHelpCommand(user, message, cancellationToken);
                break;

            case "/register":
                await HandleRegisterCommand(user, message, cancellationToken);
                break;

            case "/createclass":
                // Запускаем пошаговый ввод названия класса
                if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Сначала пройдите регистрацию: /register (ФИО и телефон).",
                        cancellationToken: cancellationToken);
                    return;
                }

                _userStates[user.TelegramUserId] = new UserState
                {
                    Step = VerificationStep.WaitingForClassNameCreate,
                    ClassAction = ClassAction.CreateClass
                };
                await _botClient.SendTextMessageAsync(chatId, "Введите название класса:", cancellationToken: cancellationToken);
                break;

            case "/createnews":
                if (user.Role == UserRole.Admin)
                    await HandleCreateNewsCommand(user, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/viewnews":
                if ((user.Role == UserRole.Admin || user.Role == UserRole.Moderator) || (user.Role == UserRole.Parent && user.IsVerified))
                    await HandleViewNewsCommand(user, parts, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа. Пожалуйста, пройдите верификацию.", cancellationToken: cancellationToken);
                break;

            case "/myclass":
                await HandleMyClassCommand(user, message, cancellationToken);
                break;

            case "/requestclass":
                if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Сначала пройдите регистрацию: /register (ФИО и телефон).",
                        cancellationToken: cancellationToken);
                    return;
                }

                _userStates[user.TelegramUserId] = new UserState
                {
                    Step = VerificationStep.WaitingForClassNameRequest,
                    ClassAction = ClassAction.RequestClass
                };
                await _botClient.SendTextMessageAsync(chatId, "Введите название класса, к которому хотите присоединиться:", cancellationToken: cancellationToken);
                break;

            case "/verifications":
                if (user.Role == UserRole.Admin)
                    await HandleVerificationsCommand(user, parts, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/parents":
                if (user.Role == UserRole.Admin)
                    await HandleParentsCommand(user, parts, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/moderators":
                if (user.Role == UserRole.Admin || user.Role == UserRole.Moderator)
                    await HandleListModeratorsCommand(user, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/addmoderator":
                if (user.Role == UserRole.Admin)
                    await HandleAddModeratorCommand(user, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            default:
                await _botClient.SendTextMessageAsync(chatId, "Неизвестная команда. Введите /help для списка команд.", cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleStartCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var text = "Добро пожаловать в бот для школьных классов!\n\n";

        if (user.Role == UserRole.Unverified)
        {
            text += "Вы еще не зарегистрированы. Используйте /register для регистрации.";
        }
        else if (user.Role == UserRole.Admin)
        {
            text += "Вы администратор класса.\n\n";
            text += "Доступные команды:\n";
            text += "/createclass - Создать класс\n";
            text += "/createnews - Создать новость или отчет\n";
            text += "/verifications - Просмотреть заявки на верификацию\n";
            text += "/moderators - Список модераторов\n";
            text += "/addmoderator - Добавить модератора\n";
            text += "/removemoderator - Удалить права модератора\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
            text += "/help - Справка";
        }
        else if (user.Role == UserRole.Moderator)
        {
            text += "Вы модератор класса.\n\n";
            text += "Доступные команды:\n";
            text += "/createnews - Создать новость или отчет\n";
            text += "/verifications - Просмотреть заявки на верификацию\n";
            text += "/moderators - Список модераторов\n";
            text += "/addmoderator - Добавить модератора\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
            text += "/help - Справка";
        }
        else if (user.Role == UserRole.Parent)
        {
            if (user.IsVerified)
            {
                text += $"Вы родитель класса: {user.Class?.Name ?? "Не назначен"}\n\n";
                text += "Доступные команды:\n";
                text += "/viewnews - Просмотреть новости и отчеты\n";
                text += "/myclass - Мои классы\n";
                text += "/help - Справка";
            }
            else
            {
                text += "Вы зарегистрированы. Чтобы привязаться к классу, отправьте:\n";
                text += "/requestclass <Название класса>\n";
                text += "Или создайте свой класс: /createclass <Название>\n";
                text += "/help - Справка";
            }
        }

        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }

    private async Task HandleHelpCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var text = "Справка по командам:\n\n";

        if (user.Role == UserRole.Admin)
        {
            text += "Команды администратора:\n";
            text += "/start - Приветствие\n";
            text += "/help - Эта справка\n";
            text += "/register - Регистрация (для родителей)\n";
            text += "/createclass <Название> - Создать класс (до 10 шт.)\n";
            text += "/createnews - Создать новость/отчет (бот)\n";
            text += "/viewnews - Посмотреть новости привязанных классов\n";
            text += "/myclass - Посмотреть все классы, где вы админ/модератор/родитель\n";
            text += "/requestclass <Название> - Подать заявку на класс (как родитель)\n";
            text += "/verifications [Название класса] - Заявки на верификацию\n";
            text += "/moderators - Список модераторов\n";
            text += "/addmoderator <TelegramUserId> - Добавить модератора\n";
            text += "/removemoderator <TelegramUserId> - Удалить права модератора\n";
            text += "/parents [Название класса] - Список родителей класса\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
        }
        else if (user.Role == UserRole.Moderator)
        {
            text += "Команды модератора:\n";
            text += "/createnews - Создать новость или отчет\n";
            text += "/verifications - Просмотреть заявки на верификацию родителей\n";
            text += "/moderators - Список модераторов\n";
            text += "/addmoderator - Добавить модератора\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
        }
        else if (user.Role == UserRole.Parent && user.IsVerified)
        {
            text += "Команды родителя:\n";
            text += "/viewnews - Просмотреть новости и отчеты вашего класса\n";
            text += "/myclass - Мои классы\n";
        }
        else
        {
            text += "/register - Зарегистрироваться как родитель\n";
            text += "/requestclass <Название класса> - Подать заявку на присоединение\n";
            text += "/createclass <Название> - Создать свой класс\n";
        }

        text += "\n/help - Показать эту справку";

        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }

    private async Task HandleRegisterCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        // Админам/модераторам регистрация не нужна
        if (user.Role == UserRole.Admin || user.Role == UserRole.Moderator)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы уже обладаете правами администратора/модератора.",
                cancellationToken: cancellationToken);
            return;
        }

        // Всегда запускаем процесс верификации, если нет ФИО/телефона или пользователь не верифицирован
        _userStates[user.TelegramUserId] = new UserState { Step = VerificationStep.WaitingForFullName };
        
        await _botClient.SendTextMessageAsync(
            chatId,
            "Для регистрации в качестве родителя необходимо пройти верификацию.\n\n" +
            "Пожалуйста, введите ваше ФИО (Фамилия Имя Отчество):",
            cancellationToken: cancellationToken);
    }

    private async Task HandleRequestClassCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.PhoneNumber))
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Сначала завершите регистрацию: /register",
                cancellationToken: cancellationToken);
            return;
        }

        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts == null || parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Использование: /requestclass <Название класса>\n" +
                "Пример: /requestclass 5А",
                cancellationToken: cancellationToken);
            return;
        }

        var className = string.Join(" ", parts.Skip(1));
        var targetClass = await _dbContext.Classes.FirstOrDefaultAsync(c => c.Name == className, cancellationToken);
        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Класс с таким названием не найден. Проверьте написание или попросите администратора создать класс.",
                cancellationToken: cancellationToken);
            return;
        }

        // Проверяем, нет ли уже заявки в этот класс
        var existingVerification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v =>
                v.TelegramUserId == user.TelegramUserId &&
                v.ClassId == targetClass.Id &&
                v.Status == VerificationStatus.Pending,
                cancellationToken);

        if (existingVerification != null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Заявка на этот класс уже отправлена и находится в ожидании.",
                cancellationToken: cancellationToken);
            return;
        }

        var verification = new ParentVerification
        {
            TelegramUserId = user.TelegramUserId,
            FullName = user.FullName!,
            PhoneNumber = user.PhoneNumber!,
            Status = VerificationStatus.Pending,
            ClassId = targetClass.Id,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.ParentVerifications.Add(verification);
        await _dbContext.SaveChangesAsync();

        await NotifyAdminsAboutNewVerification(verification);

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Заявка отправлена админу класса '{targetClass.Name}'. Ожидайте подтверждения.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleUserState(AppUser user, UserState state, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        switch (state.Step)
        {
            case VerificationStep.WaitingForFullName:
                var fullNameInput = message.Text?.Trim() ?? string.Empty;

                // ФИО не должно быть пустым, не должно начинаться с "/" и должно состоять минимум из двух слов
                var fullNameParts = fullNameInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (string.IsNullOrWhiteSpace(fullNameInput) ||
                    fullNameInput.StartsWith("/") ||
                    fullNameParts.Length < 2)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Пожалуйста, введите ваше ФИО (Через пробел).",
                        cancellationToken: cancellationToken);
                    return;
                }

                state.FullName = fullNameInput;
                state.Step = VerificationStep.WaitingForPhoneNumber;

                await _botClient.SendTextMessageAsync(
                    chatId,
                    "Теперь введите ваш номер телефона (например: +7 999 123 45 67):",
                    cancellationToken: cancellationToken);
                break;

            case VerificationStep.WaitingForPhoneNumber:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Пожалуйста, введите номер телефона.",
                        cancellationToken: cancellationToken);
                    return;
                }

                state.PhoneNumber = message.Text.Trim();

                // Удаляем состояние
                _userStates.TryRemove(user.TelegramUserId, out _);

                // Обновляем пользователя
                user.Role = UserRole.Parent;
                user.FullName = state.FullName;
                user.PhoneNumber = state.PhoneNumber;
                await _dbContext.SaveChangesAsync();

                await _botClient.SendTextMessageAsync(
                    chatId,
                    "Регистрация завершена! Теперь вы можете:\n" +
                    "• Подать заявку на существующий класс: /requestclass <Название класса>\n" +
                    "• Или создать свой класс: /createclass <Название>",
                    cancellationToken: cancellationToken);
                break;

            case VerificationStep.WaitingForNewsTitle:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Пожалуйста, введите заголовок.",
                        cancellationToken: cancellationToken);
                    return;
                }

                state.NewsTitle = message.Text.Trim();
                state.Step = VerificationStep.WaitingForNewsContent;

                await _botClient.SendTextMessageAsync(
                    chatId,
                    "Теперь введите содержание новости/отчета:",
                    cancellationToken: cancellationToken);
                break;

            case VerificationStep.WaitingForNewsContent:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Пожалуйста, введите содержание.",
                        cancellationToken: cancellationToken);
                    return;
                }

                state.NewsContent = message.Text.Trim();

                // Создаем новость
                if (state.ClassId.HasValue && state.NewsType.HasValue && state.NewsTitle != null)
                {
                    var news = new News
                    {
                        ClassId = state.ClassId.Value,
                        AuthorTelegramUserId = user.TelegramUserId,
                        Title = state.NewsTitle,
                        Content = state.NewsContent,
                        Type = state.NewsType.Value,
                        CreatedAt = DateTime.UtcNow
                    };

                    _dbContext.News.Add(news);
                    await _dbContext.SaveChangesAsync();

                    // Уведомляем всех родителей класса
                    await NotifyParentsAboutNewNews(news);

                    await _botClient.SendTextMessageAsync(
                        chatId,
                        $"✅ {(state.NewsType == NewsType.News ? "Новость" : "Отчет")} успешно создана и отправлена родителям!",
                        cancellationToken: cancellationToken);
                }

                // Удаляем состояние
                _userStates.TryRemove(user.TelegramUserId, out _);
                break;

            case VerificationStep.WaitingForClassNameCreate:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Введите название класса.", cancellationToken: cancellationToken);
                    return;
                }

                var classNameCreate = message.Text.Trim();

                // Проверяем лимит: не более 10 классов на одного админа
                var adminClassesCount = await _dbContext.Classes
                    .CountAsync(c => c.AdminTelegramUserId == user.TelegramUserId, cancellationToken);

                if (adminClassesCount >= 10)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Вы достигли лимита: максимум 10 классов на одного администратора.",
                        cancellationToken: cancellationToken);
                    _userStates.TryRemove(user.TelegramUserId, out _);
                    return;
                }

                var newClass = new Class
                {
                    Name = classNameCreate,
                    AdminTelegramUserId = user.TelegramUserId,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.Classes.Add(newClass);

                user.Role = UserRole.Admin;
                user.IsVerified = true;
                await _dbContext.SaveChangesAsync(); // получить Id

                user.ClassId = newClass.Id;
                user.Class = newClass;
                await _dbContext.SaveChangesAsync();

                _userStates.TryRemove(user.TelegramUserId, out _);

                await _botClient.SendTextMessageAsync(
                    chatId,
                    $"Класс '{classNameCreate}' успешно создан! Вы назначены администратором этого класса.",
                    cancellationToken: cancellationToken);
                break;

            case VerificationStep.WaitingForClassNameRequest:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Введите название класса.", cancellationToken: cancellationToken);
                    return;
                }

                var classNameRequest = message.Text.Trim();
                var targetClass = await _dbContext.Classes
                    .FirstOrDefaultAsync(c => c.Name == classNameRequest, cancellationToken);

                if (targetClass == null)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Класс с таким названием не найден. Проверьте написание или попросите администратора создать класс.",
                        cancellationToken: cancellationToken);
                    return;
                }

                var existingVerification = await _dbContext.ParentVerifications
                    .AnyAsync(v =>
                        v.TelegramUserId == user.TelegramUserId &&
                        v.ClassId == targetClass.Id &&
                        v.Status == VerificationStatus.Pending,
                        cancellationToken);

                if (existingVerification)
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Заявка на этот класс уже отправлена и находится в ожидании.",
                        cancellationToken: cancellationToken);
                    _userStates.TryRemove(user.TelegramUserId, out _);
                    return;
                }

                var verificationRequest = new ParentVerification
                {
                    TelegramUserId = user.TelegramUserId,
                    FullName = user.FullName!,
                    PhoneNumber = user.PhoneNumber!,
                    Status = VerificationStatus.Pending,
                    ClassId = targetClass.Id,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.ParentVerifications.Add(verificationRequest);
                await _dbContext.SaveChangesAsync();

                _userStates.TryRemove(user.TelegramUserId, out _);

                await NotifyAdminsAboutNewVerification(verificationRequest);

                await _botClient.SendTextMessageAsync(
                    chatId,
                    $"Заявка отправлена админу класса '{targetClass.Name}'. Ожидайте подтверждения.",
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task NotifyAdminsAboutNewVerification(ParentVerification verification)
    {
        var adminsAndModerators = await _dbContext.Users
            .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Moderator)
            .ToListAsync();

        var message = $"Новая заявка на верификацию родителя:\n\n" +
                     $"ФИО: {verification.FullName}\n" +
                     $"Телефон: {verification.PhoneNumber}\n" +
                     $"ID заявки: {verification.Id}\n\n" +
                     $"Используйте /verifications для просмотра всех заявок или откройте веб-админ-панель.";

        foreach (var admin in adminsAndModerators)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    admin.TelegramUserId,
                    message,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Не удалось отправить уведомление админу/модератору {admin.TelegramUserId}");
            }
        }
    }

    private async Task HandleCreateClassCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts == null || parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Использование: /createclass <Название класса>\n" +
                "Пример: /createclass 5А",
                cancellationToken: cancellationToken);
            return;
        }

        var className = string.Join(" ", parts.Skip(1));

        // Проверяем лимит: не более 10 классов на одного админа
        var adminClassesCount = await _dbContext.Classes
            .CountAsync(c => c.AdminTelegramUserId == user.TelegramUserId, cancellationToken);

        if (adminClassesCount >= 10)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы достигли лимита: максимум 10 классов на одного администратора.",
                cancellationToken: cancellationToken);
            return;
        }

        // Создаем класс и автоматически делаем пользователя админом
        var newClass = new Class
        {
            Name = className,
            AdminTelegramUserId = user.TelegramUserId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Classes.Add(newClass);
        
        // Устанавливаем пользователя как админа
        user.Role = UserRole.Admin;
        user.IsVerified = true;
        await _dbContext.SaveChangesAsync(); // сохраняем, чтобы получить Id класса

        // Привязываем админа к созданному классу
        user.ClassId = newClass.Id;
        user.Class = newClass;
        await _dbContext.SaveChangesAsync();

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Класс '{className}' успешно создан! Вы назначены администратором этого класса.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleCreateNewsCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (user.ClassId == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны к классу.",
                cancellationToken: cancellationToken);
            return;
        }

        // Получаем класс пользователя
        var userClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == user.ClassId);

        // Устанавливаем состояние для создания новости
        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForNewsType,
            ClassId = user.ClassId
        };

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Новость", "news_type_news"),
                InlineKeyboardButton.WithCallbackData("Отчет", "news_type_report")
            }
        });

        await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите тип публикации:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private async Task HandleViewNewsCommand(AppUser user, string[] parts, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(chatId, "Использование: /viewnews <Название класса>", cancellationToken: cancellationToken);
            return;
        }

        var className = string.Join(" ", parts.Skip(1));
        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Name == className, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден.", cancellationToken: cancellationToken);
            return;
        }

        // Проверка доступа: админ/модератор этого класса или родитель, привязанный к нему
        var hasAccess = (user.Role == UserRole.Admin && targetClass.AdminTelegramUserId == user.TelegramUserId)
                        || (user.Role == UserRole.Moderator && user.ClassId == targetClass.Id)
                        || (user.Role == UserRole.Parent && user.IsVerified && (
                            user.ClassId == targetClass.Id ||
                            await _dbContext.ParentClassLinks.AnyAsync(l => l.UserId == user.Id && l.ClassId == targetClass.Id, cancellationToken)
                        ));

        if (!hasAccess)
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа к новостям этого класса.", cancellationToken: cancellationToken);
            return;
        }

        // Пагинация: страницы по 5 записей, до 10 страниц
        const int pageSize = 5;
        const int maxPages = 10;
        var newsQuery = _dbContext.News
            .Where(n => n.ClassId == targetClass.Id)
            .OrderByDescending(n => n.CreatedAt);

        var totalCount = await newsQuery.CountAsync(cancellationToken);
        var page = (int)Math.Ceiling(totalCount / (double)pageSize);
        page = Math.Min(page, maxPages);

        if (page == 0)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пока нет новостей или отчетов.", cancellationToken: cancellationToken);
            return;
        }

        // Формируем одну страницу (первую)
        var news = await newsQuery
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var startIdx = (page - 1) * pageSize;
        var pageText = string.Join("\n\n", news.Select((item, idx) =>
        {
            var localDate = item.CreatedAt.AddHours(3);
            var content = (item.Content ?? string.Empty).TrimEnd();
            return $"<b>{startIdx + idx + 1}. {item.Title}</b>\n\n{content}\n\nДата: {localDate:dd.MM.yyyy HH:mm}";
        }));

        var keyboard = BuildNewsPaginationKeyboard(targetClass.Id, 1, page);

        await _botClient.SendTextMessageAsync(
            chatId,
            pageText,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private async Task HandleVerificationsCommand(AppUser user, string[] parts, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        int? classId = null;

        if (parts.Length > 1)
        {
            var className = string.Join(" ", parts.Skip(1));
            var targetClass = await _dbContext.Classes
                .FirstOrDefaultAsync(c => c.AdminTelegramUserId == user.TelegramUserId && c.Name == className, cancellationToken);
            if (targetClass == null)
            {
                await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не его админ.", cancellationToken: cancellationToken);
                return;
            }
            classId = targetClass.Id;
        }
        else
        {
            classId = user.ClassId;
        }

        if (classId == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Вы не привязаны к классу.", cancellationToken: cancellationToken);
            return;
        }

        var pendingVerifications = await _dbContext.ParentVerifications
            .Where(v => v.Status == VerificationStatus.Pending && (v.ClassId == classId || v.ClassId == null))
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();

        if (!pendingVerifications.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Нет заявок на верификацию.",
                cancellationToken: cancellationToken);
            return;
        }

        foreach (var verification in pendingVerifications)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"approve_{verification.Id}_{classId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"reject_{verification.Id}")
                }
            });

            var text = $"Заявка #{verification.Id}\n\n" +
                      $"ФИО: {verification.FullName}\n" +
                      $"Телефон: {verification.PhoneNumber}\n" +
                      $"Дата: {verification.CreatedAt:dd.MM.yyyy HH:mm}";

            await _botClient.SendTextMessageAsync(
                chatId,
                text,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

    // Обработка callback-запросов (кнопки)
    public async Task HandleCallbackQuery(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        var userId = callbackQuery.From.Id;
        var chatId = callbackQuery.Message?.Chat.Id ?? 0;

        if (data == null)
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            if (data.StartsWith("approve_"))
            {
                // Формат approve_{id} или approve_{id}_{classId}
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var verificationId))
                {
                    int? classId = null;
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedClassId))
                        classId = parsedClassId;

                    await HandleApproveVerification(verificationId, classId, userId, chatId, cancellationToken);
                }
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Одобрено", cancellationToken: cancellationToken);
            }
            else if (data.StartsWith("reject_"))
            {
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var verificationId))
                {
                    await HandleRejectVerification(verificationId, userId, chatId, cancellationToken);
                }
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Отклонено", cancellationToken: cancellationToken);
            }
            else if (data == "news_type_news" || data == "news_type_report")
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var state = _userStates.GetValueOrDefault(userId);
                if (state != null && state.Step == VerificationStep.WaitingForNewsType)
                {
                    state.NewsType = data == "news_type_news" ? NewsType.News : NewsType.Report;
                    state.Step = VerificationStep.WaitingForNewsTitle;
                    
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Введите заголовок:",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка состояния", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("news_prev_") || data.StartsWith("news_next_"))
            {
                var partsCb = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                // format: news_prev_{classId}_{currentPage} or news_next_{classId}_{currentPage}
                if (partsCb.Length == 4 &&
                    int.TryParse(partsCb[2], out var classId) &&
                    int.TryParse(partsCb[3], out var currentPage))
                {
                    var direction = partsCb[1] == "prev" ? -1 : 1;
                    var nextPage = currentPage + direction;
                    var messageId = callbackQuery.Message?.MessageId ?? 0;
                    await SendNewsPage(callbackQuery.Message!.Chat.Id, messageId, userId, classId, nextPage, cancellationToken);
                }

                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            }
            else
            {
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке callback");
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Произошла ошибка", cancellationToken: cancellationToken);
        }
    }

    private async Task NotifyParentsAboutNewNews(News news)
    {
        // Родители могут быть привязаны через ClassId или ParentClassLinks; добавляем также админов и модераторов этого класса
        var parentIdsFromLinks = await _dbContext.ParentClassLinks
            .Where(l => l.ClassId == news.ClassId)
            .Select(l => l.UserId)
            .ToListAsync();

        var recipients = await _dbContext.Users
            .Where(u =>
                (
                    (u.Role == UserRole.Parent && u.IsVerified &&
                     (u.ClassId == news.ClassId || parentIdsFromLinks.Contains(u.Id)))
                    || (u.Role == UserRole.Admin && u.ClassId == news.ClassId)
                    || (u.Role == UserRole.Moderator && u.ClassId == news.ClassId)
                ))
            .Select(u => u.TelegramUserId)
            .Distinct()
            .ToListAsync();

        var localDate = news.CreatedAt.AddHours(3); // UTC +3
        var content = (news.Content ?? string.Empty).TrimEnd();
        var message = $"<b>{news.Title}</b>\n\n" +
                     $"{content}\n\n" +
                     $"Дата: {localDate:dd.MM.yyyy HH:mm}";

        foreach (var tgId in recipients)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    tgId,
                    message,
                    parseMode: ParseMode.Html,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Не удалось отправить новость пользователю {tgId}");
            }
        }
    }

    private async Task HandleApproveVerification(int verificationId, int? classIdOverride, long adminUserId, long chatId, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == adminUserId);
        if (admin == null || (admin.Role != UserRole.Admin && admin.Role != UserRole.Moderator))
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для этого действия.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Заявка не найдена или уже обработана.", cancellationToken: cancellationToken);
            return;
        }

        // Определяем класс: либо из callback, либо из ClassId админа
        var targetClassId = classIdOverride ?? admin.ClassId;

        if (targetClassId == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Сначала создайте класс.", cancellationToken: cancellationToken);
            return;
        }

        var adminClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == targetClassId.Value);

        if (adminClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден.", cancellationToken: cancellationToken);
            return;
        }

        // Обновляем заявку
        verification.Status = VerificationStatus.Approved;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = adminUserId;
        verification.ClassId = adminClass.Id;

        // Обновляем пользователя
        var parent = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == verification.TelegramUserId);

        if (parent != null)
        {
            parent.IsVerified = true;
            parent.VerifiedAt = DateTime.UtcNow;
            parent.ClassId = adminClass.Id;

            // Добавляем связь родителя с классом (многие-ко-многим)
            var hasLink = await _dbContext.ParentClassLinks
                .AnyAsync(l => l.UserId == parent.Id && l.ClassId == adminClass.Id, cancellationToken);
            if (!hasLink)
            {
                _dbContext.ParentClassLinks.Add(new ParentClassLink
                {
                    UserId = parent.Id,
                    ClassId = adminClass.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync();

        // Уведомляем родителя
        try
        {
            await _botClient.SendTextMessageAsync(
                verification.TelegramUserId,
                $"✅ Ваша заявка одобрена! Вы получили доступ к классу '{adminClass.Name}'.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить родителя");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Заявка #{verificationId} одобрена. Родитель получил доступ к классу '{adminClass.Name}'.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleRejectVerification(int verificationId, long adminUserId, long chatId, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == adminUserId);
        if (admin == null || (admin.Role != UserRole.Admin && admin.Role != UserRole.Moderator))
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для этого действия.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Заявка не найдена или уже обработана.", cancellationToken: cancellationToken);
            return;
        }

        verification.Status = VerificationStatus.Rejected;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = adminUserId;

        await _dbContext.SaveChangesAsync();

        // Уведомляем родителя
        try
        {
            await _botClient.SendTextMessageAsync(
                verification.TelegramUserId,
                "❌ Ваша заявка на верификацию была отклонена. Обратитесь к администратору для уточнения.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить родителя");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Заявка #{verificationId} отклонена.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleAddModeratorCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts == null || parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Использование: /addmoderator <Telegram User ID>\n" +
                "Пример: /addmoderator 123456789",
                cancellationToken: cancellationToken);
            return;
        }

        if (!long.TryParse(parts[1], out var targetUserId))
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Неверный Telegram User ID.",
                cancellationToken: cancellationToken);
            return;
        }

        if (user.ClassId == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны к классу.",
                cancellationToken: cancellationToken);
            return;
        }

        var targetUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == targetUserId);

        if (targetUser == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Пользователь не найден.",
                cancellationToken: cancellationToken);
            return;
        }

        if (targetUser.Role == UserRole.Admin)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Нельзя изменить роль администратора.",
                cancellationToken: cancellationToken);
            return;
        }

        if (targetUser.ClassId != user.ClassId)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Пользователь не принадлежит вашему классу.",
                cancellationToken: cancellationToken);
            return;
        }

        targetUser.Role = UserRole.Moderator;
        targetUser.IsVerified = true;
        targetUser.ClassId = user.ClassId;
        await _dbContext.SaveChangesAsync();

        await _botClient.SendTextMessageAsync(
            chatId,
            $"✅ Пользователь {targetUser.FirstName ?? "ID: " + targetUserId} теперь модератор.",
            cancellationToken: cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                targetUserId,
                $"✅ Вы получили права модератора в классе '{user.Class?.Name}'.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить нового модератора");
        }
    }

    private async Task HandleRemoveModeratorCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts == null || parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Использование: /removemoderator <Telegram User ID>\n" +
                "Пример: /removemoderator 123456789",
                cancellationToken: cancellationToken);
            return;
        }

        if (!long.TryParse(parts[1], out var targetUserId))
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Неверный Telegram User ID.",
                cancellationToken: cancellationToken);
            return;
        }

        if (user.ClassId == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны к классу.",
                cancellationToken: cancellationToken);
            return;
        }

        var targetUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == targetUserId && u.ClassId == user.ClassId);

        if (targetUser == null || targetUser.Role != UserRole.Moderator)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Модератор не найден.",
                cancellationToken: cancellationToken);
            return;
        }

        targetUser.Role = UserRole.Parent;
        await _dbContext.SaveChangesAsync();

        await _botClient.SendTextMessageAsync(
            chatId,
            $"✅ Права модератора удалены у пользователя {targetUser.FirstName ?? "ID: " + targetUserId}.",
            cancellationToken: cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                targetUserId,
                $"❌ Ваши права модератора в классе '{user.Class?.Name}' были удалены администратором.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить бывшего модератора");
        }
    }

    private async Task HandleListModeratorsCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (user.ClassId == null)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны к классу.",
                cancellationToken: cancellationToken);
            return;
        }

        var moderators = await _dbContext.Users
            .Where(u => u.ClassId == user.ClassId && u.Role == UserRole.Moderator)
            .ToListAsync();

        if (!moderators.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "В вашем классе нет модераторов.",
                cancellationToken: cancellationToken);
            return;
        }

        var text = "Модераторы класса:\n\n";
        foreach (var moderator in moderators)
        {
            text += $"• {moderator.FirstName ?? "Не указано"} {moderator.LastName ?? ""}\n";
            text += $"  ID: {moderator.TelegramUserId}\n";
            text += $"  Username: @{moderator.Username ?? "не указан"}\n\n";
        }

        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }

    private async Task HandleAdminPanelCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        
        // Получаем URL для админ-панели (нужно будет настроить в конфигурации)
        var baseUrl = Environment.GetEnvironmentVariable("ADMIN_PANEL_URL") ?? "http://localhost:5010";
        var loginUrl = $"{baseUrl}/Account/Login?telegramUserId={user.TelegramUserId}";

        var text = "🌐 Админ-панель\n\n";
        text += "Для доступа к веб-интерфейсу:\n";
        text += $"1. Перейдите по ссылке: {loginUrl}\n";
        text += "2. Или откройте админ-панель и введите ваш Telegram User ID: " + user.TelegramUserId;

        await _botClient.SendTextMessageAsync(
            chatId,
            text,
            cancellationToken: cancellationToken);
    }

    private async Task HandleMyClassCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        // Собираем все роли/привязки:
        // - как админ (все классы, где AdminTelegramUserId = user.TelegramUserId)
        // - как модератор (ClassId)
        // - как родитель (ClassId и ParentClassLinks)

        var adminClasses = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Админ" })
            .ToListAsync(cancellationToken);

        var moderatorClasses = new List<ClassInfo>();
        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
        {
            moderatorClasses = await _dbContext.Classes
                .Where(c => c.Id == user.ClassId.Value)
                .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Модератор" })
                .ToListAsync(cancellationToken);
        }

        var parentClassIds = new List<int>();
        if (user.ClassId.HasValue)
            parentClassIds.Add(user.ClassId.Value);

        var linkIds = await _dbContext.ParentClassLinks
            .Where(l => l.UserId == user.Id)
            .Select(l => l.ClassId)
            .ToListAsync(cancellationToken);
        parentClassIds.AddRange(linkIds);
        parentClassIds = parentClassIds.Distinct().ToList();

        var parentClasses = await _dbContext.Classes
            .Where(c => parentClassIds.Contains(c.Id))
            .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Родитель" })
            .ToListAsync(cancellationToken);

        var all = adminClasses
            .Concat(moderatorClasses)
            .Concat(parentClasses)
            .GroupBy(c => c.Id)
            .Select(g => g.First()) // уникальные классы
            .OrderBy(c => c.Name)
            .ToList();

        if (!all.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны ни к одному классу.",
                cancellationToken: cancellationToken);
            return;
        }

        var response = "Ваши классы:\n" + string.Join("\n", all.Select(c => $"• {c.Name} ({c.Role})"));
        await _botClient.SendTextMessageAsync(chatId, response, cancellationToken: cancellationToken);
    }

    private async Task HandleParentsCommand(AppUser user, string[] parts, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        int? classId = null;
        if (parts.Length > 1)
        {
            var className = string.Join(" ", parts.Skip(1));
            var targetClass = await _dbContext.Classes
                .FirstOrDefaultAsync(c => c.AdminTelegramUserId == user.TelegramUserId && c.Name == className, cancellationToken);
            if (targetClass == null)
            {
                await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не его админ.", cancellationToken: cancellationToken);
                return;
            }
            classId = targetClass.Id;
        }
        else
        {
            classId = user.ClassId;
        }

        if (classId == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Вы не привязаны к классу.", cancellationToken: cancellationToken);
            return;
        }

        var linkIds = await _dbContext.ParentClassLinks
            .Where(l => l.ClassId == classId.Value)
            .Select(l => l.UserId)
            .ToListAsync(cancellationToken);

        var parents = await _dbContext.Users
            .Where(u =>
                (u.Role == UserRole.Parent || u.Role == UserRole.Unverified || u.Role == UserRole.Moderator) &&
                (u.ClassId == classId.Value || linkIds.Contains(u.Id)))
            .OrderBy(u => u.FullName ?? u.FirstName ?? u.Username)
            .ToListAsync(cancellationToken);

        if (!parents.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "В этом классе нет привязанных родителей.", cancellationToken: cancellationToken);
            return;
        }

        var lines = new List<string> { $"Родители класса (ID {classId}):" };
        foreach (var p in parents)
        {
            var fio = p.FullName ?? $"{p.FirstName} {p.LastName}".Trim();
            var phone = string.IsNullOrWhiteSpace(p.PhoneNumber) ? "не указан" : p.PhoneNumber;
            lines.Add($"• {fio} | {phone} | TG ID: {p.TelegramUserId}");
        }

        var text = string.Join("\n", lines);
        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }
}

public class UserState
{
    public VerificationStep Step { get; set; }
    public string? FullName { get; set; }
    public string? PhoneNumber { get; set; }
    public int? ClassId { get; set; }
    public NewsType? NewsType { get; set; }
    public string? NewsTitle { get; set; }
    public string? NewsContent { get; set; }
    public ClassAction ClassAction { get; set; } = ClassAction.None;
}

public enum VerificationStep
{
    WaitingForFullName,
    WaitingForPhoneNumber,
    WaitingForNewsType,
    WaitingForNewsTitle,
    WaitingForNewsContent,
    WaitingForClassNameCreate,
    WaitingForClassNameRequest
}

public enum ClassAction
{
    None,
    CreateClass,
    RequestClass
}