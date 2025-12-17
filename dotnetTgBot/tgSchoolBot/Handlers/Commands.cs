using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
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

            case "/viewreports":
                if ((user.Role == UserRole.Admin || user.Role == UserRole.Moderator) || (user.Role == UserRole.Parent && user.IsVerified))
                {
                    if (parts.Length < 2)
                        await StartViewReportsFlow(user, chatId, cancellationToken);
                    else
                        await HandleViewReportsCommand(user, parts, message, cancellationToken);
                }
                else
                {
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа.", cancellationToken: cancellationToken);
                }
                break;

            case "/viewnews":
                if ((user.Role == UserRole.Admin || user.Role == UserRole.Moderator) || (user.Role == UserRole.Parent && user.IsVerified))
                {
                    if (parts.Length < 2)
                        await StartViewNewsFlow(user, chatId, cancellationToken);
                    else
                        await HandleViewNewsCommand(user, parts, message, cancellationToken);
                }
                else
                {
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа. Пожалуйста, пройдите верификацию.", cancellationToken: cancellationToken);
                }
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
                    await StartVerificationsFlow(user, chatId, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/parents":
                if (user.Role == UserRole.Admin)
                    await StartParentsFlow(user, chatId, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/moderators":
                if (user.Role == UserRole.Admin)
                    await StartModeratorsFlow(user, ClassAction.ListModerators, chatId, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/deleteclass":
                if (user.Role == UserRole.Admin)
                    await HandleDeleteClassCommand(user, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/addmoderator":
                if (user.Role == UserRole.Admin || user.Role == UserRole.Moderator)
                    await StartModeratorsFlow(user, ClassAction.AddModerator, chatId, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/removemoderator":
                if (user.Role == UserRole.Admin)
                    await StartModeratorsFlow(user, ClassAction.RemoveModerator, chatId, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            case "/adminpanel":
                if (user.Role == UserRole.Admin || user.Role == UserRole.Moderator)
                    await HandleAdminPanelCommand(user, message, cancellationToken);
                else
                    await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.", cancellationToken: cancellationToken);
                break;

            default:
                await _botClient.SendTextMessageAsync(chatId, "Неизвестная команда. Введите /help для списка команд.", cancellationToken: cancellationToken);
                break;
        }
    }
}

