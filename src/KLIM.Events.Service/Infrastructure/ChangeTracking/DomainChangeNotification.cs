namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Domain-level notification for batch processing completion or system events
/// Used for diagnostics and monitoring of the change tracking system
/// </summary>
/// <param name="NotificationId">Unique identifier for this notification</param>
/// <param name="NotificationType">Type of notification (e.g., "BatchProcessed", "SystemHealthy", "ErrorOccurred")</param>
/// <param name="Message">Human-readable message describing the notification</param>
/// <param name="OccurredAt">UTC timestamp when the notification was generated</param>
/// <param name="Source">Source system or component that generated the notification</param>
/// <param name="Details">Additional structured data about the notification</param>
/// <param name="CorrelationId">Optional correlation ID for distributed tracing</param>
public sealed record DomainChangeNotification(
    Guid NotificationId,
    string NotificationType,
    string Message,
    DateTimeOffset OccurredAt,
    string Source,
    Dictionary<string, object?>? Details = null,
    Guid? CorrelationId = null
);