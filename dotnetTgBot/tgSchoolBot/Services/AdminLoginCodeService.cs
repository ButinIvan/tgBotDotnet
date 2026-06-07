using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace dotnetTgBot.Services;

public interface IAdminLoginCodeService
{
    string CreateCode(long telegramUserId);
    bool TryConsumeCode(long telegramUserId, string code);
}

public class AdminLoginCodeService : IAdminLoginCodeService
{
    private readonly ConcurrentDictionary<long, LoginCodeEntry> _codes = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public string CreateCode(long telegramUserId)
    {
        CleanupExpired();

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _codes[telegramUserId] = new LoginCodeEntry(code, DateTime.UtcNow.Add(_ttl));
        return code;
    }

    public bool TryConsumeCode(long telegramUserId, string code)
    {
        CleanupExpired();

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (!_codes.TryGetValue(telegramUserId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
        {
            _codes.TryRemove(telegramUserId, out _);
            return false;
        }

        var matches = string.Equals(entry.Code, code.Trim(), StringComparison.Ordinal);
        if (matches)
        {
            _codes.TryRemove(telegramUserId, out _);
        }

        return matches;
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _codes)
        {
            if (pair.Value.ExpiresAtUtc < now)
            {
                _codes.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record LoginCodeEntry(string Code, DateTime ExpiresAtUtc);
}
