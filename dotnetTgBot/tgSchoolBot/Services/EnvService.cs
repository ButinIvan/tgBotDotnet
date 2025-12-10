namespace dotnetTgBot.Services;

public class EnvService
{
    public string GetVariable(string envItem, string defaultValue = "")
    {
        var value = Environment.GetEnvironmentVariable(envItem);
        if (string.IsNullOrEmpty(value))
        {
            Console.WriteLine($"⚠️  Warning: Environment variable '{envItem}' is not set or empty.");
        }
        return value ?? defaultValue;
    }
}