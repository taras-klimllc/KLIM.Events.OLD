using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.ChangeTracking;
using MassTransit;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Handles message publishing to RabbitMQ via MassTransit with KLIM-standard routing and payload monitoring
/// </summary>
public sealed class MessagePublisher
{
    private readonly IBus _bus;
    private readonly ILogger<MessagePublisher> _logger;
    private readonly RabbitMQOptions _rabbitOptions;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Payload size monitoring and safety limits
    private const int MAX_PAYLOAD_SIZE_BYTES = 2 * 1024 * 1024; // 2MB reasonable limit
    private const int LARGE_PAYLOAD_THRESHOLD = 500 * 1024; // 500KB warning threshold
    private const int MAX_TEXT_FIELD_LENGTH = 10 * 1024; // 10KB per text field

    public MessagePublisher(IBus bus, ILogger<MessagePublisher> logger, IOptions<RabbitMQOptions> rabbitOptions)
    {
        _bus = bus;
        _logger = logger;
        _rabbitOptions = rabbitOptions.Value;
    }

    public async Task<PublishResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var messageType = ResolveMessageType(message.Type);
        if (messageType == null)
        {
            _logger.LogError("Cannot find message type {Type} for message {Id}", message.Type, message.Id);
            return PublishResult.Failed("Type not found");
        }

        var messageObj = JsonSerializer.Deserialize(message.Payload, messageType, _jsonOptions);
        if (messageObj == null)
        {
            _logger.LogError("Failed to deserialize message {Id} of type {Type}", message.Id, message.Type);
            return PublishResult.Failed("Deserialization failed");
        }

        // Payload size monitoring and safety checks
        var payloadSizeBytes = System.Text.Encoding.UTF8.GetByteCount(message.Payload);
        MonitorPayloadSize(message.Id, payloadSizeBytes);

        if (payloadSizeBytes > MAX_PAYLOAD_SIZE_BYTES)
        {
            _logger.LogError("Payload too large: {PayloadSize}KB for message {Id}, max allowed: {MaxSize}KB",
                payloadSizeBytes / 1024, message.Id, MAX_PAYLOAD_SIZE_BYTES / 1024);
            return PublishResult.Failed($"Payload too large: {payloadSizeBytes / 1024}KB");
        }

        var headers = ParseHeaders(message.Headers);
        var exchange = _rabbitOptions.ExchangeName;
        var routingKey = GetKlimStandardRoutingKey(messageType, messageObj);

        _logger.LogDebug("Publishing message {Id} to exchange {Exchange} with routing key {RoutingKey} (size: {PayloadSize}KB)",
            message.Id, exchange, routingKey, payloadSizeBytes / 1024);

        // KLIM Standard: Publish with proper routing key and headers
        await _bus.Publish(messageObj, ctx =>
        {
            // Set KLIM standard routing key
            ctx.SetRoutingKey(routingKey);

            // KLIM Standard: Set standard headers
            ctx.Headers.Set("source", "events.service");
            ctx.Headers.Set("version", GetMessageVersion(messageType));
            ctx.Headers.Set("correlation_id", message.Id.ToString());
            ctx.Headers.Set("timestamp_utc", DateTime.UtcNow.ToString("O"));
            ctx.Headers.Set("payload_size_bytes", payloadSizeBytes.ToString());

            // Add entity-specific headers for DataChangedV1
            if (messageObj is DataChangedV1 dataChange)
            {
                ctx.Headers.Set("event_type", $"{dataChange.EntityType.ToLowerInvariant()}.{dataChange.Operation.ToLowerInvariant()}");
                ctx.Headers.Set("entity_type", dataChange.EntityType.ToLowerInvariant());
                ctx.Headers.Set("entity_id", dataChange.EntityId.ToString());
            }

            // Add custom headers from message
            if (headers != null)
            {
                foreach (var header in headers)
                    ctx.Headers.Set(header.Key, header.Value);
            }

            // MassTransit standard headers
            ctx.MessageId = message.Id;
            ctx.Headers.Set("OccurredAt", message.OccurredAt.ToString("O"));
            ctx.Headers.Set("PayloadSizeKB", (payloadSizeBytes / 1024).ToString());

        }, cancellationToken);

        // Log our own clean message publishing event with short type name
        var shortTypeName = GetShortTypeName(messageType);
        _logger.LogInformation("PUBLISHED {MessageType} {MessageId} -> {Exchange}/{RoutingKey} ({PayloadSize}KB)",
            shortTypeName, message.Id, exchange, routingKey, payloadSizeBytes / 1024);

