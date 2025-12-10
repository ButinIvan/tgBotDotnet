using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<HomeController> _logger;

    public HomeController(ApplicationDbContext context, ILogger<HomeController> logger)
    {
        _context = context;
        _logger = logger;
    }

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

        ViewBag.User = user;
        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClassId;

        var currentClass = await _context.Classes.FirstOrDefaultAsync(c => c.Id == currentClassId);
        ViewBag.Class = currentClass;

        var news = await _context.News
            .Where(n => n.ClassId == currentClassId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(10)
            .ToListAsync();

        return View(news);
    }
}

