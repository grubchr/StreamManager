using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StreamManager.Api.Configuration;

namespace StreamManager.Api.Services;

public class KsqlQueryValidator
{
    private readonly ILogger<KsqlQueryValidator> _logger;
    private readonly ResourceLimitsOptions _options;

    public KsqlQueryValidator(ILogger<KsqlQueryValidator> logger, IOptions<ResourceLimitsOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        _logger.LogInformation(
            "KsqlQueryValidator initialized - MaxLength: {MaxLength}, MaxJoins: {MaxJoins}, MaxWindows: {MaxWindows}, Blocked: {BlockedCount} keywords",
            _options.Query.MaxQueryLength,
            _options.Query.MaxJoins,
            _options.Query.MaxWindows,
            _options.Query.BlockedKeywords.Count);
    }

    public ValidationResult ValidateAdHocQuery(string ksql)
    {
        if (string.IsNullOrWhiteSpace(ksql))
            return ValidationResult.Fail("Query cannot be empty");

        var normalizedQuery = ksql.Trim().ToUpperInvariant();

        // 1. Check query length
        if (ksql.Length > _options.Query.MaxQueryLength)
            return ValidationResult.Fail($"Query exceeds maximum length of {_options.Query.MaxQueryLength} characters");

        // 2. Must be a SELECT statement
        if (!normalizedQuery.StartsWith("SELECT"))
            return ValidationResult.Fail("Ad-hoc queries must be SELECT statements");

        // 3. Block dangerous operations
        foreach (var blocked in _options.Query.BlockedKeywords)
        {
            if (normalizedQuery.Contains(blocked.ToUpperInvariant()))
                return ValidationResult.Fail($"Operation '{blocked}' is not allowed in ad-hoc queries");
        }

        // 4. Must have EMIT CHANGES for push queries
        if (!normalizedQuery.Contains("EMIT CHANGES"))
        {
            _logger.LogInformation("Adding EMIT CHANGES to query");
            // This will be handled by the caller
        }

        // 5. Check for expensive operations
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            return ValidationResult.Fail($"Query contains {joinCount} JOINs. Maximum allowed: {_options.Query.MaxJoins}");
        else if (joinCount > 0)
            warnings.Add($"Query contains {joinCount} JOIN(s) which may be resource intensive");

        var windowCount = Regex.Matches(normalizedQuery, @"\bWINDOW\b").Count;
        if (windowCount > _options.Query.MaxWindows)
            return ValidationResult.Fail($"Query contains {windowCount} WINDOWs. Maximum allowed: {_options.Query.MaxWindows}");
        else if (windowCount > 0)
            warnings.Add($"Query contains {windowCount} WINDOW(s) which may be resource intensive");

        // 6. Recommend LIMIT for unbounded queries
        if (!normalizedQuery.Contains("LIMIT") && !normalizedQuery.Contains("WHERE"))
            warnings.Add("Consider adding a LIMIT or WHERE clause to restrict result set");

        return ValidationResult.Success(warnings);
    }

    public ValidationResult ValidatePersistentQuery(string ksql, string streamName)
    {
        if (string.IsNullOrWhiteSpace(ksql))
            return ValidationResult.Fail("Query cannot be empty");

        if (string.IsNullOrWhiteSpace(streamName))
            return ValidationResult.Fail("Stream name cannot be empty");

        var normalizedQuery = ksql.Trim().ToUpperInvariant();

        // 1. Check query length
        if (ksql.Length > _options.Query.MaxQueryLength)
            return ValidationResult.Fail($"Query exceeds maximum length of {_options.Query.MaxQueryLength} characters");

        // 2. Must be a SELECT statement (we'll wrap it in CREATE STREAM)
        if (!normalizedQuery.StartsWith("SELECT"))
            return ValidationResult.Fail("Persistent queries must be SELECT statements");

        // 3. Block dangerous operations
        foreach (var blocked in _options.Query.BlockedKeywords)
        {
            if (normalizedQuery.Contains(blocked.ToUpperInvariant()))
                return ValidationResult.Fail($"Operation '{blocked}' is not allowed");
        }

        // 4. Cannot have LIMIT in persistent queries
        if (normalizedQuery.Contains("LIMIT"))
            return ValidationResult.Fail("LIMIT is not allowed in persistent queries (they run continuously)");

        // 5. Check for expensive operations - more lenient for persistent
        var warnings = new List<string>();
        var joinCount = Regex.Matches(normalizedQuery, @"\bJOIN\b").Count;
        if (joinCount > _options.Query.MaxJoins)
            warnings.Add($"Query contains {joinCount} JOINs which will consume resources continuously");

        return ValidationResult.Success(warnings);
    }

    public Dictionary<string, object> GenerateStreamProperties(bool isAdHoc = true)
    {
        // Start with base properties
        var properties = new Dictionary<string, object>
        {
            ["ksql.streams.auto.offset.reset"] = "earliest"
        };

        // Merge with configured properties
        var sourceProps = isAdHoc 
            ? _options.StreamProperties.AdHoc 
            : _options.StreamProperties.Persistent;

        foreach (var kvp in sourceProps)
        {
            properties[kvp.Key] = kvp.Value;
        }

        return properties;
    }
}

public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string> Warnings { get; init; } = new();

    public static ValidationResult Success(List<string>? warnings = null)
    {
        return new ValidationResult
        {
            IsValid = true,
            Warnings = warnings ?? new List<string>()
        };
    }

    public static ValidationResult Fail(string errorMessage)
    {
        return new ValidationResult
        {
            IsValid = false,
            ErrorMessage = errorMessage
        };
    }
}
