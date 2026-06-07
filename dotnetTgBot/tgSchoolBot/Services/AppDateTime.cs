namespace dotnetTgBot.Services;

public static class AppDateTime
{
    private const string DefaultTimeZoneId = "Europe/Moscow";
    private static readonly Lazy<TimeZoneInfo> LocalTimeZone = new(ResolveTimeZone);

    public static DateTime ToLocal(DateTime utcDateTime)
    {
        var utc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);

        return TimeZoneInfo.ConvertTimeFromUtc(utc, LocalTimeZone.Value);
    }

    public static string Format(DateTime utcDateTime)
    {
        return ToLocal(utcDateTime).ToString("dd.MM.yyyy HH:mm");
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        var configuredTimeZone = Environment.GetEnvironmentVariable("APP_TIME_ZONE");
        var timeZoneId = string.IsNullOrWhiteSpace(configuredTimeZone)
            ? DefaultTimeZoneId
            : configuredTimeZone;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId == DefaultTimeZoneId)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
        catch (InvalidTimeZoneException) when (timeZoneId == DefaultTimeZoneId)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
    }
}
