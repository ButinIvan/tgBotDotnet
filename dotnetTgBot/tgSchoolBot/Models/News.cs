namespace dotnetTgBot.Models;

public class News
{
    public int Id { get; set; }
    public int ClassId { get; set; }
    public Class? Class { get; set; }
    public long AuthorTelegramUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public NewsType Type { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum NewsType
{
    News,    // Новость
    Report   // Отчет
}

