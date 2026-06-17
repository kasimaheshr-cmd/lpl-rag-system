using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

public interface IRedisCacheService
{
    Task<CachedAnswer?> GetExactAsync(string question, CancellationToken ct = default);
    Task SetExactAsync(string question, CachedAnswer value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<object> GetStatsAsync(CancellationToken ct = default);
}

// In-memory fallback cache so the API runs without Redis.
// Swap this with a real Redis implementation later (StackExchange.Redis).
public sealed class RedisCacheService : IRedisCacheService
{
    private sealed record CacheItem(string Value, DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheItem> _cache = new();
    private readonly ILogger<RedisCacheService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RedisCacheService(ILogger<RedisCacheService> logger) => _logger = logger;

    public Task<CachedAnswer?> GetExactAsync(string question, CancellationToken ct = default)
    {
        var key = ExactKey(question);
        if (!_cache.TryGetValue(key, out var item)) return Task.FromResult<CachedAnswer?>(null);

        if (item.ExpiresAt != null && item.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _cache.TryRemove(key, out _);
            return Task.FromResult<CachedAnswer?>(null);
        }

        try
        {
            var value = JsonSerializer.Deserialize<CachedAnswer>(item.Value, JsonOptions);
            return Task.FromResult<CachedAnswer?>(value);
        }
        catch (JsonException)
        {
            _cache.TryRemove(key, out _);
            return Task.FromResult<CachedAnswer?>(null);
        }
    }

    public Task SetExactAsync(string question, CachedAnswer value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = ExactKey(question);
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        var expiresAt = ttl == null ? null : (DateTimeOffset?)DateTimeOffset.UtcNow.Add(ttl.Value);
        _cache[key] = new CacheItem(payload, expiresAt);
        _logger.LogDebug("Cache set: {Key} (ttl:{Ttl})", key, ttl?.ToString() ?? "none");
        return Task.CompletedTask;
    }

    public Task<object> GetStatsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _cache.Count(kvp => kvp.Value.ExpiresAt != null && kvp.Value.ExpiresAt <= now);

        var stats = new
        {
            total_keys = _cache.Count,
            expired_keys = expired
        };

        return Task.FromResult<object>(stats);
    }

    private static string ExactKey(string question)
    {
        // Keep keys short and stable: exact:<sha256(question)>
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(question ?? ""));
        return "exact:" + Convert.ToHexString(bytes);
    }
}

