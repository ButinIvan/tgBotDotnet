using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Controllers;

[Authorize]
public class ModeratorsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ModeratorsController> _logger;

    public ModeratorsController(ApplicationDbContext context, ILogger<ModeratorsController> logger)
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
            ViewBag.IsAdmin = user.Role == UserRole.Admin;
            ViewBag.Classes = classes;
            ViewBag.SelectedClassId = null;
            return View(new List<User>());
        }

        var moderators = await _context.Users
            .Where(u => u.ClassId == currentClass.Id && u.Role == UserRole.Moderator)
            .OrderBy(u => u.FullName ?? u.FirstName ?? u.Username)
            .ToListAsync();

        ViewBag.IsAdmin = user.Role == UserRole.Admin;
        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClass.Id;

        return View(moderators);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddModerator(string telegramUserId, int selectedClassId)
    {
        var currentUser = await GetCurrentUser();
        if (currentUser == null || currentUser.Role != UserRole.Admin || !await CanManageClass(currentUser, selectedClassId))
        {
            TempData["Error"] = "Только администратор класса может добавлять модераторов.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        if (!long.TryParse(telegramUserId, out var userId))
        {
            TempData["Error"] = "Неверный Telegram User ID.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var targetUser = await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == userId);
        if (targetUser == null)
        {
            TempData["Error"] = "Пользователь не найден.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        if (targetUser.Role == UserRole.Admin)
        {
            TempData["Error"] = "Нельзя изменить роль администратора.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var belongsToClass = targetUser.ClassId == selectedClassId ||
            await _context.ParentClassLinks.AnyAsync(l => l.UserId == targetUser.Id && l.ClassId == selectedClassId);

        if (!belongsToClass)
        {
            TempData["Error"] = "Пользователь не принадлежит выбранному классу.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        if (targetUser.Role == UserRole.Moderator && targetUser.ClassId == selectedClassId)
        {
            TempData["Error"] = "Пользователь уже является модератором этого класса.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        targetUser.Role = UserRole.Moderator;
        targetUser.IsVerified = true;
        targetUser.ClassId = selectedClassId;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Модератор успешно добавлен.";
        return RedirectToAction("Index", new { classId = selectedClassId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveModerator(long id, int selectedClassId)
    {
        var currentUser = await GetCurrentUser();
        if (currentUser == null || currentUser.Role != UserRole.Admin || !await CanManageClass(currentUser, selectedClassId))
        {
            TempData["Error"] = "Только администратор класса может удалять модераторов.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var moderator = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == id && u.ClassId == selectedClassId);

        if (moderator == null || moderator.Role != UserRole.Moderator)
        {
            TempData["Error"] = "Модератор не найден.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        moderator.Role = UserRole.Parent;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Права модератора удалены.";
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

    private async Task<bool> CanManageClass(User user, int classId)
    {
        return await _context.Classes.AnyAsync(c => c.Id == classId && c.AdminTelegramUserId == user.TelegramUserId);
    }
}
