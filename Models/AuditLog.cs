namespace NexusApp.Models;

/// <summary>
/// Represents an audit log entry for tracking system events
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    
    public DateTime Timestamp { get; set; }
    
    public string EventType { get; set; } = string.Empty;
    
    public string EntityType { get; set; } = string.Empty;
    
    public int? EntityId { get; set; }
    
    public string Action { get; set; } = string.Empty;
    
    public string? OldValue { get; set; }
    
    public string? NewValue { get; set; }
    
    public string Details { get; set; } = string.Empty;

    public AuditLog() { }

    public AuditLog(string eventType, string action, string details = "")
    {
        Timestamp = DateTime.UtcNow;
        EventType = eventType;
        Action = action;
        Details = details;
    }
}