        var changeDetails = ExtractChangeDetails(messageObj);
        return PublishResult.Success(exchange, routingKey, changeDetails);
    }

    /// <summary>
    /// KLIM Standard: Generate routing keys following klim.events.{entity}.{operation}.{version} pattern
    /// </summary>
    private string GetKlimStandardRoutingKey(Type messageType, object messageObj)
    {
        return messageType switch
        {
            var t when t == typeof(DataChangedV1) && messageObj is DataChangedV1 dataChange =>
                $"klim.events.{dataChange.EntityType.ToLowerInvariant()}.{dataChange.Operation.ToLowerInvariant()}.v1",

            var t when t == typeof(DomainChangeNotification) =>
                "klim.events.domain.notification.v1",

            var t when t == typeof(DataChangeProcessed) =>
                "klim.events.processing.completed.v1",

            _ => $"klim.events.{messageType.Name.ToLowerInvariant()}.v1"
        };
    }

    /// <summary>
    /// Get message version for headers
    /// </summary>
    private static string GetMessageVersion(Type messageType)
    {
        return messageType.Name.EndsWith("V1") ? "v1" :
               messageType.Name.EndsWith("V2") ? "v2" :
               "v1"; // Default to v1
    }

    private Dictionary<string, object>? ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrEmpty(headersJson)) return null;

        try
        {
            var stringDict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson, _jsonOptions);
            return stringDict?.ToDictionary(k => k.Key, v => (object)v.Value);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse message headers");
            return null;
        }
    }

    private static ChangeDetails ExtractChangeDetails(object messageObj)
    {
        return messageObj switch
        {
            DataChangedV1 change => new ChangeDetails(
                change.Operation,
                string.Join(", ", change.ChangedFields),
                FormatImage(change.PreImage, "Pre"),
                FormatImage(change.PostImage, "Post"),
                change.EntityId.ToString(),
                change.EntityType,
                change.DisplayName
            ),
            _ => ChangeDetails.Unknown
        };
    }

    private static string FormatImage(Dictionary<string, object?>? image, string prefix)
    {
        if (image?.Any() != true) return string.Empty;

        // Show first 8 most important fields, with better formatting
        var details = image.Take(8).Select(kvp =>
        {
            var value = kvp.Value?.ToString() ?? "null";
            // Truncate long values but keep them readable
            var displayValue = value.Length > 50 ? value[..47] + "..." : value;
            return $"{kvp.Key}: {displayValue}";
        });

        var fieldCount = image.Count;
        var summary = $"{prefix}[{string.Join(", ", details)}]";

        // Show how many more fields are available
        if (fieldCount > 8)
        {
            summary += $" +{fieldCount - 8} more fields";
        }

        return summary;
    }

    // Payload size monitoring
    private void MonitorPayloadSize(Guid messageId, int payloadSizeBytes)
    {
        if (payloadSizeBytes > LARGE_PAYLOAD_THRESHOLD)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["MessageId"] = messageId,
                ["PayloadSizeBytes"] = payloadSizeBytes,
                ["PayloadSizeKB"] = payloadSizeBytes / 1024,
                ["PayloadSizeMB"] = payloadSizeBytes / (1024.0 * 1024.0)
            });

            _logger.LogWarning("Large payload detected: {PayloadSize}KB for message {MessageId} (threshold: {Threshold}KB)",
                payloadSizeBytes / 1024, messageId, LARGE_PAYLOAD_THRESHOLD / 1024);
        }
        else
        {
            _logger.LogDebug("Payload size: {PayloadSize}KB for message {MessageId}",
                payloadSizeBytes / 1024, messageId);
        }
    }

    // Utility method for projectors to sanitize large text fields
    public static Dictionary<string, object?> SanitizePayloadData(Dictionary<string, object?> data)
    {
        var sanitized = new Dictionary<string, object?>(data.Count);

        foreach (var kvp in data)
        {
            var value = kvp.Value;
            if (value is string str && str.Length > MAX_TEXT_FIELD_LENGTH)
            {
                // Truncate very large text fields
                sanitized[kvp.Key] = str[..(MAX_TEXT_FIELD_LENGTH - 100)] + $"... [TRUNCATED - original length: {str.Length} chars]";
                sanitized[$"{kvp.Key}_Truncated"] = true;
                sanitized[$"{kvp.Key}_OriginalLength"] = str.Length;
            }
            else
            {
                sanitized[kvp.Key] = value;
            }
        }

        return sanitized;
    }

    /// <summary>
    /// Resolves message type from either simple name or fully qualified name
    /// </summary>
    private static Type? ResolveMessageType(string typeName)
    {
        // Handle simple names first (DataChangedV1, DomainChangeNotification, etc.)
        switch (typeName)
        {
            case "DataChangedV1":
                return typeof(DataChangedV1);
            case "DomainChangeNotification":
                return typeof(KLIM.Events.Service.Infrastructure.ChangeTracking.DomainChangeNotification);
            case "DataChangeProcessed":
                return typeof(DataChangeProcessed);
            default:
                // Fallback to Type.GetType for fully qualified names or other types
                return Type.GetType(typeName);
        }
    }

    private static string GetShortTypeName(Type type)
    {
        // Return simple type name without namespace
        return type.Name;
    }
}

public sealed record PublishResult(bool IsSuccess, string? Error, string Exchange, string RoutingKey, ChangeDetails ChangeDetails)
{
    public static PublishResult Success(string exchange, string routingKey, ChangeDetails changeDetails) =>
        new(true, null, exchange, routingKey, changeDetails);

    public static PublishResult Failed(string error) =>
        new(false, error, string.Empty, string.Empty, ChangeDetails.Unknown);
}

public sealed record ChangeDetails(
    string Operation,
    string ChangedColumns,
    string PreImageInfo,
    string PostImageInfo,
    string EntityId,
    string EntityType,
    string DisplayName
)
{
    public static readonly ChangeDetails Unknown = new("Unknown", "Unknown", "", "", "Unknown", "Unknown", "Unknown");
}