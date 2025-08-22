using KLIM.Events.Messaging.Contracts;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Projector for Deal entities - converts raw change tracking data into DataChangedV1 events
/// </summary>
public sealed class DealProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DataChangedV1).AssemblyQualifiedName!;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const string SQL_DEAL_CURRENT = "SELECT RowGUID, DealName, DealDesc FROM dbo.Deals WHERE DealID = @id";
    private const string SQL_DEAL_HISTORY_WINDOW = @"SELECT TOP (2) RowGUID, DealName, DealDesc, ValidFrom
FROM dbo.Deals FOR SYSTEM_TIME ALL
WHERE DealID = @id
ORDER BY ValidFrom DESC"; // newest then prior

    public bool Supports(string schema, string table)
        => (schema, table) is ("dbo", "Deals");

    public async Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct)
    {
        var inserts = new List<OutboxInsert>(batch.Changes.Count);
        foreach (var change in batch.Changes)
        {
            var op = change.Operation;
            Guid rowGuid = Guid.Empty;
            string displayName = string.Empty;
            Dictionary<string, object?>? preImage = null;
            Dictionary<string, object?>? postImage = null;

            if (op == "D")
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    var last = hist[0];
                    rowGuid = last.RowGuid;
                    displayName = last.DealName ?? last.DealDesc ?? $"DELETED-DEAL-{change.Id}";
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["DealName"] = last.DealName,
                        ["DealDesc"] = last.DealDesc
                    };
                }
                else
                {
                    displayName = $"DELETED-DEAL-{change.Id}";
                }
            }
            else if (op == "I")
            {
                (rowGuid, var nullableDisplayName) = await LoadCurrentAsync(connection, tx, change.Id, ct);
                displayName = nullableDisplayName ?? $"DEAL-{change.Id}";
                postImage = new Dictionary<string, object?>
                {
                    ["RowGUID"] = rowGuid,
                    ["DisplayName"] = displayName
                };
            }
            else if (op == "U")
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    var last = hist[0];
                    rowGuid = last.RowGuid;
                    displayName = last.DealName ?? last.DealDesc ?? $"DEAL-{change.Id}";
                    postImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["DealName"] = last.DealName,
                        ["DealDesc"] = last.DealDesc
                    };
                }
                if (hist.Count > 1)
                {
                    var prev = hist[1];
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = prev.RowGuid,
                        ["DealName"] = prev.DealName,
                        ["DealDesc"] = prev.DealDesc
                    };
                }
            }

            if (rowGuid == Guid.Empty)
                rowGuid = Guid.NewGuid(); // fallback
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = $"DEAL-{change.Id}";

            var changedFields = op switch
            {
                "I" => new[] { "*" },
                "U" => change.ChangedColumns,
                "D" => new[] { "__Deleted" },
                _ => Array.Empty<string>()
            };

            var now = DateTimeOffset.UtcNow;
            var evt = new DataChangedV1(
                EntityId: rowGuid,
                EntityType: "Deal",
                DisplayName: displayName,
                ChangeVersion: change.Version,
                ChangedAt: now,
                ChangeSource: $"{batch.Schema}.{batch.Table}",
                Operation: op,
                ChangedFields: changedFields,
                PreImage: preImage,
                PostImage: postImage,
                CorrelationId: null
            );

            var payload = JsonSerializer.Serialize(evt, _json);
            inserts.Add(new OutboxInsert(
                Id: Guid.NewGuid(),
                Type: EventType,
                Payload: payload,
                OccurredAt: now.UtcDateTime,
                MessageKey: $"DataChangedV1|{evt.EntityType}|{evt.EntityId}|{batch.Schema}.{batch.Table}|{change.Version}|{op}",
                SourceEntity: $"{batch.Schema}.{batch.Table}",
                SourceId: evt.EntityId.ToString(),
                ChangeVersion: change.Version));
        }
        return inserts;
    }

    private static async Task<(Guid RowGuid, string? DisplayName)> LoadCurrentAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(SQL_DEAL_CURRENT, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (await rdr.ReadAsync(ct))
        {
            var guid = rdr.GetGuid(0);
            var dealName = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            var dealDesc = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            return (guid, dealName ?? dealDesc);
        }
        return (Guid.Empty, null);
    }

    private static async Task<List<HistRow>> LoadHistoryWindowAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(SQL_DEAL_HISTORY_WINDOW, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HistRow>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new HistRow(
                RowGuid: rdr.GetGuid(0),
                DealName: rdr.IsDBNull(1) ? null : rdr.GetString(1),
                DealDesc: rdr.IsDBNull(2) ? null : rdr.GetString(2)
            ));
        }
        return list;
    }

    private sealed record HistRow(Guid RowGuid, string? DealName, string? DealDesc);
}