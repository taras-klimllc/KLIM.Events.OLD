using Microsoft.Data.SqlClient;
using KLIM.Events.Service.Infrastructure.Outbox;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Interface for projecting database change events into domain messages
/// </summary>
public interface IChangeEventProjector
{
    /// <summary>
    /// Determines if this projector supports the given table
    /// </summary>
    bool Supports(string schema, string table);
    
    /// <summary>
    /// Projects database changes into outbox messages
    /// </summary>
    Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct);
}
