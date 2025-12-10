using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using Microsoft.EntityFrameworkCore;

namespace dotnetTgBot.Services;

public class TelegramBotService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        IOptions<TelegramBotOptions> options,
        IServiceProvider serviceProvider,
        ILogger<TelegramBotService> logger)
    {
        _botClient = new TelegramBotClient(options.Value.BotToken);
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        var me = await _botClient.GetMeAsync(stoppingToken);
        _logger.LogInformation($"Бот @{me.Username} запущен");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var handlerLogger = loggerFactory.CreateLogger<UpdateHandler>();
            var handler = new UpdateHandler(botClient, dbContext, handlerLogger);

            await handler.HandleUpdate(update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в HandleUpdateAsync");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Ошибка при обработке обновлений");
        return Task.CompletedTask;
    }
}

public class TelegramBotOptions
{
    public string BotToken { get; set; } = string.Empty;
}

