using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Events;
using dotnetTgBot.Persistence;
using dotnetTgBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace dotnetTgBot.Services;

public class NewsQueueConsumer : BackgroundService
{
    private readonly IRabbitMqService _rabbit;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NewsQueueConsumer> _logger;

    public NewsQueueConsumer(IRabbitMqService rabbit, IServiceProvider serviceProvider, ILogger<NewsQueueConsumer> logger)
    {
        _rabbit = rabbit;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new AsyncEventingBasicConsumer(_rabbit.Channel);
        consumer.Received += async (model, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var json = Encoding.UTF8.GetString(body);
                var message = JsonSerializer.Deserialize<NewsQueueMessage>(json);
                if (message == null)
                {
                    _rabbit.Channel.BasicAck(ea.DeliveryTag, multiple: false);
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var bot = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<NewsQueueConsumer>>();

                if (string.Equals(message.Type, NewsType.News.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    await SendNewsAsync(db, bot, logger, message, stoppingToken);
                }

                _rabbit.Channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing news queue message");
                if (ea.Redelivered)
                {
                    _logger.LogError("News queue message failed after retry and will be rejected. DeliveryTag: {DeliveryTag}", ea.DeliveryTag);
                    _rabbit.Channel.BasicReject(ea.DeliveryTag, requeue: false);
                }
                else
                {
                    _rabbit.Channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
                }
            }
        };

        _rabbit.Channel.BasicConsume(
            queue: _rabbit.QueueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer);

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static async Task SendNewsAsync(ApplicationDbContext db, ITelegramBotClient bot, ILogger logger, NewsQueueMessage message, CancellationToken cancellationToken)
    {
        var parentIdsFromLinks = await db.ParentClassLinks
            .Where(l => l.ClassId == message.ClassId)
            .Select(l => l.UserId)
            .ToListAsync(cancellationToken);

        var classAdminTgId = await db.Classes
            .Where(c => c.Id == message.ClassId)
            .Select(c => c.AdminTelegramUserId)
            .FirstOrDefaultAsync(cancellationToken);

        var recipients = await db.Users
            .Where(u =>
                (
                    (u.Role == UserRole.Parent && u.IsVerified &&
                     (u.ClassId == message.ClassId || parentIdsFromLinks.Contains(u.Id)))
                    || (u.Role == UserRole.Admin && (u.ClassId == message.ClassId || u.TelegramUserId == classAdminTgId))
                    || (u.Role == UserRole.Moderator && u.ClassId == message.ClassId)
                ))
            .Select(u => u.TelegramUserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var content = (message.Content ?? string.Empty).TrimEnd();
        var text = $"<b>{message.Title}</b>\n\n{content}\n\nДата: {AppDateTime.Format(message.CreatedAtUtc)}";

        foreach (var tgId in recipients)
        {
            try
            {
                await bot.SendTextMessageAsync(
                    tgId,
                    text,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send news to Telegram user {TelegramUserId}", tgId);
            }
        }
    }
}

