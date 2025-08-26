namespace KLIM.Events.Messaging.Contracts;

/// <summary>
/// Generic data change event for any tracked entity (Issuers, Deals, etc.)
/// Version: V1 (initial public contract).
/// </summary>
/// <param name="EntityId">Stable unique identifier of the entity (RowGUID where available).</param>
/// <param name="EntityType">Type/category of the entity (e.g., "Issuer", "Deal").</param>
/// <param name="DisplayName">Primary display name or identifier (ticker, name, etc.).</param>
/// <param name="ChangeVersion">Underlying SQL Change Tracking version for ordering/debug.</param>
/// <param name="ChangedAt">UTC timestamp when change was detected.</param>
/// <param name="ChangeSource">Origin table or logical source e.g. dbo.Issuers.</param>
/// <param name="Operation">CRUD semantic: I / U / D.</param>
/// <param name="ChangedFields">List of changed field names (* for inserts, __Deleted for deletes).</param>
/// <param name="PreImage">Partial pre-image (selected fields) for Update/Delete when available.</param>
/// <param name="PostImage">Partial post-image (selected fields) for Insert/Update when available.</param>
/// <param name="CorrelationId">Optional correlation id for distributed tracing.</param>
public sealed record DataChangedV1(
    Guid EntityId,
    string EntityType,
    string DisplayName,
    long ChangeVersion,
    DateTimeOffset ChangedAt,
    string ChangeSource,
    string Operation,
    string[] ChangedFields,
    Dictionary<string, object?>? PreImage,
    Dictionary<string, object?>? PostImage,
    Guid? CorrelationId = null
);