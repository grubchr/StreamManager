namespace StreamManager.Api.Models;

public class StreamDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string KsqlScript { get; set; }
    public string? KsqlQueryId { get; set; }
    public string? KsqlStreamName { get; set; }
    public string? OutputTopic { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}