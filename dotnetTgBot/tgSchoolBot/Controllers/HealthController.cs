using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;

namespace dotnetTgBot.Controllers;

[AllowAnonymous]
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IRabbitMqService _rabbitMqService;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        ApplicationDbContext context,
        IRabbitMqService rabbitMqService,
        ILogger<HealthController> logger)
    {
        _context = context;
        _rabbitMqService = rabbitMqService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var databaseReady = await CheckDatabase(cancellationToken);
        var rabbitReady = _rabbitMqService.Channel.IsOpen;
        var healthy = databaseReady && rabbitReady;

        var response = new
        {
            status = healthy ? "Healthy" : "Unhealthy",
            checks = new
            {
                database = databaseReady ? "Healthy" : "Unhealthy",
                rabbitMq = rabbitReady ? "Healthy" : "Unhealthy"
            }
        };

        return healthy ? Ok(response) : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    private async Task<bool> CheckDatabase(CancellationToken cancellationToken)
    {
        try
        {
            return await _context.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for database");
            return false;
        }
    }
}
