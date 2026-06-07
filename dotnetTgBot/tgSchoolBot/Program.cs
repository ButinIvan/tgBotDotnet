using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Telegram.Bot;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;
using RabbitMQ.Client;
using dotnetTgBot.Interfaces;
using System.Threading;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var envService = new EnvService();
builder.Services.AddSingleton(envService);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure database - PostgreSQL
var connectionString = envService.GetRequiredVariable("CONNECTION_STRING");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            "Слишком много попыток входа. Попробуйте позже.",
            cancellationToken);
    };

    options.AddPolicy("login", httpContext =>
    {
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

// Configure Telegram Bot
builder.Services.Configure<TelegramBotOptions>(options =>
{
    options.BotToken = envService.GetRequiredVariable("TELEGRAM_BOT_TOKEN");
});

// Register Telegram Bot Client
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = envService.GetRequiredVariable("TELEGRAM_BOT_TOKEN");
    return new TelegramBotClient(token);
});

// Register RabbitMQ connection
builder.Services.AddSingleton<IConnection>(sp =>
{
    var uri = envService.GetRequiredVariable("RABBITMQ_CONNECTION");
    var factory = new ConnectionFactory
    {
        Uri = new Uri(uri),
        DispatchConsumersAsync = true
    };

    var retries = 10;
    var delay = TimeSpan.FromSeconds(3);
    for (int i = 0; i < retries; i++)
    {
        try
        {
            return factory.CreateConnection();
        }
        catch (Exception) when (i < retries - 1)
        {
            Thread.Sleep(delay);
        }
    }

    // Final attempt will throw if RabbitMQ is still unavailable.
    return factory.CreateConnection();
});

// Register RabbitMQ service (queue declaration)
builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<INewsQueueProducer, NewsQueueProducer>();
builder.Services.AddHostedService<NewsQueueConsumer>();
builder.Services.AddSingleton<IAdminLoginCodeService, AdminLoginCodeService>();

// Minio / S3
builder.Services.AddSingleton<IS3Repository>(sp =>
{
    var endpoint = envService.GetVariable("MINIO_ENDPOINT", "minio:9000");
    var accessKey = envService.GetRequiredVariable("MINIO_ACCESS_KEY");
    var secretKey = envService.GetRequiredVariable("MINIO_SECRET_KEY");
    var publicEndpoint = envService.GetVariable("MINIO_PUBLIC_ENDPOINT", "minio:9000");
    var logger = sp.GetRequiredService<ILogger<MinioService>>();
    return new MinioService(endpoint, accessKey, secretKey, logger, publicEndpoint);
});

// Register Telegram Bot Service as background service
builder.Services.AddHostedService<TelegramBotService>();

var app = builder.Build();

// Apply migrations before the app starts handling requests.
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var maxRetries = 10;
    var retryDelay = TimeSpan.FromSeconds(3);

    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            if (dbContext.Database.CanConnect())
            {
                dbContext.Database.Migrate();
                logger.LogInformation("Database migrated successfully");
                break;
            }
        }
        catch (Exception ex)
        {
            if (i == maxRetries - 1)
            {
                logger.LogError(ex, "Failed to connect to database after {MaxRetries} attempts", maxRetries);
                throw;
            }

            logger.LogWarning(ex, "Database connection attempt {Attempt} failed, retrying in {Delay} seconds...", i + 1, retryDelay.TotalSeconds);
            Thread.Sleep(retryDelay);
        }
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
