using Microsoft.AspNetCore.SignalR;
using StreamManager.Api.Data;
using StreamManager.Api.Services;
using System.Runtime.CompilerServices;

namespace StreamManager.Api.Hubs;

public class KsqlHub : Hub
{
    private readonly AdHocKsqlService _adHocKsqlService;
    private readonly QueryRateLimiter _rateLimiter;
    private readonly ILogger<KsqlHub> _logger;
    private readonly StreamManagerDbContext _context;

    public KsqlHub(
        AdHocKsqlService adHocKsqlService, 
        QueryRateLimiter rateLimiter,
        ILogger<KsqlHub> logger, 
        StreamManagerDbContext context)
    {
        _adHocKsqlService = adHocKsqlService;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _context = context;
    }

    public async IAsyncEnumerable<string> ExecuteAdHoc(string ksql, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ksql))
            throw new ArgumentException("KSQL query cannot be empty", nameof(ksql));

        var userId = Context.ConnectionId; // In production, use actual user ID from authentication

        // Check rate limits
        var rateLimitResult = _rateLimiter.CanExecuteAdHocQuery(userId);
        if (!rateLimitResult.IsAllowed)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}: {Message}", userId, rateLimitResult.ErrorMessage);
            throw new InvalidOperationException(rateLimitResult.ErrorMessage);
        }

        try
        {
            _logger.LogInformation("Executing ad-hoc query for connection {ConnectionId}", Context.ConnectionId);

            await foreach (var result in _adHocKsqlService.ExecuteQueryStreamAsync(ksql, cancellationToken))
            {
                yield return result;
            }
        }
        finally
        {
            // Release the query slot when done
            _rateLimiter.ReleaseAdHocQuery(userId);
        }
    }
    
    public async Task<QueryLimitsInfo> GetQueryLimits()
    {
        var userId = Context.ConnectionId; // In production, use actual user ID
        return _rateLimiter.GetLimitsInfo(userId);
    }

    public async Task JoinStreamGroup(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName))
            throw new ArgumentException("Topic name cannot be empty", nameof(topicName));

        await Groups.AddToGroupAsync(Context.ConnectionId, topicName);
        _logger.LogInformation("Connection {ConnectionId} joined stream group {TopicName}", Context.ConnectionId, topicName);
    }

    public async Task LeaveStreamGroup(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName))
            throw new ArgumentException("Topic name cannot be empty", nameof(topicName));

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, topicName);
        _logger.LogInformation("Connection {ConnectionId} left stream group {TopicName}", Context.ConnectionId, topicName);
    }

    public async IAsyncEnumerable<string> ViewStream(string streamId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            throw new ArgumentException("Stream ID cannot be empty", nameof(streamId));

        if (!Guid.TryParse(streamId, out var guid))
            throw new ArgumentException("Invalid stream ID format", nameof(streamId));

        _logger.LogInformation("Starting stream view for stream {StreamId}, connection {ConnectionId}", streamId, Context.ConnectionId);

        // Get the stream from database to find its output topic
        var stream = await _context.StreamDefinitions.FindAsync(guid);
        if (stream == null || !stream.IsActive || string.IsNullOrWhiteSpace(stream.OutputTopic))
        {
            throw new ArgumentException("Stream not found or not active", nameof(streamId));
        }

        var topicName = stream.OutputTopic;
        _logger.LogInformation("Joining group for topic: {TopicName}", topicName);
        
        // Join the SignalR group for this stream to receive Kafka messages
        await JoinStreamGroup(topicName);

        // This will keep the connection alive and let the TopicProxyService push messages via SignalR groups
        // The actual data will come from the TopicProxyService, not this method directly
        
        // Keep the stream alive while client is connected
        // Real data comes from TopicProxyService via SignalR groups, this just keeps connection alive
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                yield return ""; // Yield empty to keep the stream alive
                await Task.Delay(5000, cancellationToken); // Check every 5 seconds
            }
        }
        finally
        {
            // Cleanup when done (this runs even if cancelled)
            try { await LeaveStreamGroup(topicName); } catch { /* Ignore cleanup errors */ }
        }
    }
}