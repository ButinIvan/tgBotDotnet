using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using Telegram.Bot;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;

namespace dotnetTgBot.Controllers;

[Authorize]
public class NewsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NewsController> _logger;
    private readonly ITelegramBotClient _botClient;

    public NewsController(ApplicationDbContext context, ILogger<NewsController> logger, ITelegramBotClient botClient)
    {
        _context = context;
        _logger = logger;
        _botClient = botClient;
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        var classes = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        if (!classes.Any())
        {
            return RedirectToAction("Index", "Home");
        }

        ViewBag.Classes = classes.Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name }).ToList();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(News news)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        if (ModelState.IsValid)
        {
            var ownedClass = await _context.Classes
                .FirstOrDefaultAsync(c => c.Id == news.ClassId && c.AdminTelegramUserId == telegramUserId);

            if (ownedClass == null)
            {
                ModelState.AddModelError(string.Empty, "Выберите класс, которым вы управляете.");
            }
            else
            {
                news.AuthorTelegramUserId = user.TelegramUserId;
                news.CreatedAt = DateTime.UtcNow;

                _context.News.Add(news);
                await _context.SaveChangesAsync();

                // Уведомляем родителей о новой новости
                await NotifyParentsAboutNewNews(news);

                TempData["Success"] = "Новость успешно создана и отправлена родителям!";
                return RedirectToAction("Index", "Home");
            }
        }

        var classes = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();
        ViewBag.Classes = classes.Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name }).ToList();
        return View(news);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        var ownedClasses = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var ownedClassIds = ownedClasses.Select(c => c.Id).ToHashSet();

        var news = await _context.News
            .FirstOrDefaultAsync(n => n.Id == id && ownedClassIds.Contains(n.ClassId));

        if (news == null)
        {
            return NotFound();
        }

        ViewBag.Classes = ownedClasses
            .Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name, Selected = c.Id == news.ClassId })
            .ToList();
        return View(news);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, News news)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        if (id != news.Id)
        {
            return NotFound();
        }

        var ownedClasses = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();
        var ownedClassIds = ownedClasses.Select(c => c.Id).ToHashSet();

        var existingNews = await _context.News
            .FirstOrDefaultAsync(n => n.Id == id && ownedClassIds.Contains(n.ClassId));

        if (existingNews == null)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            var ownedClass = await _context.Classes
                .FirstOrDefaultAsync(c => c.Id == news.ClassId && c.AdminTelegramUserId == telegramUserId);

            if (ownedClass == null)
            {
                ModelState.AddModelError(string.Empty, "Выберите класс, которым вы управляете.");
            }
            else
            {
            existingNews.Title = news.Title;
            existingNews.Content = news.Content;
            existingNews.Type = news.Type;
            existingNews.ClassId = news.ClassId;

            await _context.SaveChangesAsync();
            return RedirectToAction("Index", "Home");
        }
        }

        var classes = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .OrderBy(c => c.Name)
            .ToListAsync();
        ViewBag.Classes = classes.Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name, Selected = c.Id == news.ClassId }).ToList();
        return View(news);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        var ownedClassesIds = await _context.Classes
            .Where(c => c.AdminTelegramUserId == telegramUserId)
            .Select(c => c.Id)
            .ToListAsync();

        var news = await _context.News
            .FirstOrDefaultAsync(n => n.Id == id && ownedClassesIds.Contains(n.ClassId));

        if (news != null)
        {
            _context.News.Remove(news);
            await _context.SaveChangesAsync();
        }

        return RedirectToAction("Index", "Home");
    }

    private async Task NotifyParentsAboutNewNews(News news)
    {
        // Получаем родителей по прямой привязке и через ParentClassLinks
        var parentIdsFromLinks = await _context.ParentClassLinks
            .Where(l => l.ClassId == news.ClassId)
            .Select(l => l.UserId)
            .ToListAsync();

        var recipients = await _context.Users
            .Where(u =>
                (
                    (u.Role == UserRole.Parent && u.IsVerified &&
                     (u.ClassId == news.ClassId || parentIdsFromLinks.Contains(u.Id)))
                    || (u.Role == UserRole.Admin && u.ClassId == news.ClassId)
                    || (u.Role == UserRole.Moderator && u.ClassId == news.ClassId)
                ))
            .ToListAsync();

        // Исключаем дубликаты по TelegramUserId
        var recipientIds = recipients
            .Select(u => u.TelegramUserId)
            .Distinct()
            .ToList();

        var localDate = news.CreatedAt.AddHours(3); // UTC +3
        var message = $"<b>{news.Title}</b>\n\n" +
                     $"{news.Content}\n\n" +
                     $"Дата: {localDate:dd.MM.yyyy HH:mm}";

        foreach (var tgId in recipientIds)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    tgId,
                    message,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Не удалось отправить новость пользователю {tgId}");
            }
        }
    }
}

