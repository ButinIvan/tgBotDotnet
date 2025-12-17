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
    private async Task DeletePromptMessage(long chatId, int? messageId, CancellationToken cancellationToken)
    {
        if (messageId is null) return;
        try { await _botClient.DeleteMessageAsync(chatId, messageId.Value, cancellationToken); }
        catch { /* ignore */ }
    }

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
                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Тип публикации не требуется. Используйте /createnews заново.", cancellationToken: cancellationToken);
            }
            else if (data.StartsWith("news_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var state = _userStates.GetValueOrDefault(userId);
                if (state == null)
                {
                    state = new UserState();
                    _userStates[userId] = state;
                }

                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                        // Проверка, что пользователь имеет отношение к классу (админ или модератор)
                        var hasAccess = await _dbContext.Classes.AnyAsync(c =>
                            c.Id == classId &&
                            (c.AdminTelegramUserId == user.TelegramUserId ||
                             (user.Role == UserRole.Moderator && user.ClassId == classId)), cancellationToken);
                        if (!hasAccess)
                        {
                            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Нет доступа к этому классу", cancellationToken: cancellationToken);
                            return;
                        }

                    state.ClassId = classId;
                    state.Step = VerificationStep.WaitingForNewsTitle;
                    state.NewsType = NewsType.News;
                    state.ClassAction = ClassAction.None;

                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                    await _botClient.SendTextMessageAsync(
                        chatId,
                        "Введите заголовок:",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("news_prev_") || data.StartsWith("news_next_"))
            {
                var partsCb = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
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
            else if (data.StartsWith("viewnews_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await SendNewsPage(chatId, 0, user.TelegramUserId, classId, 1, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("viewreports_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await SendReports(chatId, user.TelegramUserId, classId, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("reports_prev_") || data.StartsWith("reports_next_"))
            {
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && int.TryParse(parts[2], out var classId) && int.TryParse(parts[3], out var currentPage))
                {
                    var nextPage = data.StartsWith("reports_prev_") ? currentPage - 1 : currentPage + 1;
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await SendReportsPage(chatId, callbackQuery.Message?.MessageId ?? 0, userId, classId, nextPage, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка пагинации", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("report_dl_"))
            {
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var reportId))
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);

                    var report = await _dbContext.News.FirstOrDefaultAsync(n => n.Id == reportId && n.Type == NewsType.Report, cancellationToken);
                    if (report == null)
                    {
                        await _botClient.SendTextMessageAsync(chatId, "Отчет не найден.", cancellationToken: cancellationToken);
                        return;
                    }

                    var user = await GetOrCreateUser(userId, callbackQuery.From);
                    var hasAccess = (user.Role == UserRole.Admin && await _dbContext.Classes.AnyAsync(c => c.Id == report.ClassId && c.AdminTelegramUserId == user.TelegramUserId, cancellationToken))
                                    || (user.Role == UserRole.Moderator && user.ClassId == report.ClassId)
                                    || (user.Role == UserRole.Parent && user.IsVerified && (
                                        user.ClassId == report.ClassId ||
                                        await _dbContext.ParentClassLinks.AnyAsync(l => l.UserId == user.Id && l.ClassId == report.ClassId, cancellationToken)
                                    ));
                    if (!hasAccess)
                    {
                        await _botClient.SendTextMessageAsync(chatId, "Нет доступа к этому отчету.", cancellationToken: cancellationToken);
                        return;
                    }

                    var localDate = report.CreatedAt.AddHours(3);
                    var fileName = string.IsNullOrWhiteSpace(report.FileName) ? "report" : report.FileName;
                    var caption = $"<b>{report.Title}</b>\n\n{fileName}\n\nДата: {localDate:dd.MM.yyyy HH:mm}";

                    if (!string.IsNullOrEmpty(report.FilePath))
                    {
                        var url = await _s3Repository.GetPresignedUrlAsync(report.FilePath);
                        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            var doc = InputFile.FromUri(url);
                            await _botClient.SendDocumentAsync(
                                chatId,
                                document: doc,
                                caption: caption,
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        try
                        {
                            await using var stream = await _s3Repository.GetObjectStreamAsync(report.FilePath);
                            var doc = InputFile.FromStream(stream, fileName);
                            await _botClient.SendDocumentAsync(
                                chatId,
                                document: doc,
                                caption: caption,
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                cancellationToken: cancellationToken);
                            return;
                        }
                        catch
                        {
                            // fall back to text
                        }
                    }

                    await _botClient.SendTextMessageAsync(
                        chatId,
                        caption,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка скачивания", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("delete_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await HandleDeleteClassById(user, classId, callbackQuery, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("modclass_add_") || data.StartsWith("modclass_remove_") || data.StartsWith("modclass_list_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    var action = parts[1];
                    switch (action)
                    {
                        case "add":
                            if (_userStates.TryGetValue(userId, out var stAdd) && stAdd.Step == VerificationStep.WaitingForModeratorClass)
                            {
                                stAdd.ClassId = classId;
                                stAdd.Step = VerificationStep.WaitingForModeratorUserIdAdd;
                                stAdd.ClassAction = ClassAction.AddModerator;
                                stAdd.PromptMessageId = null;
                            }
                            else
                            {
                                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Сессия устарела. Начните заново.", cancellationToken: cancellationToken);
                                return;
                            }
                            await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                            await _botClient.SendTextMessageAsync(user.TelegramUserId, "Введите Telegram ID пользователя:", cancellationToken: cancellationToken);
                            break;
                        case "remove":
                            if (_userStates.TryGetValue(userId, out var stRem) && stRem.Step == VerificationStep.WaitingForModeratorClass)
                            {
                                stRem.ClassId = classId;
                                stRem.Step = VerificationStep.WaitingForModeratorUserIdRemove;
                                stRem.ClassAction = ClassAction.RemoveModerator;
                                stRem.PromptMessageId = null;
                            }
                            else
                            {
                                await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Сессия устарела. Начните заново.", cancellationToken: cancellationToken);
                                return;
                            }
                            await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                            await _botClient.SendTextMessageAsync(user.TelegramUserId, "Введите Telegram ID модератора:", cancellationToken: cancellationToken);
                            break;
                        case "list":
                            _userStates.TryRemove(user.TelegramUserId, out _);
                            await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                            await HandleListModeratorsForClass(classId, user.TelegramUserId, cancellationToken);
                            break;
                        default:
                            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка", cancellationToken: cancellationToken);
                            break;
                    }
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("parents_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    _userStates.TryRemove(user.TelegramUserId, out _);
                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await ShowParentsForClass(user, classId, user.TelegramUserId, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
            }
            else if (data.StartsWith("verif_class_"))
            {
                var user = await GetOrCreateUser(userId, callbackQuery.From);
                var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[2], out var classId))
                {
                    _userStates.TryRemove(user.TelegramUserId, out _);
                    await DeletePromptMessage(chatId, callbackQuery.Message?.MessageId, cancellationToken);
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
                    await ShowVerificationsForClass(user, classId, user.TelegramUserId, cancellationToken);
                }
                else
                {
                    await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Ошибка выбора класса", cancellationToken: cancellationToken);
                }
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
}

