using System.Net;

namespace dotnetTgBot.Services;

public static class TelegramHtml
{
    public static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
