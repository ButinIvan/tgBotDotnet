using System.Linq;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;
using dotnetTgBot.Interfaces;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly INewsQueueProducer _newsQueueProducer;
    private readonly IS3Repository _s3Repository;
    // Статус пользователей между сообщениями
    private static readonly ConcurrentDictionary<long, UserState> _userStates = new();

    private sealed class ClassInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    public UpdateHandler(
        ITelegramBotClient botClient,
        ApplicationDbContext dbContext,
        ILogger<UpdateHandler> logger,
        INewsQueueProducer newsQueueProducer,
        IS3Repository s3Repository)
    {
        _botClient = botClient;
        _dbContext = dbContext;
        _logger = logger;
        _newsQueueProducer = newsQueueProducer;
        _s3Repository = s3Repository;
    }

    public async Task HandleUpdate(Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQuery(callbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { } message)
                return;

            var chatId = message.Chat.Id;
            var userId = message.From?.Id ?? 0;

            var user = await GetOrCreateUser(userId, message.From);

            // Команды имеют приоритет над любыми незавершенными шагами
            if (message.Text is { } text && text.StartsWith('/'))
            {
                if (_userStates.TryRemove(userId, out var prevState) && prevState?.PromptMessageId is int msgId)
                {
                    try { await _botClient.DeleteMessageAsync(chatId, msgId, cancellationToken); } catch { /* ignore */ }
                }

                await HandleCommand(user, text, message, cancellationToken);
                return;
            }

            if (_userStates.TryGetValue(userId, out var state))
            {
                await HandleUserState(user, state, message, cancellationToken);
                return;
            }

            // авто-старт регистрации если нет ФИО/телефона
            if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.PhoneNumber))
            {
                var newState = new UserState { Step = VerificationStep.WaitingForFullName };
                _userStates[user.TelegramUserId] = newState;
                await HandleUserState(user, newState, message, cancellationToken);
                return;
            }

            await _botClient.SendTextMessageAsync(
                chatId,
                "Используйте команды для взаимодействия с ботом. Введите /help для списка команд.",
                cancellationToken: cancellationToken);
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

    private bool IsValidClassName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (!char.IsDigit(name[0]))
            return false;

        var dashCount = 0;
        foreach (var ch in name)
        {
            if (ch == '-')
            {
                dashCount++;
                if (dashCount > 1) return false;
                continue;
            }

            if (!char.IsLetterOrDigit(ch))
                return false;
        }

        return true;
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
    public int? PromptMessageId { get; set; }
}

public enum VerificationStep
{
    WaitingForFullName,
    WaitingForPhoneNumber,
    WaitingForNewsClass,
    WaitingForNewsType,
    WaitingForNewsTitle,
    WaitingForNewsContent,
    WaitingForClassNameCreate,
    WaitingForClassNameRequest,
    WaitingForDeleteClass,
    WaitingForModeratorClass,
    WaitingForModeratorUserIdAdd,
    WaitingForModeratorUserIdRemove,
    WaitingForParentsClass,
    WaitingForVerificationsClass
}

public enum ClassAction
{
    None,
    CreateClass,
    RequestClass,
    DeleteClass,
    AddModerator,
    RemoveModerator,
    ListModerators,
    ParentsInfo,
    VerificationsInfo
}