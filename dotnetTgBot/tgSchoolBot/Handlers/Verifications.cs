using Telegram.Bot.Types;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using BotUser = Telegram.Bot.Types.User;
using AppUser = dotnetTgBot.Models.User;

namespace dotnetTgBot.Services;

public partial class UpdateHandler
{
    private async Task StartVerificationsFlow(AppUser user, long chatId, CancellationToken cancellationToken)
    {
        var classes = await _dbContext.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        if (!classes.Any())
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет классов для верификаций.", cancellationToken: cancellationToken);
            return;
        }

        _userStates[user.TelegramUserId] = new UserState
        {
            Step = VerificationStep.WaitingForVerificationsClass,
            ClassAction = ClassAction.VerificationsInfo
        };

        var buttons = classes
            .Select(c => InlineKeyboardButton.WithCallbackData(c.Name, $"verif_class_{c.Id}"))
            .Chunk(2)
            .Select(chunk => chunk.ToArray())
            .ToArray();

        var msg = await _botClient.SendTextMessageAsync(
            chatId,
            "Выберите класс для просмотра заявок:",
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: cancellationToken);

        if (_userStates.TryGetValue(user.TelegramUserId, out var st))
            st.PromptMessageId = msg.MessageId;
    }

    private async Task ShowVerificationsForClass(AppUser user, int classId, long chatId, CancellationToken cancellationToken)
    {
        var targetClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId, cancellationToken);

        if (targetClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден или вы не админ этого класса.", cancellationToken: cancellationToken);
            return;
        }

        var pendingVerifications = await _dbContext.ParentVerifications
            .Where(v => v.Status == VerificationStatus.Pending && (v.ClassId == classId || v.ClassId == null))
            .OrderBy(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        if (!pendingVerifications.Any())
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                "Нет заявок на верификацию.",
                cancellationToken: cancellationToken);
            return;
        }

        foreach (var verification in pendingVerifications)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"approve_{verification.Id}_{classId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"reject_{verification.Id}")
                }
            });

            var text = $"Заявка #{verification.Id}\n\n" +
                      $"ФИО: {verification.FullName}\n" +
                      $"Телефон: {verification.PhoneNumber}\n" +
                      $"Дата: {verification.CreatedAt:dd.MM.yyyy HH:mm}";

            await _botClient.SendTextMessageAsync(
                chatId,
                text,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleApproveVerification(int verificationId, int? classIdOverride, long adminUserId, long chatId, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == adminUserId, cancellationToken);
        if (admin == null || (admin.Role != UserRole.Admin && admin.Role != UserRole.Moderator))
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для этого действия.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId, cancellationToken);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Заявка не найдена или уже обработана.", cancellationToken: cancellationToken);
            return;
        }

        var targetClassId = classIdOverride ?? admin.ClassId;

        if (targetClassId == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Сначала создайте класс.", cancellationToken: cancellationToken);
            return;
        }

        var adminClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == targetClassId.Value, cancellationToken);

        if (adminClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "Класс не найден.", cancellationToken: cancellationToken);
            return;
        }

        verification.Status = VerificationStatus.Approved;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = adminUserId;
        verification.ClassId = adminClass.Id;

        var parent = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == verification.TelegramUserId, cancellationToken);

        if (parent != null)
        {
            parent.IsVerified = true;
            parent.VerifiedAt = DateTime.UtcNow;
            parent.ClassId = adminClass.Id;

            var hasLink = await _dbContext.ParentClassLinks
                .AnyAsync(l => l.UserId == parent.Id && l.ClassId == adminClass.Id, cancellationToken);
            if (!hasLink)
            {
                _dbContext.ParentClassLinks.Add(new ParentClassLink
                {
                    UserId = parent.Id,
                    ClassId = adminClass.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                verification.TelegramUserId,
                $"✅ Ваша заявка одобрена! Вы получили доступ к классу '{adminClass.Name}'.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить родителя");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Заявка #{verificationId} одобрена. Родитель получил доступ к классу '{adminClass.Name}'.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleRejectVerification(int verificationId, long adminUserId, long chatId, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == adminUserId, cancellationToken);
        if (admin == null || (admin.Role != UserRole.Admin && admin.Role != UserRole.Moderator))
        {
            await _botClient.SendTextMessageAsync(chatId, "У вас нет прав для этого действия.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId, cancellationToken);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Заявка не найдена или уже обработана.", cancellationToken: cancellationToken);
            return;
        }

        verification.Status = VerificationStatus.Rejected;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = adminUserId;

        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await _botClient.SendTextMessageAsync(
                verification.TelegramUserId,
                "❌ Ваша заявка на верификацию была отклонена. Обратитесь к администратору для уточнения.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось уведомить родителя");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Заявка #{verificationId} отклонена.",
            cancellationToken: cancellationToken);
    }

    private async Task NotifyAdminsAboutNewVerification(ParentVerification verification)
    {
        var adminsAndModerators = await _dbContext.Users
            .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Moderator)
            .ToListAsync();

        var message = $"Новая заявка на верификацию родителя:\n\n" +
                     $"ФИО: {verification.FullName}\n" +
                     $"Телефон: {verification.PhoneNumber}\n" +
                     $"ID заявки: {verification.Id}\n\n" +
                     $"Используйте /verifications для просмотра всех заявок или откройте веб-админ-панель.";

        foreach (var admin in adminsAndModerators)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    admin.TelegramUserId,
                    message,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Не удалось отправить уведомление админу/модератору {admin.TelegramUserId}");
            }
        }
    }
}

