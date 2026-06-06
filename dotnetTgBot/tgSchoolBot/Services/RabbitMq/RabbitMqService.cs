using RabbitMQ.Client;

namespace dotnetTgBot.Services;

public interface IRabbitMqService : IDisposable
{
    IModel Channel { get; }
    string QueueName { get; }
}

public class RabbitMqService : IRabbitMqService
{
    private readonly IConnection _connection;
    public IModel Channel { get; }
    public string QueueName { get; }

    public RabbitMqService(IConnection connection, EnvService envService)
    {
        _connection = connection;
        QueueName = envService.GetVariable("RABBITMQ_QUEUE") ?? "tg_bot_queue";

        Channel = _connection.CreateModel();
        Channel.QueueDeclare(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }

    public void Dispose()
    {
        try { Channel?.Close(); } catch { }
        try { Channel?.Dispose(); } catch { }
        try { _connection?.Close(); } catch { }
    }
}

