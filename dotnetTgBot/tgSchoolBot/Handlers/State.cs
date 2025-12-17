using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
    private async Task HandleUserState(AppUser user, UserState state, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        switch (state.Step)
        {
            case VerificationStep.WaitingForFullName:
                var fullNameInput = message.Text?.Trim() ?? string.Empty;
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

                _userStates.TryRemove(user.TelegramUserId, out _);

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
                    "Теперь введите содержание новости:",
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

                if (state.ClassId.HasValue && state.NewsType.HasValue && state.NewsTitle != null)
                {
                    var news = new News
                    {
                        ClassId = state.ClassId.Value,
                        AuthorTelegramUserId = user.TelegramUserId,
                        Title = state.NewsTitle,
                        Content = state.NewsContent,
                        Type = NewsType.News,
                        CreatedAt = DateTime.UtcNow
                    };

                    _dbContext.News.Add(news);
                    await _dbContext.SaveChangesAsync();

                    await _newsQueueProducer.EnqueueNewsAsync(news, cancellationToken);

                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "✅ Новость поставлена в очередь на отправку!",
                        cancellationToken: cancellationToken);
                }

                _userStates.TryRemove(user.TelegramUserId, out _);
                break;

            case VerificationStep.WaitingForClassNameCreate:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Введите название класса.", cancellationToken: cancellationToken);
                    return;
                }

                var classNameCreate = message.Text.Trim();
                if (!IsValidClassName(classNameCreate))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Некорректное название. Разрешены только буквы и цифры, один символ '-', и название должно начинаться с цифры.",
                        cancellationToken: cancellationToken);
                    return;
                }

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
                await _dbContext.SaveChangesAsync();

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
                if (!IsValidClassName(classNameRequest))
                {
                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Некорректное название. Разрешены только буквы и цифры, один символ '-', и название должно начинаться с цифры.",
                        cancellationToken: cancellationToken);
                    return;
                }
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

            case VerificationStep.WaitingForModeratorUserIdAdd:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Введите Telegram ID пользователя.", cancellationToken: cancellationToken);
                    return;
                }
                if (!long.TryParse(message.Text.Trim(), out var targetUserIdAdd))
                {
                    await _botClient.SendTextMessageAsync(chatId, "ID должен быть числом.", cancellationToken: cancellationToken);
                    return;
                }
                if (!state.ClassId.HasValue)
                {
                    await _botClient.SendTextMessageAsync(chatId, "Сначала выберите класс.", cancellationToken: cancellationToken);
                    return;
                }

                await AddModeratorToClass(user, state.ClassId.Value, targetUserIdAdd, chatId, cancellationToken);
                _userStates.TryRemove(user.TelegramUserId, out _);
                break;

            case VerificationStep.WaitingForModeratorUserIdRemove:
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Введите Telegram ID модератора.", cancellationToken: cancellationToken);
                    return;
                }
                if (!long.TryParse(message.Text.Trim(), out var targetUserIdRem))
                {
                    await _botClient.SendTextMessageAsync(chatId, "ID должен быть числом.", cancellationToken: cancellationToken);
                    return;
                }
                if (!state.ClassId.HasValue)
                {
                    await _botClient.SendTextMessageAsync(chatId, "Сначала выберите класс.", cancellationToken: cancellationToken);
                    return;
                }

                await RemoveModeratorFromClass(user, state.ClassId.Value, targetUserIdRem, chatId, cancellationToken);
                _userStates.TryRemove(user.TelegramUserId, out _);
                break;
        }
    }
}

