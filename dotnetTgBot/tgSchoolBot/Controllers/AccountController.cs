using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;

namespace dotnetTgBot.Controllers;

public class AccountController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AccountController> _logger;
    private readonly IAdminLoginCodeService _adminLoginCodes;

    public AccountController(
        ApplicationDbContext context,
        ILogger<AccountController> logger,
        IAdminLoginCodeService adminLoginCodes)
    {
        _context = context;
        _logger = logger;
        _adminLoginCodes = adminLoginCodes;
    }

    [HttpGet]
    public IActionResult Login()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(string telegramUserId, string loginCode)
    {
        if (string.IsNullOrWhiteSpace(telegramUserId) || !long.TryParse(telegramUserId, out var userId))
        {
            ViewBag.Error = "Неверный Telegram User ID.";
            return View();
        }

        if (string.IsNullOrWhiteSpace(loginCode))
        {
            ViewBag.Error = "Введите одноразовый код из Telegram.";
            return View();
        }

        return await ProcessLogin(userId, loginCode);
    }

    private async Task<IActionResult> ProcessLogin(long userId, string loginCode)
    {
        var user = await _context.Users
            .Include(u => u.Class)
            .FirstOrDefaultAsync(u => u.TelegramUserId == userId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            _logger.LogWarning("Rejected admin panel login for Telegram user {TelegramUserId}", userId);
            ViewBag.Error = "У вас нет доступа к админ-панели. Войти могут только администраторы и модераторы.";
            return View();
        }

        if (!_adminLoginCodes.TryConsumeCode(user.TelegramUserId, loginCode))
        {
            _logger.LogWarning("Rejected admin panel login for Telegram user {TelegramUserId}: invalid one-time code", user.TelegramUserId);
            ViewBag.Error = "Неверный или устаревший код входа. Запросите новый код командой /adminpanel.";
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

        _logger.LogInformation("Admin panel login for Telegram user {TelegramUserId}", user.TelegramUserId);
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
