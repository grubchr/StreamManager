using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace StreamManager.Api.Services;

public class AdHocKsqlService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AdHocKsqlService> _logger;
    private readonly KsqlQueryValidator _validator;
    private readonly string _ksqlDbUrl;

    public AdHocKsqlService(
        HttpClient httpClient, 
        ILogger<AdHocKsqlService> logger, 
        KsqlQueryValidator validator,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _validator = validator;
        _ksqlDbUrl = configuration.GetConnectionString("KsqlDb") ?? "http://localhost:8088";
    }

    public async IAsyncEnumerable<string> ExecuteQueryStreamAsync(
        string ksql, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting ksql query: {Ksql}", ksql);
        
        // Validate query
        var validation = _validator.ValidateAdHocQuery(ksql);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Query validation failed: {Error}", validation.ErrorMessage);
            throw new InvalidOperationException($"Query validation failed: {validation.ErrorMessage}");
        }

        // Log warnings
        foreach (var warning in validation.Warnings)
        {
            _logger.LogInformation("Query warning: {Warning}", warning);
        }

        // Ensure EMIT CHANGES is present
        if (!ksql.ToUpperInvariant().Contains("EMIT CHANGES"))
        {
            ksql = ksql.TrimEnd(';') + " EMIT CHANGES;";
            _logger.LogInformation("Added EMIT CHANGES to query");
        }
        
        var payload = new
        {
            ksql = ksql,
            streamsProperties = _validator.GenerateStreamProperties(isAdHoc: true)
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;

        _logger.LogInformation("Sending request to ksqlDB: {Url}", $"{_ksqlDbUrl}/query");
        _logger.LogInformation("About to send HTTP request with streaming");
        
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_ksqlDbUrl}/query")
        {
            Content = httpContent
        };
        
        response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _logger.LogInformation("Got HTTP response: {StatusCode}", response.StatusCode);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("About to read response stream");
        stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        _logger.LogInformation("Got response stream, stream length: {Length}", stream.CanSeek ? stream.Length : -1);
        reader = new StreamReader(stream);
        _logger.LogInformation("Created StreamReader successfully");

        string? line;
        int lineCount = 0;
        
        _logger.LogInformation("Starting to read lines from stream");
        
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            _logger.LogInformation("Read line {LineCount}: got line with length {Length}", lineCount + 1, line?.Length ?? 0);
            
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Query cancelled after {LineCount} lines", lineCount);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                lineCount++;
                _logger.LogDebug("Processing line {LineCount}: {Line}", lineCount, line.Length > 100 ? line[..100] + "..." : line);
                
                _logger.LogDebug("Original line length: {Length}", line.Length);
                _logger.LogDebug("Original line content: {Line}", line);
                
                // ksqlDB returns array-style JSON, need to extract individual objects
                var cleanLine = line.TrimEnd(',');
                if (cleanLine.StartsWith("["))
                {
                    // First line might be the start of the array, extract the first object
                    cleanLine = cleanLine.TrimStart('[');
                }
                if (cleanLine.EndsWith("]"))
                {
                    // Last line might end the array
                    cleanLine = cleanLine.TrimEnd(']');
                }
                
                _logger.LogDebug("Cleaned line length: {Length}", cleanLine.Length);
                _logger.LogDebug("Cleaned line content: {CleanLine}", cleanLine);
                
                if (!string.IsNullOrWhiteSpace(cleanLine))
                {
                    yield return cleanLine;
                }
            }
        }
        
        _logger.LogInformation("Query stream completed after {LineCount} lines", lineCount);
    }

    private static string? ProcessMetadataLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<JsonElement>(line);
            if (metadata.TryGetProperty("columnNames", out var columnNames) && 
                columnNames.ValueKind == JsonValueKind.Array)
            {
                var names = columnNames.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
                
                if (names.Length > 0)
                {
                    var schema = string.Join(",", names);
                    var headerResponse = new { header = new { schema = schema } };
                    return JsonSerializer.Serialize(headerResponse);
                }
            }
        }
        catch (Exception)
        {
            // If parsing fails, return original line
        }
        
        return line;
    }

    private static string? ProcessDataRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            var rowData = JsonSerializer.Deserialize<JsonElement>(line);
            if (rowData.ValueKind == JsonValueKind.Array)
            {
                var columns = rowData.EnumerateArray().Select(e => 
                    e.ValueKind == JsonValueKind.Null ? null : e.ToString()).ToArray();
                var rowResponse = new { row = new { columns = columns } };
                return JsonSerializer.Serialize(rowResponse);
            }
        }
        catch (Exception)
        {
            // If parsing fails, return original line
        }
        
        return line;
    }
}