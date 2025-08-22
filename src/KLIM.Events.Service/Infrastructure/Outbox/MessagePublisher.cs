using KLIM.Events.Messaging.Contracts;
using MassTransit;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Handles message publishing to RabbitMQ via MassTransit with payload monitoring and optimization
/// </summary>
public sealed class MessagePublisher
{
    private readonly IBus _bus;
    private readonly ILogger<MessagePublisher> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Payload size monitoring and safety limits
    private const int MAX_PAYLOAD_SIZE_BYTES = 2 * 1024 * 1024; // 2MB reasonable limit
    private const int LARGE_PAYLOAD_THRESHOLD = 500 * 1024; // 500KB warning threshold
    private const int MAX_TEXT_FIELD_LENGTH = 10 * 1024; // 10KB per text field

    public MessagePublisher(IBus bus, ILogger<MessagePublisher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var messageType = Type.GetType(message.Type);
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
        var exchange = GetExchange(messageType);
        var routingKey = GetRoutingKey(messageType);

        _logger.LogDebug("Publishing message {Id} to exchange {Exchange} with routing key {RoutingKey} (size: {PayloadSize}KB)", 
            message.Id, exchange, routingKey, payloadSizeBytes / 1024);

        // Use the correct routing - publish to the topic exchange with routing key
        await _bus.Publish(messageObj, ctx =>
        {
            if (headers != null)
            {
                foreach (var header in headers)
                    ctx.Headers.Set(header.Key, header.Value);
            }
            ctx.MessageId = message.Id;
            ctx.Headers.Set("OccurredAt", message.OccurredAt.ToString("O"));
            ctx.Headers.Set("PayloadSizeKB", (payloadSizeBytes / 1024).ToString()); // Add size info
            
        }, cancellationToken);

        _logger.LogInformation("Successfully published message {Id} to {Exchange}/{RoutingKey} (size: {PayloadSize}KB)", 
            message.Id, exchange, routingKey, payloadSizeBytes / 1024);

        var changeDetails = ExtractChangeDetails(messageObj);
        return PublishResult.Success(exchange, routingKey, changeDetails);
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

    private static string GetExchange(Type messageType) =>
        messageType == typeof(DataChangedV1) ? "klim.change.events" : "klim.change.events";

    private static string GetRoutingKey(Type messageType) => messageType == typeof(DataChangedV1)
        ? "data.changed.v1"  // Keep the original routing key with dot
        : messageType.Name.ToLowerInvariant();

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