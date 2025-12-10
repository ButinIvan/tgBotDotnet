namespace dotnetTgBot.Models;

public class ParentVerification
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public VerificationStatus Status { get; set; }
    public int? ClassId { get; set; } // Класс, к которому родитель хочет получить доступ
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public long? ProcessedByTelegramUserId { get; set; } // Кто обработал заявку
}

public enum VerificationStatus
{
    Pending,   // Ожидает рассмотрения
    Approved,  // Одобрено
    Rejected   // Отклонено
}

