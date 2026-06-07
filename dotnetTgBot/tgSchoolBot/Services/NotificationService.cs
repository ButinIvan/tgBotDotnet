using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Services;

public class NotificationService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        ITelegramBotClient botClient,
        ApplicationDbContext dbContext,
        ILogger<NotificationService> logger)
    {
        _botClient = botClient;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task NotifyParentsAboutNewNews(News news)
    {
        var parents = await _dbContext.Users
            .Where(u => u.ClassId == news.ClassId && u.Role == UserRole.Parent && u.IsVerified)
            .ToListAsync();

        var typeText = news.Type == NewsType.News ? "Новость" : "Отчет";
        var title = TelegramHtml.Encode(news.Title);
        var content = TelegramHtml.Encode(news.Content);
        var message = $"{typeText}\n\n" +
                     $"<b>{title}</b>\n\n" +
                     $"{content}\n\n" +
                     $"Дата: {AppDateTime.Format(news.CreatedAt)}";

        foreach (var parent in parents)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    parent.TelegramUserId,
                    message,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send notification to parent {TelegramUserId}", parent.TelegramUserId);
            }
        }
    }
}
