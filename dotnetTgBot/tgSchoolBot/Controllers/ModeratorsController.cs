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
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

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

        var moderators = await _context.Users
            .Where(u => u.ClassId == currentClassId && u.Role == UserRole.Moderator)
            .ToListAsync();

        ViewBag.IsAdmin = user.Role == UserRole.Admin;
        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClassId;

        return View(moderators);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddModerator(string telegramUserId)
    {
        var currentUserId = long.Parse(User.Identity!.Name!);
        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == currentUserId);

        if (currentUser == null || (currentUser.Role != UserRole.Admin && currentUser.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        var classId = currentUser.Role == UserRole.Admin
            ? (int.TryParse(Request.Form["SelectedClassId"], out var parsedId) ? parsedId : (int?)null)
            : currentUser.ClassId;

        if (classId == null)
            return RedirectToAction("Index", "Home");

        if (!long.TryParse(telegramUserId, out var userId))
        {
            TempData["Error"] = "Неверный Telegram User ID";
            return RedirectToAction("Index");
        }

        var targetUser = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == userId);

        if (targetUser == null)
        {
            TempData["Error"] = "Пользователь не найден";
            return RedirectToAction("Index");
        }

        if (targetUser.Role == UserRole.Admin)
        {
            TempData["Error"] = "Нельзя изменить роль администратора";
            return RedirectToAction("Index");
        }

        if (targetUser.ClassId != classId)
        {
            TempData["Error"] = "Пользователь не принадлежит вашему классу";
            return RedirectToAction("Index");
        }

        targetUser.Role = UserRole.Moderator;
        targetUser.IsVerified = true;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Модератор успешно добавлен";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveModerator(int id)
    {
        var currentUserId = long.Parse(User.Identity!.Name!);
        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == currentUserId);

        if (currentUser == null || currentUser.Role != UserRole.Admin)
        {
            TempData["Error"] = "Только администратор может удалять модераторов";
            return RedirectToAction("Index");
        }

        var moderator = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == id && u.ClassId == currentUser.ClassId);

        if (moderator == null || moderator.Role != UserRole.Moderator)
        {
            TempData["Error"] = "Модератор не найден";
            return RedirectToAction("Index");
        }

        moderator.Role = UserRole.Parent;
        await _context.SaveChangesAsync();

        TempData["Success"] = "Права модератора успешно удалены";
        return RedirectToAction("Index");
    }
}

