using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
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
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        int? currentClassId;
        List<Class> classes;

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

        // Родители: по ClassId или ParentClassLinks
        var parentLinkIds = await _context.ParentClassLinks
            .Where(l => l.ClassId == currentClassId.Value)
            .Select(l => l.UserId)
            .ToListAsync();

        var parents = await _context.Users
            .Where(u =>
                (u.Role == UserRole.Parent || u.Role == UserRole.Moderator || u.Role == UserRole.Unverified) &&
                (u.ClassId == currentClassId.Value || parentLinkIds.Contains(u.Id)))
            .OrderBy(u => u.FullName ?? u.FirstName ?? u.Username)
            .ToListAsync();

        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClassId;
        ViewBag.IsAdmin = user.Role == UserRole.Admin;
        return View(parents);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(long id, string role, int selectedClassId)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || user.Role != UserRole.Admin)
        {
            TempData["Error"] = "Только администратор может менять роли родителей.";
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
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || user.Role != UserRole.Admin)
        {
            TempData["Error"] = "Только администратор может удалять родителей из класса.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        var target = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (target == null)
        {
            TempData["Error"] = "Пользователь не найден.";
            return RedirectToAction("Index", new { classId = selectedClassId });
        }

        // Удаляем ссылку многие-ко-многим
        var links = await _context.ParentClassLinks
            .Where(l => l.UserId == id && l.ClassId == selectedClassId)
            .ToListAsync();
        _context.ParentClassLinks.RemoveRange(links);

        if (target.ClassId == selectedClassId)
        {
            target.ClassId = null;
        }

        await _context.SaveChangesAsync();
        TempData["Success"] = "Пользователь удален из класса.";
        return RedirectToAction("Index", new { classId = selectedClassId });
    }
}


