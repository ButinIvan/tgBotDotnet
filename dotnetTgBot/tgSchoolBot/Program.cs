using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Telegram.Bot;
using dotnetTgBot.Persistence;
using dotnetTgBot.Services;
using RabbitMQ.Client;
using dotnetTgBot.Interfaces;
using System.Threading;

var builder = WebApplication.CreateBuilder(args);

var envService = new EnvService();
builder.Services.AddSingleton(envService);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure database - PostgreSQL
var connectionString = envService.GetVariable("CONNECTION_STRING");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
    });

builder.Services.AddAuthorization();

// Configure Telegram Bot
builder.Services.Configure<TelegramBotOptions>(options =>
{
    options.BotToken = envService.GetVariable("TELEGRAM_BOT_TOKEN");
});

// Register Telegram Bot Client
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = envService.GetVariable("TELEGRAM_BOT_TOKEN");
    return new Telegram.Bot.TelegramBotClient(token);
});

// Register RabbitMQ connection
builder.Services.AddSingleton<IConnection>(sp =>
{
    var uri = envService.GetVariable("RABBITMQ_CONNECTION");
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
        catch (Exception ex) when (i < retries - 1)
        {
            Thread.Sleep(delay);
        }
    }

    // Final attempt (will throw if fails)
    return factory.CreateConnection();
});

// Register RabbitMQ service (queue declaration)
builder.Services.AddSingleton<IRabbitMqService, RabbitMqService>();
builder.Services.AddSingleton<INewsQueueProducer, NewsQueueProducer>();
builder.Services.AddHostedService<NewsQueueConsumer>();

// Minio / S3
builder.Services.AddSingleton<IS3Repository>(sp =>
{
    var endpoint = envService.GetVariable("MINIO_ENDPOINT", "minio:9000");
    var accessKey = envService.GetVariable("MINIO_ACCESS_KEY");
    var secretKey = envService.GetVariable("MINIO_SECRET_KEY");
    var publicEndpoint = envService.GetVariable("MINIO_PUBLIC_ENDPOINT", "minio:9000");
    return new MinioService(endpoint, accessKey, secretKey, publicEndpoint);
});

// Register Telegram Bot Service as background service
builder.Services.AddHostedService<TelegramBotService>();

var app = builder.Build();

// Ensure database is created and migrated
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
                // Пытаемся применить миграции
                try
                {
                    dbContext.Database.Migrate();
                    logger.LogInformation("Database migrated successfully");
                }
                catch (Exception migrateEx)
                {
                    // Если миграций нет или они не работают, создаем базу через EnsureCreated
                    logger.LogWarning(migrateEx, "Migration failed (this is normal if no migrations exist), trying EnsureCreated...");
                    dbContext.Database.EnsureCreated();
                    logger.LogInformation("Database created successfully using EnsureCreated");
                }
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
            else
            {
                logger.LogWarning(ex, "Database connection attempt {Attempt} failed, retrying in {Delay} seconds...", i + 1, retryDelay.TotalSeconds);
                Thread.Sleep(retryDelay);
            }
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
