namespace dotnetTgBot.Models;

public class Class
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty; // Например, "5А", "10Б"
    public long AdminTelegramUserId { get; set; } // Telegram ID админа класса
    public User? Admin { get; set; }
    public List<ParentClassLink> ParentLinks { get; set; } = new();
    public List<News> News { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

