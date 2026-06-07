using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Controllers;

[Authorize]
public class ParentsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ParentsController> _logger;

    public ParentsController(ApplicationDbContext context, ILogger<ParentsController> logger)
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
            ViewBag.IsAdmin = user.Role == UserRole.Admin;
            return View(new List<User>());
        }

        var parentLinkIds = await _context.ParentClassLinks
            .Where(l => l.ClassId == currentClass.Id)
            .Select(l => l.UserId)
            .ToListAsync();

        var parents = await _context.Users
            .Where(u =>
                (u.Role == UserRole.Parent || u.Role == UserRole.Moderator || u.Role == UserRole.Unverified) &&
                (u.ClassId == currentClass.Id || parentLinkIds.Contains(u.Id)))
            .OrderBy(u => u.FullName ?? u.FirstName ?? u.Username)
            .ToListAsync();

        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClass.Id;
        ViewBag.IsAdmin = user.Role == UserRole.Admin;
        return View(parents);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(long id, string role, int selectedClassId)
    {
        var user = await GetCurrentUser();
        if (user == null || user.Role != UserRole.Admin || !await CanManageClass(user, selectedClassId))
        {
            TempData["Error"] = "Только администратор класса может менять роли родителей.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var target = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (target == null)
        {
            TempData["Error"] = "Пользователь не найден.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        if (role == nameof(UserRole.Parent))
        {
            target.Role = UserRole.Parent;
            target.IsVerified = true;
            if (target.ClassId == null)
            {
                target.ClassId = selectedClassId;
            }

            await EnsureParentClassLink(target.Id, selectedClassId);
        }
        else if (role == nameof(UserRole.Moderator))
        {
            target.Role = UserRole.Moderator;
            target.IsVerified = true;
            target.ClassId = selectedClassId;
        }
        else
        {
            TempData["Error"] = "Недопустимая роль.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = "Роль обновлена.";
        return RedirectToAction("Index", new { classId = selectedClassId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveFromClass(long id, int selectedClassId)
    {
        var user = await GetCurrentUser();
        if (user == null || user.Role != UserRole.Admin || !await CanManageClass(user, selectedClassId))
        {
            TempData["Error"] = "Только администратор класса может удалять пользователей из класса.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var target = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (target == null)
        {
            TempData["Error"] = "Пользователь не найден.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var links = await _context.ParentClassLinks
            .Where(l => l.UserId == id && l.ClassId == selectedClassId)
            .ToListAsync();
        _context.ParentClassLinks.RemoveRange(links);

        if (target.ClassId == selectedClassId)
        {
            target.ClassId = null;
            if (target.Role == UserRole.Moderator)
            {
                target.Role = UserRole.Parent;
            }
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = "Пользователь удален из класса.";
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

    private async Task EnsureParentClassLink(long userId, int classId)
    {
        var exists = await _context.ParentClassLinks.AnyAsync(l => l.UserId == userId && l.ClassId == classId);
        if (!exists)
        {
            _context.ParentClassLinks.Add(new ParentClassLink
            {
                UserId = userId,
                ClassId = classId,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
