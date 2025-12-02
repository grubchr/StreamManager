using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Data;
using StreamManager.Api.Hubs;

namespace StreamManager.Api.Services;

public class TopicProxyService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<KsqlHub> _hubContext;
    private readonly ILogger<TopicProxyService> _logger;
    private readonly ConcurrentDictionary<string, IConsumer<string, string>> _consumers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _consumerCancellations = new();
    private readonly string _kafkaBootstrapServers;

    public TopicProxyService(IServiceProvider serviceProvider, IHubContext<KsqlHub> hubContext, 
        ILogger<TopicProxyService> logger, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
        _kafkaBootstrapServers = configuration.GetConnectionString("Kafka") ?? "localhost:9092";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndConsumeActiveStreams(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TopicProxyService execution");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ScanAndConsumeActiveStreams(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<StreamManagerDbContext>();

        var activeStreams = await context.StreamDefinitions
            .Where(s => s.IsActive && !string.IsNullOrEmpty(s.OutputTopic))
            .ToListAsync(cancellationToken);

        // Start consumers for new topics
        foreach (var stream in activeStreams)
        {
            if (!_consumers.ContainsKey(stream.OutputTopic!) && !string.IsNullOrWhiteSpace(stream.OutputTopic))
            {
                try
                {
                    var consumer = CreateConsumer(stream.OutputTopic);
                    var cts = new CancellationTokenSource();
                    
                    _consumers[stream.OutputTopic] = consumer;
                    _consumerCancellations[stream.OutputTopic] = cts;
                    
                    // Start consuming in background task with dedicated cancellation token
                    _ = Task.Run(() => ConsumeTopicAsync(consumer, stream.OutputTopic, cts.Token), cancellationToken);
                    
                    _logger.LogInformation("Started consuming topic {TopicName}", stream.OutputTopic);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start consumer for topic {TopicName}", stream.OutputTopic);
                }
            }
        }

        // Stop consumers for removed topics
        var activeTopics = activeStreams.Where(s => !string.IsNullOrEmpty(s.OutputTopic)).Select(s => s.OutputTopic!).ToHashSet();
        var consumersToRemove = _consumers.Keys.Where(topic => !activeTopics.Contains(topic)).ToList();

        foreach (var topic in consumersToRemove)
        {
            // Step 1: Signal the consumption loop to stop
            if (_consumerCancellations.TryRemove(topic, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cancelling consumer for topic {TopicName}", topic);
                }
            }
            
            // Step 2: Remove consumer from dictionary so it won't be used
            if (_consumers.TryRemove(topic, out var consumer))
            {
                // Step 3: Dispose in a background task with delays to allow graceful shutdown
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Wait for consumption loop to detect cancellation and exit
                        // Consumer.Consume() can block for up to 1 second, so we need to wait at least that long
                        await Task.Delay(1500);
                        
                        // Close gracefully
                        consumer.Close();
                        
                        // Small delay before dispose
                        await Task.Delay(200);
                        
                        consumer.Dispose();
                        
                        _logger.LogInformation("Stopped consuming topic {TopicName}", topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing consumer for topic {TopicName}", topic);
                    }
                    finally
                    {
                        cts?.Dispose();
                    }
                });
            }
        }
    }

    private async Task ConsumeTopicAsync(IConsumer<string, string> consumer, string topicName, CancellationToken cancellationToken)
    {
        try
        {
            int retryCount = 0;
            const int maxRetries = 10;
            
            while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // ksqlDB creates topics in uppercase, but our stored name might be lowercase
        var actualTopicName = topicName.ToUpperInvariant();
        _logger.LogInformation("Subscribing to topic {TopicName} (actual: {ActualTopicName})", topicName, actualTopicName);
        consumer.Subscribe(actualTopicName);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                            if (consumeResult?.Message != null)
                            {
                                var messageValue = consumeResult.Message.Value ?? "";
                                var messageKey = consumeResult.Message.Key ?? "";
                                
                                _logger.LogInformation("Raw message from topic {TopicName}: Key='{Key}', Value='{Value}' (Length: {Length}), Offset={Offset}, Partition={Partition}", 
                                    topicName, messageKey, messageValue, messageValue.Length, consumeResult.Offset, consumeResult.Partition);

                                // Send the message to the SignalR group (use original topic name as group name)
                                await _hubContext.Clients.Group(topicName).SendAsync("ReceiveMessage", messageValue, cancellationToken);
                                
                                if (string.IsNullOrEmpty(messageValue))
                                {
                                    _logger.LogWarning("Received empty message value from topic {TopicName} - Headers: {Headers}", 
                                        topicName, string.Join(", ", consumeResult.Message.Headers?.Select(h => $"{h.Key}={System.Text.Encoding.UTF8.GetString(h.GetValueBytes())}") ?? new string[0]));
                                }
                            }
                            else if (consumeResult != null)
                            {
                                _logger.LogDebug("Received empty message from topic {TopicName}", topicName);
                            }
                        }
                        catch (ConsumeException ex) when (ex.Error.Code != ErrorCode.UnknownTopicOrPart)
                        {
                            _logger.LogError(ex, "Error consuming message from topic {TopicName}", topicName);
                        }
                    }
                    break; // If we get here, consumption was successful
                }
                catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    retryCount++;
                    _logger.LogWarning("Topic {TopicName} not available yet (attempt {Attempt}/{MaxAttempts}), retrying in 5 seconds...", topicName, retryCount, maxRetries);
                    
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, "Topic {TopicName} still not available after {MaxRetries} attempts", topicName, maxRetries);
                        throw;
                    }
                    
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Consumer for topic {TopicName} was cancelled", topicName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in consumer for topic {TopicName}", topicName);
        }
        finally
        {
            try
            {
                consumer.Unsubscribe();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing from topic {TopicName}", topicName);
            }
        }
    }

    private IConsumer<string, string> CreateConsumer(string topicName)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaBootstrapServers,
            GroupId = $"stream-manager-proxy-{Environment.MachineName}-{DateTime.UtcNow.Ticks}",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            StatisticsIntervalMs = 5000,
            SessionTimeoutMs = 6000,
            AutoCommitIntervalMs = 100,
            EnablePartitionEof = true
        };

        return new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Consumer error: {Reason}", e.Reason))
            .Build();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TopicProxyService stopping - disposing all consumers safely");
        
        // Step 1: Cancel all consumption loops
        foreach (var cts in _consumerCancellations.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch { }
        }
        
        // Step 2: Wait for consumption loops to exit (Consume() can block up to 1 second)
        await Task.Delay(1500, cancellationToken);
        
        // Step 3: Get all consumers and clear dictionaries
        var consumersToCleanup = new List<(IConsumer<string, string> Consumer, CancellationTokenSource? Cts)>();
        foreach (var kvp in _consumers)
        {
            if (_consumers.TryRemove(kvp.Key, out var consumer))
            {
                _consumerCancellations.TryRemove(kvp.Key, out var cts);
                consumersToCleanup.Add((consumer, cts));
            }
        }
        
        // Step 4: Dispose consumers in parallel background tasks
        var disposalTasks = consumersToCleanup.Select(item => Task.Run(() =>
        {
            try
            {
                item.Consumer.Close();
                Thread.Sleep(100);
                item.Consumer.Dispose();
                item.Cts?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing consumer during shutdown");
            }
        })).ToArray();
        
        // Step 5: Wait for all disposal tasks to complete (with timeout)
        try
        {
            await Task.WhenAny(
                Task.WhenAll(disposalTasks),
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeout or error waiting for consumer disposal");
        }
        
        _logger.LogInformation("TopicProxyService stopped - all consumers disposed");
        await base.StopAsync(cancellationToken);
    }
}