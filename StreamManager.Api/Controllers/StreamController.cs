using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Data;
using StreamManager.Api.Models;
using System.Text;
using System.Text.Json;

namespace StreamManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private readonly StreamManagerDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ILogger<StreamController> _logger;
    private readonly string _ksqlDbUrl;

    public StreamController(StreamManagerDbContext context, HttpClient httpClient, ILogger<StreamController> logger, IConfiguration configuration)
    {
        _context = context;
        _httpClient = httpClient;
        _logger = logger;
        _ksqlDbUrl = configuration.GetConnectionString("KsqlDb") ?? "http://localhost:8088";
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StreamDefinition>>> GetStreams()
    {
        return await _context.StreamDefinitions.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<StreamDefinition>> GetStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        return stream;
    }

    [HttpPost]
    public async Task<ActionResult<StreamDefinition>> CreateStream(CreateStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.KsqlScript))
            return BadRequest("Name and KsqlScript are required");

        var stream = new StreamDefinition
        {
            Name = request.Name.Trim(),
            KsqlScript = request.KsqlScript.Trim(),
            IsActive = false
        };

        _context.StreamDefinitions.Add(stream);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetStream), new { id = stream.Id }, stream);
    }

    [HttpPost("{id}/deploy")]
    public async Task<ActionResult> DeployStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        try
        {
            var safeName = GenerateSafeName(stream.Name);
            var createStreamSql = $"CREATE STREAM {safeName} AS {stream.KsqlScript};";
            
            var payload = new { ksql = createStreamSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
            
            if (response.IsSuccessStatusCode)
            {
                // Store the actual stream name for later cleanup
                stream.KsqlStreamName = safeName;
                // Query ksqlDB to get the actual created query ID and topic
                var showQueriesPayload = new { ksql = "SHOW QUERIES;" };
                var showQueriesJson = JsonSerializer.Serialize(showQueriesPayload);
                var showQueriesContent = new StringContent(showQueriesJson, Encoding.UTF8, "application/json");
                
                var queriesResponse = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", showQueriesContent);
                
                if (queriesResponse.IsSuccessStatusCode)
                {
                    var queriesResult = await queriesResponse.Content.ReadAsStringAsync();
                    
                    // Parse the response to find the most recent query that matches our stream name
                    string actualQueryId = null;
                    string actualTopic = null;
                    
                    using var doc = JsonDocument.Parse(queriesResult);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var firstResult = doc.RootElement[0];
                        if (firstResult.TryGetProperty("queries", out var queries))
                        {
                            // Find the query with our stream name (look for most recent one)
                            foreach (var query in queries.EnumerateArray())
                            {
                                if (query.TryGetProperty("queryString", out var queryString) &&
                                    queryString.GetString()?.Contains($"CREATE STREAM {safeName}", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    if (query.TryGetProperty("id", out var idProp))
                                        actualQueryId = idProp.GetString();
                                    
                                    if (query.TryGetProperty("sinks", out var sinks) && sinks.GetArrayLength() > 0)
                                        actualTopic = sinks[0].GetString();
                                    
                                    // Take the last match (most recent)
                                }
                            }
                        }
                    }
                    
                    // If we couldn't parse the response, fall back to safer defaults
                    if (string.IsNullOrWhiteSpace(actualQueryId))
                        actualQueryId = $"CSAS_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";
                    
                    if (string.IsNullOrWhiteSpace(actualTopic))
                        actualTopic = safeName.ToUpperInvariant();
                    
                    stream.KsqlQueryId = actualQueryId;
                    stream.OutputTopic = actualTopic;
                    stream.IsActive = true;
                    
                    await _context.SaveChangesAsync();
                    
                    _logger.LogInformation("Successfully deployed stream {StreamId} with query ID {QueryId} and topic {Topic}", id, actualQueryId, actualTopic);
                    return Ok(new { QueryId = actualQueryId, OutputTopic = actualTopic });
                }
                else
                {
                    // If we can't query, at least use better defaults
                    var fallbackQueryId = $"CSAS_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";
                    var fallbackTopic = safeName.ToUpperInvariant();
                    
                    stream.KsqlQueryId = fallbackQueryId;
                    stream.OutputTopic = fallbackTopic;
                    stream.IsActive = true;
                    
                    await _context.SaveChangesAsync();
                    
                    _logger.LogWarning("Could not query ksqlDB for actual IDs, using fallbacks for stream {StreamId}", id);
                    return Ok(new { QueryId = fallbackQueryId, OutputTopic = fallbackTopic });
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to deploy stream {StreamId}: {Error}", id, errorContent);
                return BadRequest($"Failed to deploy stream: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deploying stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("{id}/stop")]
    public async Task<ActionResult> StopStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (!stream.IsActive)
            return BadRequest("Stream is not active");

        try
        {
            if (string.IsNullOrWhiteSpace(stream.KsqlQueryId))
                return BadRequest("Stream has no query ID to terminate");
            
            var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
            var payload = new { ksql = terminateSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
            
            if (response.IsSuccessStatusCode)
            {
                stream.IsActive = false;
                stream.KsqlQueryId = null; // Clear since it's terminated
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Successfully stopped stream {StreamId} with query ID {QueryId}", id, stream.KsqlQueryId);
                return Ok();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to stop stream {StreamId}: {Error}", id, errorContent);
                return BadRequest($"Failed to stop stream: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateStream(Guid id, UpdateStreamRequest request)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.KsqlScript))
            return BadRequest("Name and KsqlScript are required");

        try
        {
            // If stream is active, stop it first
            if (stream.IsActive && !string.IsNullOrWhiteSpace(stream.KsqlQueryId))
            {
                var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
                var payload = new { ksql = terminateSql };
                var jsonContent = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
                
                // Drop the stream if it exists
                var safeName = GenerateSafeName(stream.Name);
                var dropSql = $"DROP STREAM IF EXISTS {safeName};";
                var dropPayload = new { ksql = dropSql };
                var dropJsonContent = JsonSerializer.Serialize(dropPayload);
                var dropHttpContent = new StringContent(dropJsonContent, Encoding.UTF8, "application/json");
                
                await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", dropHttpContent);
            }

            // Update the stream definition
            stream.Name = request.Name.Trim();
            stream.KsqlScript = request.KsqlScript.Trim();
            stream.IsActive = false;
            stream.KsqlQueryId = null;
            stream.KsqlStreamName = null;
            stream.OutputTopic = null;

            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Successfully updated stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        try
        {
            // Step 1: Terminate the query if active
            if (stream.IsActive && !string.IsNullOrWhiteSpace(stream.KsqlQueryId))
            {
                var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
                var payload = new { ksql = terminateSql };
                var jsonContent = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                var terminateResponse = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
                
                if (!terminateResponse.IsSuccessStatusCode)
                {
                    var errorContent = await terminateResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to terminate query {QueryId} for stream {StreamId}: {Error}", 
                        stream.KsqlQueryId, id, errorContent);
                }
            }

            // Step 2: Drop the stream and delete its topic
            if (!string.IsNullOrWhiteSpace(stream.KsqlStreamName))
            {
                var dropSql = $"DROP STREAM IF EXISTS {stream.KsqlStreamName} DELETE TOPIC;";
                var dropPayload = new { ksql = dropSql };
                var dropJsonContent = JsonSerializer.Serialize(dropPayload);
                var dropHttpContent = new StringContent(dropJsonContent, Encoding.UTF8, "application/json");
                
                var dropResponse = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", dropHttpContent);
                
                if (!dropResponse.IsSuccessStatusCode)
                {
                    var errorContent = await dropResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning("Failed to drop stream {StreamName} for stream {StreamId}: {Error}", 
                        stream.KsqlStreamName, id, errorContent);
                }
                else
                {
                    _logger.LogInformation("Successfully dropped stream {StreamName} and topic for stream {StreamId}", 
                        stream.KsqlStreamName, id);
                }
            }

            // Step 3: Remove from database
            _context.StreamDefinitions.Remove(stream);
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Successfully deleted stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    private static string GenerateSafeName(string name)
    {
        // Generate a safe name for ksqlDB (alphanumeric + underscore, starts with letter)
        var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName) || !char.IsLetter(safeName[0]))
            safeName = "STREAM_" + safeName;
        
        return $"VIEW_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";
    }
}

public record CreateStreamRequest(string Name, string KsqlScript);
public record UpdateStreamRequest(string Name, string KsqlScript);