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
        var user = await GetCurrentUser();
        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        var classes = await GetManageableClasses(user);
        var currentClass = GetCurrentClass(classes, classId);

        if (currentClass == null)
        {
            ViewBag.Classes = classes;
            ViewBag.SelectedClassId = null;
            return View(new List<ParentVerification>());
        }

        var verifications = await _context.ParentVerifications
            .Where(v => v.Status == VerificationStatus.Pending && (v.ClassId == currentClass.Id || v.ClassId == null))
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();

        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClass.Id;

        return View(verifications);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, int selectedClassId)
    {
        var user = await GetCurrentUser();
        if (user == null || !await CanReviewClass(user, selectedClassId))
        {
            return RedirectToAction("Login", "Account");
        }

        var verification = await _context.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == id && (v.ClassId == selectedClassId || v.ClassId == null));

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            TempData["Error"] = "Заявка не найдена или уже обработана.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        verification.Status = VerificationStatus.Approved;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = user.TelegramUserId;
        verification.ClassId = selectedClassId;

        var parent = await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == verification.TelegramUserId);
        if (parent != null)
        {
            parent.IsVerified = true;
            parent.VerifiedAt = DateTime.UtcNow;
            parent.ClassId = selectedClassId;

            var hasLink = await _context.ParentClassLinks.AnyAsync(l => l.UserId == parent.Id && l.ClassId == selectedClassId);
            if (!hasLink)
            {
                _context.ParentClassLinks.Add(new ParentClassLink
                {
                    UserId = parent.Id,
                    ClassId = selectedClassId,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();

        TempData["Success"] = "Заявка одобрена.";
        return RedirectToAction("Index", new { classId = selectedClassId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, int selectedClassId)
    {
        var user = await GetCurrentUser();
        if (user == null || !await CanReviewClass(user, selectedClassId))
        {
            return RedirectToAction("Login", "Account");
        }

        var verification = await _context.ParentVerifications
            .FirstOrDefaultAsync(v => v.Id == id && (v.ClassId == selectedClassId || v.ClassId == null));

        if (verification == null || verification.Status != VerificationStatus.Pending)
        {
            TempData["Error"] = "Заявка не найдена или уже обработана.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        verification.Status = VerificationStatus.Rejected;
        verification.ProcessedAt = DateTime.UtcNow;
        verification.ProcessedByTelegramUserId = user.TelegramUserId;

        await _context.SaveChangesAsync();

        TempData["Success"] = "Заявка отклонена.";
        return RedirectToAction("Index", new { classId = selectedClassId });
    }

    private async Task<User?> GetCurrentUser()
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        return await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);
    }

    private async Task<List<Class>> GetManageableClasses(User user)
    {
        var classes = await _context.Classes
            .Where(c => c.AdminTelegramUserId == user.TelegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        if (user.Role == UserRole.Moderator && user.ClassId.HasValue)
        {
            var moderatorClass = await _context.Classes.FirstOrDefaultAsync(c => c.Id == user.ClassId.Value);
            if (moderatorClass != null && classes.All(c => c.Id != moderatorClass.Id))
            {
                classes.Add(moderatorClass);
            }
        }

        return classes.OrderBy(c => c.Name).ToList();
    }

    private static Class? GetCurrentClass(List<Class> classes, int? classId)
    {
        return classId.HasValue
            ? classes.FirstOrDefault(c => c.Id == classId.Value)
            : classes.FirstOrDefault();
    }

    private async Task<bool> CanReviewClass(User user, int classId)
    {
        if (user.Role == UserRole.Moderator && user.ClassId == classId)
        {
            return true;
        }

        return user.Role == UserRole.Admin &&
            await _context.Classes.AnyAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId);
    }
}
