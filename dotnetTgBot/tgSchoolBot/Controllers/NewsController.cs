using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using dotnetTgBot.Models;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;
using dotnetTgBot.Interfaces;

namespace dotnetTgBot.Controllers;

[Authorize]
public class NewsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NewsController> _logger;
    private readonly INewsQueueProducer _newsQueueProducer;
    private readonly IS3Repository _s3Repository;

    public NewsController(ApplicationDbContext context, ILogger<NewsController> logger, INewsQueueProducer newsQueueProducer, IS3Repository s3Repository)
    {
        _context = context;
        _logger = logger;
        _newsQueueProducer = newsQueueProducer;
        _s3Repository = s3Repository;
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
    public async Task<IActionResult> Create(News news, IFormFile? reportFile)
    {
        var telegramUserId = long.Parse(User.Identity!.Name!);
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user == null || (user.Role != UserRole.Admin && user.Role != UserRole.Moderator))
        {
            return RedirectToAction("Login", "Account");
        }

        if (news.Type == NewsType.Report)
        {
            // Для отчета контент не обязателен
            ModelState.Remove(nameof(News.Content));
            news.Content = string.Empty;
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

                if (news.Type == NewsType.Report)
                {
                    if (reportFile == null || reportFile.Length == 0)
                    {
                        ModelState.AddModelError(string.Empty, "Загрузите файл отчета.");
                    }
                    else
                    {
                        var objectName = $"{news.ClassId}/{Guid.NewGuid()}_{reportFile.FileName}";
                        using var stream = reportFile.OpenReadStream();
                        await _s3Repository.UploadFileAsync(objectName, stream, reportFile.ContentType ?? "application/octet-stream", reportFile.Length);

                        news.FilePath = objectName;
                        news.FileName = reportFile.FileName;

                        _context.News.Add(news);
                        await _context.SaveChangesAsync();

                        TempData["Success"] = "Отчет сохранен. Его можно скачать через бота командой /viewreports.";
                        return RedirectToAction("Index", "Home");
                    }
                }
                else
                {
                    _context.News.Add(news);
                    await _context.SaveChangesAsync();

                    await _newsQueueProducer.EnqueueNewsAsync(news);

                    TempData["Success"] = "Новость успешно создана и поставлена в очередь на отправку!";
                    return RedirectToAction("Index", "Home");
                }
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

}

