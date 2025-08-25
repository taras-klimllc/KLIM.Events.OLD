using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Generic projector that emits a lightweight DomainChangeNotification for any table *not* handled by specialized projectors.
/// Acts as a fallback for future tables that don't have dedicated projectors yet.
/// </summary>
public sealed class GenericDomainChangeProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DomainChangeNotification).Name; // Use simple name instead of AssemblyQualifiedName
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool Supports(string schema, string table) => true; // fallback, will run after specialized projectors

    public Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct)
    {
        // Skip tables that have specialized projectors to avoid duplicates
        if ((batch.Schema, batch.Table) is ("dbo", "Issuers") or ("dbo", "Deals"))
            return Task.FromResult<IEnumerable<OutboxInsert>>(Array.Empty<OutboxInsert>());

        var list = new List<OutboxInsert>(batch.Changes.Count);
        foreach (var change in batch.Changes)
        {
            var detectedAt = DateTime.UtcNow;
            var dc = new DomainChangeNotification(
                Table: $"{batch.Schema}.{batch.Table}",
                EntityId: change.Id.ToString()!,
                Operation: change.Operation,
                ChangeVersion: change.Version,
                DetectedAtUtc: detectedAt
            );
            var payload = JsonSerializer.Serialize(dc, _json);
            var key = $"DomainChange|{dc.Table}|{dc.EntityId}|{dc.ChangeVersion}";
            list.Add(new OutboxInsert(
                Id: Guid.NewGuid(),
                Type: EventType,
                Payload: payload,
                OccurredAt: detectedAt,
                MessageKey: key,
                SourceEntity: dc.Table,
                SourceId: dc.EntityId,
                ChangeVersion: dc.ChangeVersion));
        }
        return Task.FromResult<IEnumerable<OutboxInsert>>(list);
    }
}

/// <summary>
/// Lightweight notification for changes in tables without specialized projectors
/// </summary>
public sealed record DomainChangeNotification(
    string Table,
    string EntityId,
    string Operation,
    long ChangeVersion,
    DateTime DetectedAtUtc);
