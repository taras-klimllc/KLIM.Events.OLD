using KLIM.Events.Messaging.Contracts;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Projector for Issuer entities - converts raw change tracking data into DataChangedV1 events
/// </summary>
public sealed class IssuerProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DataChangedV1).AssemblyQualifiedName!;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const string SQL_ISSUER_CURRENT = "SELECT RowGUID, IssuerName, IssuerReportingName FROM dbo.Issuers WHERE IssuerID = @id";
    private const string SQL_ISSUER_HISTORY_WINDOW = @"SELECT TOP (2) RowGUID, IssuerName, IssuerReportingName, ValidFrom
FROM dbo.Issuers FOR SYSTEM_TIME ALL
WHERE IssuerID = @id
ORDER BY ValidFrom DESC"; // newest then prior

    public bool Supports(string schema, string table)
        => (schema, table) is ("dbo", "Issuers");

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
                    displayName = last.Name ?? last.ReportingName ?? $"DELETED-ISSUER-{change.Id}";
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["IssuerName"] = last.Name,
                        ["IssuerReportingName"] = last.ReportingName
                    };
                }
                else
                {
                    displayName = $"DELETED-ISSUER-{change.Id}";
                }
            }
            else if (op == "I")
            {
                (rowGuid, var nullableDisplayName) = await LoadCurrentAsync(connection, tx, change.Id, ct);
                displayName = nullableDisplayName ?? $"ISSUER-{change.Id}";
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
                    displayName = last.Name ?? last.ReportingName ?? $"ISSUER-{change.Id}";
                    postImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["IssuerName"] = last.Name,
                        ["IssuerReportingName"] = last.ReportingName
                    };
                }
                if (hist.Count > 1)
                {
                    var prev = hist[1];
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = prev.RowGuid,
                        ["IssuerName"] = prev.Name,
                        ["IssuerReportingName"] = prev.ReportingName
                    };
                }
            }

            if (rowGuid == Guid.Empty)
                rowGuid = Guid.NewGuid(); // fallback
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = $"ISSUER-{change.Id}";

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
                EntityType: "Issuer",
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
        await using var cmd = new SqlCommand(SQL_ISSUER_CURRENT, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (await rdr.ReadAsync(ct))
        {
            var guid = rdr.GetGuid(0);
            var name = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            var reportingName = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            return (guid, name ?? reportingName);
        }
        return (Guid.Empty, null);
    }

    private static async Task<List<HistRow>> LoadHistoryWindowAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(SQL_ISSUER_HISTORY_WINDOW, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HistRow>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new HistRow(
                RowGuid: rdr.GetGuid(0),
                Name: rdr.IsDBNull(1) ? null : rdr.GetString(1),
                ReportingName: rdr.IsDBNull(2) ? null : rdr.GetString(2)
            ));
        }
        return list;
    }

    private sealed record HistRow(Guid RowGuid, string? Name, string? ReportingName);
}