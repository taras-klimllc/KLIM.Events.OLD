using KLIM.Events.Messaging.Contracts;
using MassTransit;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Handles message publishing to RabbitMQ via MassTransit
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

        var headers = ParseHeaders(message.Headers);
        var exchange = GetExchange(messageType);
        var routingKey = GetRoutingKey(messageType);

        await _bus.Publish(messageObj, ctx =>
        {
            if (headers != null)
            {
                foreach (var header in headers)
                    ctx.Headers.Set(header.Key, header.Value);
            }
            ctx.MessageId = message.Id;
            ctx.Headers.Set("OccurredAt", message.OccurredAt.ToString("O"));
        }, cancellationToken);

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

    private static string GetExchange(Type messageType) => messageType == typeof(DataChangedV1) 
        ? "klim.change.events" 
        : "klim.change.events";

    private static string GetRoutingKey(Type messageType) => messageType == typeof(DataChangedV1)
        ? "data.changed.v1"
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

        var details = image.Select(kvp =>
        {
            var value = kvp.Value?.ToString() ?? "null";
            return $"{kvp.Key}: {(value.Length > 100 ? value[..97] + "..." : value)}";
        });

        return $"{prefix}[{string.Join(", ", details)}]";
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