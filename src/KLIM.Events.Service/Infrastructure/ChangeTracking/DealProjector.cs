using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Enhanced projector for Deal entities - captures ALL business columns for comprehensive change events
/// </summary>
public sealed class DealProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DataChangedV1).Name; // Use simple name instead of AssemblyQualifiedName
    private readonly ILogger<DealProjector>? _logger;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DealProjector(ILogger<DealProjector>? logger = null)
    {
        _logger = logger;
    }

    // Comprehensive SQL - all business columns from Deals schema
    private const string SQL_DEAL_CURRENT = @"
        SELECT 
            RowGUID, DealID, DealName, DealDesc, TypeID, StrategyID, 
            VerticalID, RiskTypeID, CapitalTypeID, DealLeadID, InternalDealTag, 
            ExternalDealTag, MultiIssuerDeal, TRHaircut, ShortName, SourceID, 
            KLInvestmentRoleID, VintageYear, StartDate, EndDate, 
            CreatedBy, Created, LastUpdatedBy, LastUpdated, 
            NonSponsored, ParentDealID, MarketType, KLDealSize, TotalDealSize, 
            TargetIRR, TargetMOIC, InvestmentThesis, Stage, BoardSeats, 
            PrimarySeniority, PrimaryRate, ReportClassification, UseofProceed, 
            FinalReview, TargetFund, KlCashInvestmentAmount, ScheduledIcDate, 
            FundingStatus, ExpectedFundingDate, LenderArrangement, SourcingChannel, 
            SourcingCounterparty, Restructured, RiskLookupTypeID, CapitalLookupTypeID, 
            StatusID, StatusLookupTypeId, SourceLookupTypeId
        FROM dbo.Deals 
        WHERE DealID = @id";

    // Historical query for temporal tables - same columns as current
    private const string SQL_DEAL_HISTORY_WINDOW = @"
        SELECT TOP (2) 
            RowGUID, DealID, DealName, DealDesc, TypeID, StrategyID, 
            VerticalID, RiskTypeID, CapitalTypeID, DealLeadID, InternalDealTag, 
            ExternalDealTag, MultiIssuerDeal, TRHaircut, ShortName, SourceID, 
            KLInvestmentRoleID, VintageYear, StartDate, EndDate, 
            CreatedBy, Created, LastUpdatedBy, LastUpdated, 
            NonSponsored, ParentDealID, MarketType, KLDealSize, TotalDealSize, 
            TargetIRR, TargetMOIC, InvestmentThesis, Stage, BoardSeats, 
            PrimarySeniority, PrimaryRate, ReportClassification, UseofProceed, 
            FinalReview, TargetFund, KlCashInvestmentAmount, ScheduledIcDate, 
            FundingStatus, ExpectedFundingDate, LenderArrangement, SourcingChannel, 
            SourcingCounterparty, Restructured, RiskLookupTypeID, CapitalLookupTypeID, 
            StatusID, StatusLookupTypeId, SourceLookupTypeId,
            ValidFrom
        FROM dbo.Deals FOR SYSTEM_TIME ALL
        WHERE DealID = @id
        ORDER BY ValidFrom DESC"; // newest then prior

    // Audit fields to exclude from business change filtering
    private static readonly HashSet<string> AuditFields = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ledger fields (SQL Server 2022+ Ledger feature)
        "ledger_start_transaction_id", "ledger_end_transaction_id",
        "ledger_start_sequence_number", "ledger_end_sequence_number",
        "ledger_view_id", "ledger_transaction_id", "ledger_sequence_number",
        
        // Temporal table fields  
        "ValidFrom", "ValidTo",
        
        // Standard audit fields
        "CreatedBy", "Created", "LastUpdatedBy", "LastUpdated"
    };

    // Audit field prefixes for dynamic columns (e.g., MSSQL_DroppedLedgerColumn_*)
    private static readonly string[] AuditFieldPrefixes =
    {
        "MSSQL_DroppedLedgerColumn_"
    };

    /// <summary>
    /// Determines if a field should be considered an audit field and excluded from business change tracking
    /// </summary>
    private static bool IsAuditField(string fieldName)
    {
        // Check exact matches first (faster)
        if (AuditFields.Contains(fieldName))
            return true;

        // Check prefix matches for dynamic columns
        return AuditFieldPrefixes.Any(prefix => fieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool Supports(string schema, string table) => (schema, table) is ("dbo", "Deals");

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
                    .Where(field => !IsAuditField(field))
                    .ToArray();

                if (businessChangedFields.Length == 0)
                {
                    // Log skipped audit-only changes for visibility
                    _logger?.LogDebug("Skipping audit-only update for Deal {EntityId} (version {ChangeVersion}): [{AuditFields}]",
                        change.Id, change.Version, string.Join(", ", change.ChangedColumns));
                    continue;
                }
                else if (businessChangedFields.Length < change.ChangedColumns.Length)
                {
                    // Mixed business + audit changes - log for awareness
                    var auditOnlyFields = change.ChangedColumns.Except(businessChangedFields).ToArray();
                    _logger?.LogDebug("Processing mixed update for Deal {EntityId}: Business[{BusinessFields}] + Audit[{AuditFields}]",
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
                    displayName = $"DELETED-DEAL-{change.Id}";
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
                    displayName = $"DEAL-{change.Id}";
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
            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"DEAL-{change.Id}";

            var changedFields = op switch
            {
                "I" => new[] { "*" },
                "U" => change.ChangedColumns.Where(field => !IsAuditField(field)).ToArray(), // Filter audit fields from changedFields JSON
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

    // Extract all columns to dictionary dynamically with safety checks
    private async Task<(Guid RowGuid, Dictionary<string, object?> Data)?> LoadCurrentAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(SQL_DEAL_CURRENT, conn, tx);
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
            _logger?.LogWarning(ex, "Failed to load current data for Deal {PrimaryKey}, using fallback", pk);
        }

        return null;
    }

    private async Task<List<HistRow>> LoadHistoryWindowAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        var list = new List<HistRow>();

        try
        {
            await using var cmd = new SqlCommand(SQL_DEAL_HISTORY_WINDOW, conn, tx);
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
            _logger?.LogWarning(ex, "Failed to load complete history for Deal {PrimaryKey}, using partial data (count: {Count})", pk, list.Count);
        }

        return list;
    }

    // Create meaningful display names with deal size and strategy info
    private static string GetDisplayName(Dictionary<string, object?> data)
    {
        var name = data.GetValueOrDefault("DealName")?.ToString();
        var shortName = data.GetValueOrDefault("ShortName")?.ToString();
        var dealSize = data.GetValueOrDefault("KLDealSize");
        var stage = data.GetValueOrDefault("Stage")?.ToString();

        var displayName = shortName ?? name ?? "Unknown Deal";

        // Add size and stage info if available
        var details = new List<string>();
        if (dealSize != null && decimal.TryParse(dealSize.ToString(), out var size) && size > 0)
        {
            details.Add($"${size:N0}M");
        }
        if (!string.IsNullOrEmpty(stage))
        {
            details.Add(stage);
        }

        if (details.Count > 0)
        {
            displayName += $" ({string.Join(", ", details)})";
        }

        return displayName;
    }

    private sealed record HistRow(Guid RowGuid, Dictionary<string, object?> Data);
}