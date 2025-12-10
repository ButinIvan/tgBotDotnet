namespace dotnetTgBot.Models;

public class ParentClassLink
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public int ClassId { get; set; }
    public Class? Class { get; set; }
    public DateTime CreatedAt { get; set; }
}


