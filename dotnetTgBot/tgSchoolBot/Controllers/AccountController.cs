using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;

namespace dotnetTgBot.Controllers;

public class AccountController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AccountController> _logger;

    public AccountController(ApplicationDbContext context, ILogger<AccountController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string telegramUserId)
    {
        if (string.IsNullOrWhiteSpace(telegramUserId) || !long.TryParse(telegramUserId, out var userId))
        {
            ViewBag.Error = "Неверный Telegram User ID";
            return View();
        }

        return await ProcessLogin(userId);
    }

    private async Task<IActionResult> ProcessLogin(long userId)
    {
        var user = await _context.Users
            .Include(u => u.Class)
            .FirstOrDefaultAsync(u => u.TelegramUserId == userId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            ViewBag.Error = "У вас нет доступа к админ-панели. Войти могут только администраторы и модераторы.";
            return View();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.TelegramUserId.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("Role", user.Role.ToString()),
            new("ClassId", user.ClassId?.ToString() ?? string.Empty)
        };

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(claimsIdentity),
            authProperties);

        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
}
