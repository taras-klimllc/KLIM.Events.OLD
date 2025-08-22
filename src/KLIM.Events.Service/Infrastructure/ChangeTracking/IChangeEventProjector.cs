using Microsoft.Data.SqlClient;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

public interface IChangeEventProjector
{
    bool Supports(string schema, string table);
    Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct);
}

public interface IOutboxWriter
{
    Task InsertManyAsync(SqlConnection conn, SqlTransaction tx, IReadOnlyCollection<OutboxInsert> inserts, CancellationToken ct);
}

public sealed record OutboxInsert(
    Guid Id,
    string Type,
    string Payload,
    DateTime OccurredAt,
    string MessageKey,
    string SourceEntity,
    string SourceId,
    long ChangeVersion);
