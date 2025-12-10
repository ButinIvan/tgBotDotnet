using Microsoft.EntityFrameworkCore;
using dotnetTgBot.Models;

namespace dotnetTgBot.Persistence;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Class> Classes { get; set; }
    public DbSet<News> News { get; set; }
    public DbSet<ParentVerification> ParentVerifications { get; set; }
    public DbSet<ParentClassLink> ParentClassLinks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TelegramUserId).IsUnique();
            entity.Property(e => e.Role).HasConversion<string>();
            entity.HasOne(e => e.Class)
                  .WithMany()
                  .HasForeignKey(e => e.ClassId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Class>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Admin)
                  .WithMany() // допускаем несколько классов на одного админа
                  .HasForeignKey(e => e.AdminTelegramUserId)
                  .HasPrincipalKey(e => e.TelegramUserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<News>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.HasOne(e => e.Class)
                  .WithMany(c => c.News)
                  .HasForeignKey(e => e.ClassId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ParentVerification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
        });

        modelBuilder.Entity<ParentClassLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserId, e.ClassId }).IsUnique();
            entity.HasOne(e => e.User)
                  .WithMany(u => u.ClassLinks)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Class)
                  .WithMany(c => c.ParentLinks)
                  .HasForeignKey(e => e.ClassId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

