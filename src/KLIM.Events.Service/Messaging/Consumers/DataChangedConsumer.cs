using KLIM.Events.Messaging.Contracts;
using MassTransit;
using System.Diagnostics;

namespace KLIM.Events.Messaging.Consumers;

/// <summary>
/// Consumes DataChangedV1 events, validates basic invariants,
/// and republishes a simplified downstream message for further processing.
/// Supports multiple entity types (Issuers, Deals, etc.)
/// </summary>
public sealed class DataChangedConsumer : IConsumer<DataChangedV1>
{
    private readonly ILogger<DataChangedConsumer> _log;

    public DataChangedConsumer(ILogger<DataChangedConsumer> log) => _log = log;

    public async Task Consume(ConsumeContext<DataChangedV1> ctx)
    {
        var msg = ctx.Message;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ValidateMessage(msg);

            var processedMessage = new DataChangeProcessed(
                EntityId: msg.EntityId,
                EntityType: msg.EntityType,
                DisplayName: msg.DisplayName,
                ChangeVersion: msg.ChangeVersion,
                ChangedAt: msg.ChangedAt,
                Operation: msg.Operation,
                ChangeSource: msg.ChangeSource,
                Fields: msg.ChangedFields,
                ProcessedAt: DateTimeOffset.UtcNow,
                DeliveryCount: ctx.GetRetryCount() + 1
            );

            await ctx.Publish(processedMessage, ctx.CancellationToken);

            stopwatch.Stop();
            _log.LogInformation(
                "Handled DataChangedV1 {EntityType} {EntityId} Op={Op} Display={DisplayName} in {ElapsedMs}ms",
                msg.EntityType,
                msg.EntityId,
                msg.Operation,
                msg.DisplayName,
                stopwatch.ElapsedMilliseconds);
        }
        catch (ArgumentException ex)
        {
            _log.LogWarning("Validation failed for {EntityType} {EntityId}: {Error}", msg.EntityType, msg.EntityId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing {EntityType} {EntityId} Op={Op} Display={DisplayName}", 
                msg.EntityType, msg.EntityId, msg.Operation, msg.DisplayName);
            throw;
        }
    }

    private static void ValidateMessage(DataChangedV1 msg)
    {
        if (string.IsNullOrWhiteSpace(msg.DisplayName))
            throw new ArgumentException("DisplayName is required");
        if (string.IsNullOrWhiteSpace(msg.EntityType))
            throw new ArgumentException("EntityType is required");
        if (msg.ChangedFields is null)
            throw new ArgumentException("ChangedFields cannot be null");
        if (Math.Abs(msg.ChangedAt.Offset.TotalMinutes) > 1)
            throw new ArgumentException($"ChangedAt must be UTC or near-UTC: {msg.ChangedAt}");
    }
}