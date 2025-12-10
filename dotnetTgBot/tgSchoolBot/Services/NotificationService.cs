using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

        var typeText = news.Type == NewsType.News ? "📰 Новость" : "📊 Отчет";
        var message = $"{typeText}\n\n" +
                     $"<b>{news.Title}</b>\n\n" +
                     $"{news.Content}\n\n" +
                     $"Дата: {news.CreatedAt:dd.MM.yyyy HH:mm}";

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
                _logger.LogError(ex, $"Не удалось отправить новость родителю {parent.TelegramUserId}");
            }
        }
    }
}

