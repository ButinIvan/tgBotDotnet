namespace dotnetTgBot.Models;

public class User
{
    public long Id { get; set; }
    public long TelegramUserId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public UserRole Role { get; set; }
    public int? ClassId { get; set; }
    public Class? Class { get; set; }
    public bool IsVerified { get; set; }
    public string? FullName { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }

    // Для родителей с несколькими детьми — связи хранить в ParentClassLink
    public List<ParentClassLink> ClassLinks { get; set; } = new();
}

public enum UserRole
{
    Admin,
    Moderator,
    Parent,
    Unverified
}

