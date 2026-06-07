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
            await _botClient.SendTextMessageAsync(chatId, "РЈ РІР°СЃ РЅРµС‚ РєР»Р°СЃСЃРѕРІ РґР»СЏ РІРµСЂРёС„РёРєР°С†РёР№.", cancellationToken: cancellationToken);
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
            "Р’С‹Р±РµСЂРёС‚Рµ РєР»Р°СЃСЃ РґР»СЏ РїСЂРѕСЃРјРѕС‚СЂР° Р·Р°СЏРІРѕРє:",
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
            await _botClient.SendTextMessageAsync(chatId, "РљР»Р°СЃСЃ РЅРµ РЅР°Р№РґРµРЅ РёР»Рё РІС‹ РЅРµ Р°РґРјРёРЅ СЌС‚РѕРіРѕ РєР»Р°СЃСЃР°.", cancellationToken: cancellationToken);
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
                "РќРµС‚ Р·Р°СЏРІРѕРє РЅР° РІРµСЂРёС„РёРєР°С†РёСЋ.",
                cancellationToken: cancellationToken);
            return;
        }

        foreach (var verification in pendingVerifications)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("вњ… РћРґРѕР±СЂРёС‚СЊ", $"approve_{verification.Id}_{classId}"),
                    InlineKeyboardButton.WithCallbackData("вќЊ РћС‚РєР»РѕРЅРёС‚СЊ", $"reject_{verification.Id}")
                }
            });

            var text = $"Р—Р°СЏРІРєР° #{verification.Id}\n\n" +
                      $"Р¤РРћ: {verification.FullName}\n" +
                      $"РўРµР»РµС„РѕРЅ: {verification.PhoneNumber}\n" +
                      $"Р”Р°С‚Р°: {AppDateTime.Format(verification.CreatedAt)}";

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
            await _botClient.SendTextMessageAsync(chatId, "РЈ РІР°СЃ РЅРµС‚ РїСЂР°РІ РґР»СЏ СЌС‚РѕРіРѕ РґРµР№СЃС‚РІРёСЏ.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId, cancellationToken);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Р—Р°СЏРІРєР° РЅРµ РЅР°Р№РґРµРЅР° РёР»Рё СѓР¶Рµ РѕР±СЂР°Р±РѕС‚Р°РЅР°.", cancellationToken: cancellationToken);
            return;
        }

        var targetClassId = classIdOverride ?? admin.ClassId;

        if (targetClassId == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "РЎРЅР°С‡Р°Р»Р° СЃРѕР·РґР°Р№С‚Рµ РєР»Р°СЃСЃ.", cancellationToken: cancellationToken);
            return;
        }

        var adminClass = await _dbContext.Classes
            .FirstOrDefaultAsync(c => c.Id == targetClassId.Value, cancellationToken);

        if (adminClass == null)
        {
            await _botClient.SendTextMessageAsync(chatId, "РљР»Р°СЃСЃ РЅРµ РЅР°Р№РґРµРЅ.", cancellationToken: cancellationToken);
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
                $"вњ… Р’Р°С€Р° Р·Р°СЏРІРєР° РѕРґРѕР±СЂРµРЅР°! Р’С‹ РїРѕР»СѓС‡РёР»Рё РґРѕСЃС‚СѓРї Рє РєР»Р°СЃСЃСѓ '{adminClass.Name}'.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "РќРµ СѓРґР°Р»РѕСЃСЊ СѓРІРµРґРѕРјРёС‚СЊ СЂРѕРґРёС‚РµР»СЏ");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Р—Р°СЏРІРєР° #{verificationId} РѕРґРѕР±СЂРµРЅР°. Р РѕРґРёС‚РµР»СЊ РїРѕР»СѓС‡РёР» РґРѕСЃС‚СѓРї Рє РєР»Р°СЃСЃСѓ '{adminClass.Name}'.",
            cancellationToken: cancellationToken);
    }

    private async Task HandleRejectVerification(int verificationId, long adminUserId, long chatId, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Users.FirstOrDefaultAsync(u => u.TelegramUserId == adminUserId, cancellationToken);
        if (admin == null || (admin.Role != UserRole.Admin && admin.Role != UserRole.Moderator))
        {
            await _botClient.SendTextMessageAsync(chatId, "РЈ РІР°СЃ РЅРµС‚ РїСЂР°РІ РґР»СЏ СЌС‚РѕРіРѕ РґРµР№СЃС‚РІРёСЏ.", cancellationToken: cancellationToken);
            return;
        }

        var verification = await _dbContext.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == verificationId, cancellationToken);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            await _botClient.SendTextMessageAsync(chatId, "Р—Р°СЏРІРєР° РЅРµ РЅР°Р№РґРµРЅР° РёР»Рё СѓР¶Рµ РѕР±СЂР°Р±РѕС‚Р°РЅР°.", cancellationToken: cancellationToken);
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
                "вќЊ Р’Р°С€Р° Р·Р°СЏРІРєР° РЅР° РІРµСЂРёС„РёРєР°С†РёСЋ Р±С‹Р»Р° РѕС‚РєР»РѕРЅРµРЅР°. РћР±СЂР°С‚РёС‚РµСЃСЊ Рє Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂСѓ РґР»СЏ СѓС‚РѕС‡РЅРµРЅРёСЏ.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "РќРµ СѓРґР°Р»РѕСЃСЊ СѓРІРµРґРѕРјРёС‚СЊ СЂРѕРґРёС‚РµР»СЏ");
        }

        await _botClient.SendTextMessageAsync(
            chatId,
            $"Р—Р°СЏРІРєР° #{verificationId} РѕС‚РєР»РѕРЅРµРЅР°.",
            cancellationToken: cancellationToken);
    }

    private async Task NotifyAdminsAboutNewVerification(ParentVerification verification)
    {
        var adminsAndModerators = await _dbContext.Users
            .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.Moderator)
            .ToListAsync();

        var message = $"РќРѕРІР°СЏ Р·Р°СЏРІРєР° РЅР° РІРµСЂРёС„РёРєР°С†РёСЋ СЂРѕРґРёС‚РµР»СЏ:\n\n" +
                     $"Р¤РРћ: {verification.FullName}\n" +
                     $"РўРµР»РµС„РѕРЅ: {verification.PhoneNumber}\n" +
                     $"ID Р·Р°СЏРІРєРё: {verification.Id}\n\n" +
                     $"РСЃРїРѕР»СЊР·СѓР№С‚Рµ /verifications РґР»СЏ РїСЂРѕСЃРјРѕС‚СЂР° РІСЃРµС… Р·Р°СЏРІРѕРє РёР»Рё РѕС‚РєСЂРѕР№С‚Рµ РІРµР±-Р°РґРјРёРЅ-РїР°РЅРµР»СЊ.";

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
                _logger.LogError(ex, $"РќРµ СѓРґР°Р»РѕСЃСЊ РѕС‚РїСЂР°РІРёС‚СЊ СѓРІРµРґРѕРјР»РµРЅРёРµ Р°РґРјРёРЅСѓ/РјРѕРґРµСЂР°С‚РѕСЂСѓ {admin.TelegramUserId}");
            }
        }
    }
}

