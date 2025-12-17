using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
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
            text += "/createclass - Создать класс (до 10 шт.)\n";
            text += "/deleteclass - Удалить класс (выбор из списка)\n";
            text += "/createnews - Создать новость/отчет (выбор класса, для отчёта нужен файл)\n";
            text += "/viewnews - Посмотреть новости (пагинация, выбор класса)\n";
            text += "/viewreports - Посмотреть отчёты (пагинация, выбор класса, скачивание по кнопке)\n";
            text += "/myclass - Посмотреть все классы, где вы админ/модератор/родитель\n";
            text += "/requestclass - Подать заявку на класс (как родитель)\n";
            text += "/verifications - Заявки на верификацию (выбор класса)\n";
            text += "/moderators - Список модераторов (выбор класса)\n";
            text += "/addmoderator - Добавить модератора (выбор класса -> ввод ID)\n";
            text += "/removemoderator - Удалить права модератора (выбор класса -> ввод ID)\n";
            text += "/parents - Список родителей/пользователей класса (выбор класса)\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
            text += "/help - Справка";
        }
        else if (user.Role == UserRole.Moderator)
        {
            text += "Вы модератор класса.\n\n";
            text += "Доступные команды:\n";
            text += "/createclass - Создать класс (до 10 шт.)\n";
            text += "/createnews - Создать новость или отчет\n";
            text += "/verifications - Просмотреть заявки на верификацию\n";
            text += "/addmoderator - Добавить модератора\n";
            text += "/viewnews - Просмотреть новости и отчеты\n";
            text += "/viewreports - Просмотреть отчёты (пагинация, выбор класса, скачивание)\n";
            text += "/myclass - Мои классы\n";
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
                text += "/viewreports - Просмотреть отчёты (пагинация, выбор класса, скачивание)\n";
                text += "/myclass - Мои классы\n";
                text += "/help - Справка";
            }
            else
            {
                text += "Вы зарегистрированы. Чтобы привязаться к классу, отправьте:\n";
                text += "/requestclass \n";
                text += "Или создайте свой класс: /createclass \n";
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
            text += "/createclass - Создать класс (до 10 шт.)\n";
            text += "/deleteclass - Удалить класс (выбор из списка)\n";
            text += "/createnews - Создать новость/отчет (выбор класса, для отчёта нужен файл)\n";
            text += "/viewnews - Посмотреть новости (пагинация, выбор класса)\n";
            text += "/viewreports - Посмотреть отчёты (пагинация, выбор класса, скачивание по кнопке)\n";
            text += "/myclass - Посмотреть все классы, где вы админ/модератор/родитель\n";
            text += "/requestclass - Подать заявку на класс (как родитель)\n";
            text += "/verifications - Заявки на верификацию (выбор класса)\n";
            text += "/moderators - Список модераторов (выбор класса)\n";
            text += "/addmoderator - Добавить модератора (выбор класса -> ввод ID)\n";
            text += "/removemoderator - Удалить права модератора (выбор класса -> ввод ID)\n";
            text += "/parents - Список родителей/пользователей класса (выбор класса)\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
        }
        else if (user.Role == UserRole.Moderator)
        {
            text += "Команды модератора:\n";
            text += "/createnews - Создать новость или отчет\n";
            text += "/viewnews - Посмотреть новости (пагинация, выбор класса)\n";
            text += "/viewreports - Посмотреть отчёты (пагинация, выбор класса, скачивание)\n";
            text += "/verifications - Просмотреть заявки на верификацию родителей (если админ)\n";
            text += "/addmoderator - Добавить модератора (в свой класс)\n";
            text += "/adminpanel - Ссылка на веб-админ-панель\n";
        }
        else if (user.Role == UserRole.Parent && user.IsVerified)
        {
            text += "Команды родителя:\n";
            text += "/viewnews - Просмотреть новости (пагинация, выбор класса)\n";
            text += "/viewreports - Просмотреть отчёты (пагинация, выбор класса, скачивание)\n";
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

        if (user.Role == UserRole.Admin || user.Role == UserRole.Moderator)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы уже обладаете правами администратора/модератора.",
                cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState { Step = VerificationStep.WaitingForFullName };

        await _botClient.SendTextMessageAsync(
            chatId,
            "Для регистрации в качестве родителя необходимо пройти верификацию.\n\n" +
            "Пожалуйста, введите ваше ФИО (Фамилия Имя Отчество):",
            cancellationToken: cancellationToken);
    }
}

