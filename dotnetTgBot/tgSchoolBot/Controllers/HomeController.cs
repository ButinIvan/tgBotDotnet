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

        List<Class> classes;
        Class? currentClass;

        if (user.Role == UserRole.Admin)
        {
            classes = await _context.Classes
                .Where(c => c.AdminTelegramUserId == telegramUserId)
                .OrderBy(c => c.Name)
                .ToListAsync();

            currentClass = classId.HasValue
                ? classes.FirstOrDefault(c => c.Id == classId.Value)
                : classes.FirstOrDefault();
        }
        else
        {
            currentClass = user.ClassId.HasValue
                ? await _context.Classes.FirstOrDefaultAsync(c => c.Id == user.ClassId.Value)
                : null;

            classes = currentClass == null
                ? new List<Class>()
                : new List<Class> { currentClass };
        }

        ViewBag.User = user;
        ViewBag.Classes = classes;
        ViewBag.SelectedClassId = currentClass?.Id;
        ViewBag.Class = currentClass;

        if (currentClass == null)
        {
            return View(new List<News>());
        }

        var news = await _context.News
            .Where(n => n.ClassId == currentClass.Id)
            .OrderByDescending(n => n.CreatedAt)
            .Take(10)
            .ToListAsync();

        return View(news);
    }
}

