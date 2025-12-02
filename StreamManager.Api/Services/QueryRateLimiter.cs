using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services;

public class QueryRateLimiter
{
    private readonly ILogger<QueryRateLimiter> _logger;
    private readonly ResourceLimitsOptions _options;
    
    // Track active queries per user/connection
    private readonly ConcurrentDictionary<string, UserQueryState> _userStates = new();
    
    // Global limits
    private int _activeAdHocQueries = 0;
    private int _activePersistentQueries = 0;

    public QueryRateLimiter(ILogger<QueryRateLimiter> logger, IOptions<ResourceLimitsOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        _logger.LogInformation(
            "QueryRateLimiter initialized - Per-user: {MaxAdHoc} ad-hoc, {MaxPersistent} persistent | " +
            "Global: {MaxTotalAdHoc} ad-hoc, {MaxTotalPersistent} persistent",
            _options.RateLimits.PerUser.MaxAdHocQueries,
            _options.RateLimits.PerUser.MaxPersistentQueries,
            _options.RateLimits.Global.MaxTotalAdHocQueries,
            _options.RateLimits.Global.MaxTotalPersistentQueries);
        
        // Cleanup old entries every minute
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                CleanupOldEntries();
            }
        });
    }

    public RateLimitResult CanExecuteAdHocQuery(string userId)
    {
        var state = _userStates.GetOrAdd(userId, _ => new UserQueryState());
        var maxTotalAdHoc = _options.RateLimits.Global.MaxTotalAdHocQueries;
        var maxPerUser = _options.RateLimits.PerUser.MaxAdHocQueries;
        var maxPerMinute = _options.RateLimits.PerUser.MaxQueriesPerMinute;

        lock (state)
        {
            // Check global limit
            if (_activeAdHocQueries >= maxTotalAdHoc)
            {
                _logger.LogWarning("Global ad-hoc query limit reached: {Count}/{Max}", 
                    _activeAdHocQueries, maxTotalAdHoc);
                return RateLimitResult.Fail("System is at capacity. Please try again in a moment.");
            }

            // Check per-user concurrent query limit
            if (state.ActiveAdHocQueries >= maxPerUser)
            {
                _logger.LogWarning("User {UserId} has reached ad-hoc query limit: {Count}/{Max}", 
                    userId, state.ActiveAdHocQueries, maxPerUser);
                return RateLimitResult.Fail($"You have reached the maximum of {maxPerUser} concurrent queries. Please stop an existing query first.");
            }

            // Check rate limit (queries per minute)
            var now = DateTime.UtcNow;
            var recentQueries = state.QueryTimestamps
                .Where(t => t > now.AddMinutes(-1))
                .ToList();
            
            state.QueryTimestamps = recentQueries;

            if (recentQueries.Count >= maxPerMinute)
            {
                _logger.LogWarning("User {UserId} has exceeded rate limit: {Count}/{Max} queries per minute", 
                    userId, recentQueries.Count, maxPerMinute);
                return RateLimitResult.Fail($"Rate limit exceeded. Maximum {maxPerMinute} queries per minute.");
            }

            // Allow query
            state.ActiveAdHocQueries++;
            state.QueryTimestamps.Add(now);
            Interlocked.Increment(ref _activeAdHocQueries);

            _logger.LogInformation("User {UserId} started ad-hoc query. Active: {UserActive}/{UserMax}, Global: {GlobalActive}/{GlobalMax}", 
                userId, state.ActiveAdHocQueries, maxPerUser, _activeAdHocQueries, maxTotalAdHoc);

            return RateLimitResult.Success();
        }
    }

    public RateLimitResult CanCreatePersistentQuery(string userId)
    {
        var state = _userStates.GetOrAdd(userId, _ => new UserQueryState());
        var maxTotalPersistent = _options.RateLimits.Global.MaxTotalPersistentQueries;
        var maxPerUser = _options.RateLimits.PerUser.MaxPersistentQueries;

        lock (state)
        {
            // Check global limit
            if (_activePersistentQueries >= maxTotalPersistent)
            {
                _logger.LogWarning("Global persistent query limit reached: {Count}/{Max}", 
                    _activePersistentQueries, maxTotalPersistent);
                return RateLimitResult.Fail("System is at capacity for persistent queries.");
            }

            // Check per-user limit
            if (state.ActivePersistentQueries >= maxPerUser)
            {
                _logger.LogWarning("User {UserId} has reached persistent query limit: {Count}/{Max}", 
                    userId, state.ActivePersistentQueries, maxPerUser);
                return RateLimitResult.Fail($"You have reached the maximum of {maxPerUser} persistent queries. Please delete an existing one first.");
            }

            // Allow query
            state.ActivePersistentQueries++;
            Interlocked.Increment(ref _activePersistentQueries);

            _logger.LogInformation("User {UserId} created persistent query. Active: {UserActive}/{UserMax}, Global: {GlobalActive}/{GlobalMax}", 
                userId, state.ActivePersistentQueries, maxPerUser, _activePersistentQueries, maxTotalPersistent);

            return RateLimitResult.Success();
        }
    }

    public void ReleaseAdHocQuery(string userId)
    {
        if (_userStates.TryGetValue(userId, out var state))
        {
            lock (state)
            {
                if (state.ActiveAdHocQueries > 0)
                {
                    state.ActiveAdHocQueries--;
                    Interlocked.Decrement(ref _activeAdHocQueries);
                    
                    _logger.LogInformation("User {UserId} released ad-hoc query. Active: {UserActive}, Global: {GlobalActive}", 
                        userId, state.ActiveAdHocQueries, _activeAdHocQueries);
                }
            }
        }
    }

    public void ReleasePersistentQuery(string userId)
    {
        if (_userStates.TryGetValue(userId, out var state))
        {
            lock (state)
            {
                if (state.ActivePersistentQueries > 0)
                {
                    state.ActivePersistentQueries--;
                    Interlocked.Decrement(ref _activePersistentQueries);
                    
                    _logger.LogInformation("User {UserId} released persistent query. Active: {UserActive}, Global: {GlobalActive}", 
                        userId, state.ActivePersistentQueries, _activePersistentQueries);
                }
            }
        }
    }

    public QueryLimitsInfo GetLimitsInfo(string userId)
    {
        var state = _userStates.GetOrAdd(userId, _ => new UserQueryState());
        
        lock (state)
        {
            return new QueryLimitsInfo
            {
                UserActiveAdHocQueries = state.ActiveAdHocQueries,
                UserMaxAdHocQueries = _options.RateLimits.PerUser.MaxAdHocQueries,
                UserActivePersistentQueries = state.ActivePersistentQueries,
                UserMaxPersistentQueries = _options.RateLimits.PerUser.MaxPersistentQueries,
                GlobalActiveAdHocQueries = _activeAdHocQueries,
                GlobalMaxAdHocQueries = _options.RateLimits.Global.MaxTotalAdHocQueries,
                GlobalActivePersistentQueries = _activePersistentQueries,
                GlobalMaxPersistentQueries = _options.RateLimits.Global.MaxTotalPersistentQueries
            };
        }
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var keysToRemove = _userStates
            .Where(kvp => kvp.Value.LastActivity < cutoff && 
                          kvp.Value.ActiveAdHocQueries == 0 && 
                          kvp.Value.ActivePersistentQueries == 0)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _userStates.TryRemove(key, out _);
        }

        if (keysToRemove.Any())
        {
            _logger.LogInformation("Cleaned up {Count} inactive user states", keysToRemove.Count);
        }
    }

    private class UserQueryState
    {
        public int ActiveAdHocQueries { get; set; }
        public int ActivePersistentQueries { get; set; }
        public List<DateTime> QueryTimestamps { get; set; } = new();
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }
}

public class RateLimitResult
{
    public bool IsAllowed { get; init; }
    public string? ErrorMessage { get; init; }

    public static RateLimitResult Success() => new() { IsAllowed = true };
    public static RateLimitResult Fail(string message) => new() { IsAllowed = false, ErrorMessage = message };
}

public class QueryLimitsInfo
{
    public int UserActiveAdHocQueries { get; init; }
    public int UserMaxAdHocQueries { get; init; }
    public int UserActivePersistentQueries { get; init; }
    public int UserMaxPersistentQueries { get; init; }
    public int GlobalActiveAdHocQueries { get; init; }
    public int GlobalMaxAdHocQueries { get; init; }
    public int GlobalActivePersistentQueries { get; init; }
    public int GlobalMaxPersistentQueries { get; init; }
}
