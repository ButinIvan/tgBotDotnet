using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Controllers;

[Authorize]
public class VerificationsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<VerificationsController> _logger;

    public VerificationsController(ApplicationDbContext context, ILogger<VerificationsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? classId)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        // Определяем доступные классы для админа
        List<Class> classes;
        int? currentClassId;

        if (user.Role == UserRole.Admin)
        {
            classes = await _context.Classes
                .Where(c => c.AdminTelegramUserId == telegramUserId)
                .OrderBy(c => c.Name)
                .ToListAsync();

            currentClassId = classId ?? classes.FirstOrDefault()?.Id;
        }
        else
        {
            classes = new List<Class>();
            currentClassId = user.ClassId;
        }

        if (currentClassId == null)
        {
            return RedirectToAction("Index", "Home");
        }

        var verifications = await _context.ParentVerifications
            .Where(v => v.Status == VerificationStatus.Pending && (v.ClassId == currentClassId || v.ClassId == null))
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();

        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClassId;

        return View(verifications);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        // Определяем класс: для админа приходит из формы, для модератора — из ClassId
        int? currentClassId = null;
        if (user.Role == UserRole.Admin)
        {
            if (!int.TryParse(Request.Form["SelectedClassId"], out var parsedId))
            {
                return RedirectToAction("Index");
            }
            currentClassId = parsedId;
        }
        else
        {
            currentClassId = user.ClassId;
        }

        if (currentClassId == null)
        {
            return RedirectToAction("Index", "Home");
        }

        var verification = await _context.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == id && (v.ClassId == currentClassId || v.ClassId == null));

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            TempData["Error"] = "Заявка не найдена или уже обработана";
            return RedirectToAction("Index");
        }

        verification.Status = VerificationStatus.Approved;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = user.TelegramUserId;
        verification.ClassId = currentClassId; // Устанавливаем класс при одобрении

        var parent = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == verification.TelegramUserId);

        if (parent != null)
        {
            parent.IsVerified = true;
            parent.VerifiedAt = DateTime.UtcNow;
            parent.ClassId = currentClassId;

            // Добавляем связь многие-ко-многим
            var hasLink = await _context.ParentClassLinks
                .AnyAsync(l => l.UserId == parent.Id && l.ClassId == currentClassId, CancellationToken.None);
            if (!hasLink)
            {
                _context.ParentClassLinks.Add(new ParentClassLink
                {
                    UserId = parent.Id,
                    ClassId = currentClassId.Value,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        TempData["Success"] = "Заявка одобрена";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        int? currentClassId = user.Role == UserRole.Admin
            ? int.TryParse(Request.Form["SelectedClassId"], out var parsedId) ? parsedId : (int?)null
            : user.ClassId;

        if (currentClassId == null)
        {
            return RedirectToAction("Index", "Home");
        }

        var verification = await _context.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == id && v.ClassId == currentClassId);

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            TempData["Error"] = "Заявка не найдена или уже обработана";
            return RedirectToAction("Index");
        }

        verification.Status = VerificationStatus.Rejected;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = user.TelegramUserId;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Заявка отклонена";
        return RedirectToAction("Index");
    }
}

