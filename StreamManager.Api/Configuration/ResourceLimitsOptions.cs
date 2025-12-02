namespace StreamManager.Api.Configuration;

public class ResourceLimitsOptions
{
    public const string SectionName = "ResourceLimits";
    
    public QueryLimits Query { get; set; } = new();
    public RateLimits RateLimits { get; set; } = new();
    public StreamPropertiesConfig StreamProperties { get; set; } = new();
}

public class QueryLimits
{
    public int MaxQueryLength { get; set; } = 5000;
    public int MaxJoins { get; set; } = 3;
    public int MaxWindows { get; set; } = 2;
    public int MaxQueryTimeoutMinutes { get; set; } = 5;
    public List<string> BlockedKeywords { get; set; } = new()
    {
        "CREATE CONNECTOR",
        "DROP CONNECTOR",
        "PRINT"
    };
}

public class RateLimits
{
    public PerUserLimits PerUser { get; set; } = new();
    public GlobalLimits Global { get; set; } = new();
}

public class PerUserLimits
{
    public int MaxAdHocQueries { get; set; } = 2;
    public int MaxPersistentQueries { get; set; } = 5;
    public int MaxQueriesPerMinute { get; set; } = 30;
}

public class GlobalLimits
{
    public int MaxTotalAdHocQueries { get; set; } = 20;
    public int MaxTotalPersistentQueries { get; set; } = 30;
}

public class StreamPropertiesConfig
{
    public Dictionary<string, object> AdHoc { get; set; } = new();
    public Dictionary<string, object> Persistent { get; set; } = new();
}
