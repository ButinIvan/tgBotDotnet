using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using dotnetTgBot.Models;

namespace dotnetTgBot.Services;

public interface INewsQueueProducer
{
    Task EnqueueNewsAsync(News news, CancellationToken cancellationToken = default);
}

public class NewsQueueProducer : INewsQueueProducer
{
    private readonly IRabbitMqService _rabbit;
    private readonly ILogger<NewsQueueProducer> _logger;
    private readonly object _lock = new();

    public NewsQueueProducer(IRabbitMqService rabbit, ILogger<NewsQueueProducer> logger)
    {
        _rabbit = rabbit;
        _logger = logger;
    }

    public Task EnqueueNewsAsync(News news, CancellationToken cancellationToken = default)
    {
        try
        {
            if (news.Type == NewsType.News)
            {
                var payload = new NewsQueueMessage
                {
                    ClassId = news.ClassId,
                    Title = news.Title,
                    Content = news.Content,
                    CreatedAtUtc = news.CreatedAt,
                    Type = news.Type.ToString()
                };

                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

                lock (_lock)
                {
                    _rabbit.Channel.BasicPublish(
                        exchange: string.Empty,
                        routingKey: _rabbit.QueueName,
                        mandatory: false,
                        basicProperties: null,
                        body: body);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue news message");
        }

        return Task.CompletedTask;
    }
}

public class NewsQueueMessage
{
    public int ClassId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Type { get; set; } = "News";
}

