using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;
using System.Linq;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
    private async Task StartParentsFlow(AppUser user, long chatId, CancellationToken cancellationToken)
    {
        var classes = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        if (!classes.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет классов для отображения родителей.", cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForParentsClass,
            ClassAction = ClassAction.ParentsInfo
        };

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"parents_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для просмотра родителей:",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task ShowParentsForClass(AppUser user, int classId, long chatId, CancellationToken cancellationToken)
    {
        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не админ этого класса.", cancellationToken: cancellationToken);
            return;
        }

        var linkIds = await _dbContext.ParentClassLinks
            .Where(l => l.ClassId == classId)
            .Select(l => l.UserId)
            .ToListAsync(cancellationToken);

        var users = await _dbContext.Users
            .Where(u => u.ClassId == classId || linkIds.Contains(u.Id))
            .OrderBy(u => u.FullName ?? u.FirstName ?? u.Username)
            .ToListAsync(cancellationToken);

        if (!users.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "В этом классе нет привязанных родителей.", cancellationToken: cancellationToken);
            return;
        }

        var lines = new List<string> { $"Родители класса {targetClass.Name}:" };
        foreach (var p in users)
        {
            var fio = p.FullName ?? $"{p.FirstName} {p.LastName}".Trim();
            var phone = string.IsNullOrWhiteSpace(p.PhoneNumber) ? "не указан" : p.PhoneNumber;
            var roleText = p.Role switch
            {
                UserRole.Admin => "admin",
                UserRole.Moderator => "moderator",
                UserRole.Parent => "parent",
                _ => "unverified"
            };
            lines.Add($"• {fio} | {phone} | TG ID: {p.TelegramUserId} | role: {roleText}");
        }

        var text = string.Join("\n", lines);
        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }

    private async Task StartModeratorsFlow(AppUser user, ClassAction action, long chatId, CancellationToken cancellationToken)
    {
        var classes = await GetManageableClasses(user, cancellationToken);

        if (!classes.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет классов для управления.", cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForModeratorClass,
            ClassAction = action
        };

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, action switch
            {
                ClassAction.AddModerator => $"modclass_add_{c.Id}",
                ClassAction.RemoveModerator => $"modclass_remove_{c.Id}",
                ClassAction.ListModerators => $"modclass_list_{c.Id}",
                _ => $"modclass_unknown_{c.Id}"
            }))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var keyboard = new InlineKeyboardMarkup(buttons);

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            action switch
            {
                ClassAction.AddModerator => "Выберите класс, куда добавить модератора:",
                ClassAction.RemoveModerator => "Выберите класс, откуда удалить модератора:",
                ClassAction.ListModerators => "Выберите класс для просмотра модераторов:",
                _ => "Выберите класс:"
            },
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task<List<Class>> GetManageableClasses(AppUser user, CancellationToken cancellationToken)
    {
        var adminClasses = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .ToListAsync(cancellationToken);

        var moderatorClasses = new List<Class>();
        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
        {
            var modClass = await _dbContext.Classes
                .Where(c => c.Id == user.ClassId.Value)
                .ToListAsync(cancellationToken);
            moderatorClasses.AddRange(modClass);
        }

        return adminClasses.Concat(moderatorClasses)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ToList();
    }

    private async Task HandleListModeratorsForClass(int classId, long chatId, CancellationToken cancellationToken)
    {
        var moderators = await _dbContext.Users
            .Where(u => u.ClassId == classId && u.Role == UserRole.Moderator)
            .ToListAsync(cancellationToken);

        if (!moderators.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "В выбранном классе нет модераторов.",
                cancellationToken: cancellationToken);
            return;
        }

        var text = "Модераторы класса:\n\n";
        foreach (var moderator in moderators)
        {
            text += $"• {moderator.FirstName ?? "Не указано"} {moderator.LastName ?? ""}\n";
            text += $"  ID: {moderator.TelegramUserId}\n";
            text += $"  Username: @{moderator.Username ?? "не указан"}\n\n";
        }

        await _botClient.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
    }

    private async Task HandleAdminPanelCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        var baseUrl = Environment.GetEnvironmentVariable("ADMIN_PANEL_URL") ?? "http://localhost:5010";
        var loginUrl = $"{baseUrl}/Account/Login";

        var text = "Админ-панель\n\n";
        text += "Для доступа к веб-интерфейсу:\n";
        text += $"Перейдите по ссылке: {loginUrl} и введите ваш Telegram User ID: " + user.TelegramUserId;

        await _botClient.SendTextMessageAsync(
            chatId,
            text,
            cancellationToken: cancellationToken);
    }

    private async Task HandleMyClassCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        var adminClasses = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Админ" })
            .ToListAsync(cancellationToken);

        var moderatorClasses = new List<ClassInfo>();
        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
        {
            moderatorClasses = await _dbContext.Classes
                .Where(c => c.Id == user.ClassId.Value)
                .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Модератор" })
                .ToListAsync(cancellationToken);
        }

        var parentClassIds = new List<int>();
        if (user.ClassId.HasValue)
            parentClassIds.Add(user.ClassId.Value);

        var linkIds = await _dbContext.ParentClassLinks
            .Where(l => l.UserId == user.Id)
            .Select(l => l.ClassId)
            .ToListAsync(cancellationToken);
        parentClassIds.AddRange(linkIds);
        parentClassIds = parentClassIds.Distinct().ToList();

        var parentClasses = await _dbContext.Classes
            .Where(c => parentClassIds.Contains(c.Id))
            .Select(c => new ClassInfo { Id = c.Id, Name = c.Name, Role = "Родитель" })
            .ToListAsync(cancellationToken);

        var all = adminClasses
            .Concat(moderatorClasses)
            .Concat(parentClasses)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ToList();

        if (!all.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Вы не привязаны ни к одному классу.",
                cancellationToken: cancellationToken);
            return;
        }

        var response = "Ваши классы:\n" + string.Join("\n", all.Select(c => $"• {c.Name} ({c.Role})"));
        await _botClient.SendTextMessageAsync(chatId, response, cancellationToken: cancellationToken);
    }

    private async Task HandleDeleteClassCommand(AppUser user, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var adminClasses = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        if (!adminClasses.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "У вас нет классов для удаления.",
                cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForDeleteClass,
            ClassAction = ClassAction.DeleteClass
        };

        var buttons = adminClasses
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"delete_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var keyboard = new InlineKeyboardMarkup(buttons);

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для удаления:",
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task HandleDeleteClassById(AppUser user, int classId, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? 0;

        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Класс не найден или нет прав", cancellationToken: cancellationToken);
            return;
        }

        // Удаляем связанные записи
        var news = _dbContext.News.Where(n => n.ClassId == classId);
        var verifications = _dbContext.ParentVerifications.Where(v => v.ClassId == classId);
        var links = _dbContext.ParentClassLinks.Where(l => l.ClassId == classId);

        _dbContext.News.RemoveRange(news);
        _dbContext.ParentVerifications.RemoveRange(verifications);
        _dbContext.ParentClassLinks.RemoveRange(links);

        // Отвязываем всех пользователей, у кого этот класс как основной
        var users = await _dbContext.Users
            .Where(u => u.ClassId == classId)
            .ToListAsync(cancellationToken);
        foreach (var u in users)
        {
            u.ClassId = null;
        }

        // Удаляем сам класс
        _dbContext.Classes.Remove(targetClass);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Очистка состояния
        _userStates.TryRemove(user.TelegramUserId, out _);

        await _botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Класс удален", cancellationToken: cancellationToken);

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Класс '{targetClass.Name}' удален. Все родители и пользователи отвязаны от этого класса.",
            cancellationToken: cancellationToken);
    }

    private async Task AddModeratorToClass(AppUser adminUser, int classId, long targetUserId, long chatId, CancellationToken cancellationToken)
    {
        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && c.AdminTelegramUserId == adminUser.TelegramUserId, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не являетесь его админом.", cancellationToken: cancellationToken);
            return;
        }

        var targetUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == targetUserId, cancellationToken);

        if (targetUser == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пользователь не найден.", cancellationToken: cancellationToken);
            return;
        }
        if (targetUser.Role == UserRole.Admin)
        {
            await _botClient.SendTextMessageAsync(chatId, "Нельзя изменить роль администратора.", cancellationToken: cancellationToken);
            return;
        }

        if (targetUser.ClassId != classId)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пользователь не принадлежит выбранному классу.", cancellationToken: cancellationToken);
            return;
        }

        if (targetUser.Role == UserRole.Moderator)
        {
            await _botClient.SendTextMessageAsync(chatId, "Пользователь уже является модератором этого класса.", cancellationToken: cancellationToken);
            return;
        }

        targetUser.Role = UserRole.Moderator;
        targetUser.IsVerified = true;
        targetUser.ClassId = classId;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _botClient.SendTextMessageAsync(chatId, $"✅ Пользователь {targetUser.FirstName ?? "ID: " + targetUserId} теперь модератор.", cancellationToken: cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                targetUserId,
                $"✅ Вы получили права модератора в классе '{targetClass.Name}'.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить нового модератора");
        }
    }

    private async Task RemoveModeratorFromClass(AppUser adminUser, int classId, long targetUserId, long chatId, CancellationToken cancellationToken)
    {
        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && c.AdminTelegramUserId == adminUser.TelegramUserId, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не являетесь его админом.", cancellationToken: cancellationToken);
            return;
        }

        var targetUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == targetUserId && u.ClassId == classId, cancellationToken);

        if (targetUser == null || targetUser.Role != UserRole.Moderator)
        {
            await _botClient.SendTextMessageAsync(chatId, "Модератор не найден в выбранном классе.", cancellationToken: cancellationToken);
            return;
        }

        targetUser.Role = UserRole.Parent;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _botClient.SendTextMessageAsync(chatId, $"✅ Права модератора удалены у пользователя {targetUser.FirstName ?? "ID: " + targetUserId}.", cancellationToken: cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                targetUserId,
                $"❌ Ваши права модератора в классе '{targetClass.Name}' были удалены администратором.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить бывшего модератора");
        }
    }
}

