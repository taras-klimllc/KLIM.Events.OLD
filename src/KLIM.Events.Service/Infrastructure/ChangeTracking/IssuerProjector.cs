using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Enhanced projector for Issuer entities - captures ALL business columns for comprehensive change events
/// </summary>
public sealed class IssuerProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DataChangedV1).AssemblyQualifiedName!;
    private readonly ILogger<IssuerProjector>? _logger;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public IssuerProjector(ILogger<IssuerProjector>? logger = null)
    {
        _logger = logger;
    }

    // Comprehensive SQL - all business columns from schema
    private const string SQL_ISSUER_CURRENT = @"
        SELECT 
            RowGUID, IssuerID, IssuerName, IssuerDesc, IssuerTicker, 
            FigiID, BBGID, IssuerReportingName, BorrowerName, CountryId, 
            VerticalID, MoodysIndustryId, IssuerESGCode, Performing, PublicIssuer, 
            DealLeadID, ParentIssuerID, HSIssuerID, VPMIssuerID, SSIssuerID, 
            WSOIssuerID, PBIIssuerID, ReorgIssuerID, IsBDC, StatusCode, 
            CreatedBy, Created, LastUpdatedBy, LastUpdated, 
            FinalReview, YodaIssuerID, FindoxIssuerID, Restricted, 
            RestrictionStartDate, RestrictionEndDate, LastRestricted, RestrictedBy, 
            RestrictionRemovedBy, RestrictionRemovedOn, RestrictedFlag, Owner, 
            RestrictionReason, SNPIndustryID, GICSIndustryCodeID, GICSSubIndustryId
        FROM dbo.Issuers 
        WHERE IssuerID = @id";

    // Historical query for temporal tables - same columns as current
    private const string SQL_ISSUER_HISTORY_WINDOW = @"
        SELECT TOP (2) 
            RowGUID, IssuerID, IssuerName, IssuerDesc, IssuerTicker, 
            FigiID, BBGID, IssuerReportingName, BorrowerName, CountryId, 
            VerticalID, MoodysIndustryId, IssuerESGCode, Performing, PublicIssuer, 
            DealLeadID, ParentIssuerID, HSIssuerID, VPMIssuerID, SSIssuerID, 
            WSOIssuerID, PBIIssuerID, ReorgIssuerID, IsBDC, StatusCode, 
            CreatedBy, Created, LastUpdatedBy, LastUpdated, 
            FinalReview, YodaIssuerID, FindoxIssuerID, Restricted, 
            RestrictionStartDate, RestrictionEndDate, LastRestricted, RestrictedBy, 
            RestrictionRemovedBy, RestrictionRemovedOn, RestrictedFlag, Owner, 
            RestrictionReason, SNPIndustryID, GICSIndustryCodeID, GICSSubIndustryId,
            ValidFrom
        FROM dbo.Issuers FOR SYSTEM_TIME ALL
        WHERE IssuerID = @id
        ORDER BY ValidFrom DESC"; // newest then prior

    // Audit fields to exclude from business change filtering  
    private static readonly HashSet<string> AuditFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ledger_start_transaction_id", "ledger_end_transaction_id",
        "ledger_start_sequence_number", "ledger_end_sequence_number", 
        "ValidFrom", "ValidTo"
    };

    public bool Supports(string schema, string table) => (schema, table) is ("dbo", "Issuers");

    public async Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct)
    {
        var inserts = new List<OutboxInsert>(batch.Changes.Count);
        
        foreach (var change in batch.Changes)
        {
            var op = change.Operation;
            
            // Filter audit-only changes 
            if (op == "U")
            {
                var businessChangedFields = change.ChangedColumns
                    .Where(field => !AuditFields.Contains(field))
                    .ToArray();
                
                // Log detected changes for monitoring
                _logger?.LogDebug("Change tracking detected for Issuer {EntityId} (version {ChangeVersion}): Business[{BusinessFields}] | Audit[{AuditFields}]", 
                    change.Id, change.Version, 
                    string.Join(", ", businessChangedFields),
                    string.Join(", ", change.ChangedColumns.Except(businessChangedFields)));
                
                // Skip audit-only updates
                if (businessChangedFields.Length == 0)
                {
                    _logger?.LogDebug("Skipping audit-only update for Issuer {EntityId} (version {ChangeVersion}): [{AuditFields}]", 
                        change.Id, change.Version, string.Join(", ", change.ChangedColumns));
                    continue;
                }
                
                if (businessChangedFields.Length < change.ChangedColumns.Length)
                {
                    var auditOnlyFields = change.ChangedColumns.Except(businessChangedFields).ToArray();
                    _logger?.LogDebug("Processing mixed update for Issuer {EntityId}: Business[{BusinessFields}] + Audit[{AuditFields}]", 
                        change.Id, string.Join(", ", businessChangedFields), string.Join(", ", auditOnlyFields));
                }
            }

            Guid rowGuid = Guid.Empty;
            string displayName = string.Empty;
            Dictionary<string, object?>? preImage = null;
            Dictionary<string, object?>? postImage = null;

            if (op == "D")
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    rowGuid = hist[0].RowGuid;
                    displayName = GetDisplayName(hist[0].Data);
                    preImage = hist[0].Data; // Full business data before deletion
                }
                else
                {
                    displayName = $"DELETED-ISSUER-{change.Id}";
                }
            }
            else if (op == "I")
            {
                var current = await LoadCurrentAsync(connection, tx, change.Id, ct);
                if (current.HasValue)
                {
                    rowGuid = current.Value.RowGuid;
                    displayName = GetDisplayName(current.Value.Data);
                    postImage = current.Value.Data; // Full business data after insert
                }
                else
                {
                    displayName = $"ISSUER-{change.Id}";
                }
            }
            else if (op == "U")
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    rowGuid = hist[0].RowGuid;
                    displayName = GetDisplayName(hist[0].Data);
                    postImage = hist[0].Data; // Full business data after update
                }
                if (hist.Count > 1)
                {
                    preImage = hist[1].Data; // Full business data before update
                }
            }

            // Fallback handling
            if (rowGuid == Guid.Empty) rowGuid = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"ISSUER-{change.Id}";

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

    // Extract all columns to dictionary dynamically with safety checks
    private async Task<(Guid RowGuid, Dictionary<string, object?> Data)?> LoadCurrentAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(SQL_ISSUER_CURRENT, conn, tx);
            cmd.Parameters.AddWithValue("@id", pk);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                var guid = rdr.GetGuid(0); // RowGUID is first column
                var data = new Dictionary<string, object?>();
                
                // Extract all columns dynamically
                for (int i = 0; i < rdr.FieldCount; i++)
                {
                    var fieldName = rdr.GetName(i);
                    data[fieldName] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                }
                
                // Apply payload sanitization for large text fields
                var sanitizedData = MessagePublisher.SanitizePayloadData(data);
                return (guid, sanitizedData);
            }
        }
        catch (Exception ex)
        {
            // Enhanced error handling - log but don't fail the batch
            using var scope = _logger?.BeginScope(new Dictionary<string, object> { ["PrimaryKey"] = pk.ToString() ?? "null", ["Operation"] = "LoadCurrent" });
            _logger?.LogWarning(ex, "Failed to load current data for Issuer {PrimaryKey}, using fallback", pk);
        }
        
        return null;
    }

    private async Task<List<HistRow>> LoadHistoryWindowAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        var list = new List<HistRow>();
        
        try
        {
            await using var cmd = new SqlCommand(SQL_ISSUER_HISTORY_WINDOW, conn, tx);
            cmd.Parameters.AddWithValue("@id", pk);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var guid = rdr.GetGuid(0); // RowGUID is first column
                var data = new Dictionary<string, object?>();
                
                for (int i = 0; i < rdr.FieldCount; i++)
                {
                    var fieldName = rdr.GetName(i);
                    if (fieldName != "ValidFrom") // Exclude temporal metadata from payload
                    {
                        data[fieldName] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    }
                }
                
                // Apply payload sanitization for large text fields
                var sanitizedData = MessagePublisher.SanitizePayloadData(data);
                list.Add(new HistRow(guid, sanitizedData));
            }
        }
        catch (Exception ex)
        {
            // Enhanced error handling - log warning but return what we have
            using var scope = _logger?.BeginScope(new Dictionary<string, object> { ["PrimaryKey"] = pk.ToString() ?? "null", ["Operation"] = "LoadHistory" });
            _logger?.LogWarning(ex, "Failed to load complete history for Issuer {PrimaryKey}, using partial data (count: {Count})", pk, list.Count);
        }
        
        return list;
    }

    // Create meaningful display names with ticker and identifiers
    private static string GetDisplayName(Dictionary<string, object?> data)
    {
        var name = data.GetValueOrDefault("IssuerName")?.ToString();
        var ticker = data.GetValueOrDefault("IssuerTicker")?.ToString();
        var reportingName = data.GetValueOrDefault("IssuerReportingName")?.ToString();
        var bbgId = data.GetValueOrDefault("BBGID")?.ToString();
        
        return ticker switch
        {
            not null when name != null => $"{ticker} ({name})",
            not null => $"{ticker} ({reportingName ?? "No Name"})",
            null when bbgId != null => $"{bbgId} ({name ?? reportingName ?? "No Name"})", 
            _ => name ?? reportingName ?? "Unknown Issuer"
        };
    }

    private sealed record HistRow(Guid RowGuid, Dictionary<string, object?> Data);
}