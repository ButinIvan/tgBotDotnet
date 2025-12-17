using Telegram.Bot;
using Telegram.Bot.Types;
using System.IO;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Interfaces;
using Telegram.Bot.Types;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
    private async Task HandleCreateNewsCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        // Собираем классы, где пользователь админ или модератор
        var adminClasses = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);

        var moderatorClasses = new List<(int Id, string Name)>();
        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
        {
            var modClass = await _dbContext.Classes
                .Where(c => c.Id == user.ClassId.Value)
                .Select(c => new { c.Id, c.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (modClass != null)
            {
                moderatorClasses.Add((modClass.Id, modClass.Name));
            }
        }

        var classes = adminClasses
            .Select(c => (c.Id, c.Name))
            .Concat(moderatorClasses)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ToList();

        if (!classes.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "У вас нет классов для публикации (нужно быть админом или модератором).",
                cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForNewsClass,
            ClassId = null
        };

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"news_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var keyboard = new InlineKeyboardMarkup(buttons);

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для публикации:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task StartViewNewsFlow(AppUser user, long chatId, CancellationToken cancellationToken)
    {
        var classIds = new HashSet<int>();

        if (user.Role == UserRole.Admin)
        {
            var adminIds = await _dbContext.Classes
                .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in adminIds) classIds.Add(id);
        }

        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
            classIds.Add(user.ClassId.Value);

        if (user.Role == UserRole.Parent && user.IsVerified)
        {
            if (user.ClassId.HasValue) classIds.Add(user.ClassId.Value);
            var linkIds = await _dbContext.ParentClassLinks
                .Where(l => l.UserId == user.Id)
                .Select(l => l.ClassId)
                .ToListAsync(cancellationToken);
            foreach (var id in linkIds) classIds.Add(id);
        }

        if (!classIds.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступных классов для просмотра новостей.", cancellationToken: cancellationToken);
            return;
        }

        var classes = await _dbContext.Classes
            .Where(c => classIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"viewnews_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для просмотра новостей:",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task StartViewReportsFlow(AppUser user, long chatId, CancellationToken cancellationToken)
    {
        var classIds = new HashSet<int>();

        if (user.Role == UserRole.Admin)
        {
            var adminIds = await _dbContext.Classes
                .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in adminIds) classIds.Add(id);
        }

        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
            classIds.Add(user.ClassId.Value);

        if (user.Role == UserRole.Parent && user.IsVerified)
        {
            if (user.ClassId.HasValue) classIds.Add(user.ClassId.Value);
            var linkIds = await _dbContext.ParentClassLinks
                .Where(l => l.UserId == user.Id)
                .Select(l => l.ClassId)
                .ToListAsync(cancellationToken);
            foreach (var id in linkIds) classIds.Add(id);
        }

        if (!classIds.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступных классов для просмотра отчетов.", cancellationToken: cancellationToken);
            return;
        }

        var classes = await _dbContext.Classes
            .Where(c => classIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"viewreports_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для просмотра отчетов:",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
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

        const int pageSize = 5;
        const int maxPages = 10;
        var newsQuery = _dbContext.News
            .Where(n => n.ClassId == targetClass.Id && n.Type == NewsType.News)
            .OrderByDescending(n => n.CreatedAt);

        var totalCount = await newsQuery.CountAsync(cancellationToken);
        var pages = (int)Math.Ceiling(totalCount / (double)pageSize);
        pages = Math.Min(pages, maxPages);

        if (pages == 0)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пока нет новостей или отчетов.", cancellationToken: cancellationToken);
            return;
        }

        var news = await newsQuery
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var startIdx = (1 - 1) * pageSize;
        var pageText = string.Join("\n\n", news.Select((item, idx) =>
        {
            var localDate = item.CreatedAt.AddHours(3);
            var content = (item.Content ?? string.Empty).TrimEnd();
            return $"<b>{startIdx + idx + 1}. {item.Title}</b>\n\n{content}\n\nДата: {localDate:dd.MM.yyyy HH:mm}";
        }));

        var keyboard = BuildNewsPaginationKeyboard(targetClass.Id, 1, pages);

        await _botClient.SendTextMessageAsync(
            chatId,
            pageText,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private async Task HandleViewReportsCommand(AppUser user, string[] parts, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (parts.Length < 2)
        {
            await _botClient.SendTextMessageAsync(chatId, "Использование: /viewreports <Название класса>", cancellationToken: cancellationToken);
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

        var hasAccess = (user.Role == UserRole.Admin && targetClass.AdminTelegramUserId == user.TelegramUserId)
                        || (user.Role == UserRole.Moderator && user.ClassId == targetClass.Id)
                        || (user.Role == UserRole.Parent && user.IsVerified && (
                            user.ClassId == targetClass.Id ||
                            await _dbContext.ParentClassLinks.AnyAsync(l => l.UserId == user.Id && l.ClassId == targetClass.Id, cancellationToken)
                        ));

        if (!hasAccess)
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа к отчетам этого класса.", cancellationToken: cancellationToken);
            return;
        }

        await SendReports(chatId, user.TelegramUserId, targetClass.Id, cancellationToken);
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

    private async Task SendNewsPage(long chatId, int messageId, long userTelegramId, int classId, int page, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        const int maxPages = 10;

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
            .Where(n => n.ClassId == classId && n.Type == NewsType.News)
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

        var startIdx = skip;
        var pageText = string.Join("\n\n", news.Select((item, idx) =>
        {
            var localDate = item.CreatedAt.AddHours(3);
            var content = (item.Content ?? string.Empty).TrimEnd();
            return $"<b>{startIdx + idx + 1}. {item.Title}</b>\n\n{content}\n\nДата: {localDate:dd.MM.yyyy HH:mm}";
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

    private async Task SendReports(long chatId, long userTelegramId, int classId, CancellationToken cancellationToken)
    {
        await SendReportsPage(chatId, 0, userTelegramId, classId, 1, cancellationToken);
    }

    private InlineKeyboardMarkup BuildReportsKeyboard(List<News> reports, int classId, int page, int totalPages)
    {
        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var r in reports)
        {
            buttons.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"⬇️ {r.Title}", $"report_dl_{r.Id}")
            });
        }

        var nav = new List<InlineKeyboardButton>();
        if (page > 1) nav.Add(InlineKeyboardButton.WithCallbackData("« Пред", $"reports_prev_{classId}_{page}"));
        if (page < totalPages) nav.Add(InlineKeyboardButton.WithCallbackData("След »", $"reports_next_{classId}_{page}"));
        if (nav.Any()) buttons.Add(nav);

        return new InlineKeyboardMarkup(buttons);
    }

    private async Task SendReportsPage(long chatId, int messageId, long userTelegramId, int classId, int page, CancellationToken cancellationToken)
    {
        const int pageSize = 5;
        const int maxPages = 10;

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
            await _botClient.SendTextMessageAsync(chatId, "У вас нет доступа к отчетам этого класса.", cancellationToken: cancellationToken);
            return;
        }

        var query = _dbContext.News
            .Where(n => n.ClassId == classId && n.Type == NewsType.Report)
            .OrderByDescending(n => n.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            await _botClient.SendTextMessageAsync(chatId, "Отчеты отсутствуют.", cancellationToken: cancellationToken);
            return;
        }

        var totalPages = Math.Max(1, Math.Min((int)Math.Ceiling(total / (double)pageSize), maxPages));
        page = Math.Max(1, Math.Min(page, totalPages));
        var skip = (page - 1) * pageSize;

        var reports = await query.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);

        var startIdx = skip;
        var text = string.Join("\n\n", reports.Select((r, idx) =>
        {
            var localDate = r.CreatedAt.AddHours(3);
            var fileName = string.IsNullOrWhiteSpace(r.FileName) ? "report" : r.FileName;
            return $"{startIdx + idx + 1}. <b>{r.Title}</b>\n{fileName}\nДата: {localDate:dd.MM.yyyy HH:mm}";
        }));

        var keyboard = BuildReportsKeyboard(reports, classId, page, totalPages);

        if (messageId > 0)
        {
            await _botClient.EditMessageTextAsync(
                chatId,
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

}
