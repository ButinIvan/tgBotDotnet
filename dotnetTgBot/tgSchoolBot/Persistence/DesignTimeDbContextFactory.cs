using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace dotnetTgBot.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        
        // Для создания миграций используем локальную строку подключения
        // В продакшене будет использоваться переменная окружения
        var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? "Host=localhost;Port=5433;Database=tgSchoolBotDb;Username=postgres;Password=postgres";
        
        optionsBuilder.UseNpgsql(connectionString);
        
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}

