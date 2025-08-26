namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Specialized logging extensions for outbox dispatch operations
/// </summary>
public static class OutboxLoggerExtensions
{
    /// <summary>
    /// Extracts a friendly type name for logging purposes
    /// </summary>
    private static string GetFriendlyTypeName(string fullTypeName)
    {
        // Extract just the class name from either simple name or assembly qualified name
        var typeName = fullTypeName.Split(',')[0]; // Remove assembly info
        var lastDot = typeName.LastIndexOf('.');
        return lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
    }

    /// <summary>
    /// Log successful message dispatch with all relevant details
    /// </summary>
    public static void LogOutboxDispatchSuccess(this ILogger logger,
        Guid messageId,
        string messageType,
        string exchange,
        string routingKey,
        string sourceEntity,
        string sourceId,
        string changeVersion,
        long durationMs,
        string operation = "",
        string changedColumns = "",
        string entityType = "",
        string displayName = "")
    {
        var friendlyTypeName = GetFriendlyTypeName(messageType);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = messageId,
            ["MessageType"] = friendlyTypeName, // Use friendly name in scope
            ["Exchange"] = exchange,
            ["RoutingKey"] = routingKey,
            ["SourceEntity"] = sourceEntity,
            ["SourceId"] = sourceId,
            ["ChangeVersion"] = changeVersion,
            ["Operation"] = operation,
            ["ChangedColumns"] = changedColumns,
            ["EntityType"] = entityType,
            ["DisplayName"] = displayName,
            ["DurationMs"] = durationMs,
            ["Status"] = "SUCCESS"
        }))
        {
            logger.LogInformation("OUTBOX DISPATCH SUCCESS: {MessageId} | Type: {MessageType} | Exchange: {Exchange} | RoutingKey: {RoutingKey} | Source: {SourceEntity}#{SourceId} | ChangeVersion: {ChangeVersion} | Duration: {DurationMs}ms",
                messageId, friendlyTypeName, exchange, routingKey, sourceEntity, sourceId, changeVersion, durationMs);
        }
    }

    /// <summary>
    /// Log message dispatch failure with all relevant details
    /// </summary>
    public static void LogOutboxDispatchError(this ILogger logger,
        Exception exception,
        Guid messageId,
        string messageType,
        string sourceEntity,
        string sourceId)
    {
        var friendlyTypeName = GetFriendlyTypeName(messageType);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = messageId,
            ["MessageType"] = friendlyTypeName, // Use friendly name in scope
            ["SourceEntity"] = sourceEntity,
            ["SourceId"] = sourceId,
            ["Status"] = "ERROR"
        }))
        {
            logger.LogError(exception, "OUTBOX DISPATCH ERROR: {MessageId} | Type: {MessageType} | Source: {SourceEntity}#{SourceId}",
                messageId, friendlyTypeName, sourceEntity, sourceId);
        }
    }

    /// <summary>
    /// Log detailed change information
    /// </summary>
    public static void LogChangeDetails(this ILogger logger,
        Guid messageId,
        string operation,
        string entityId,
        string entityType,
        string displayName,
        string changedColumns,
        string preImageInfo,
        string postImageInfo)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = messageId,
            ["Operation"] = operation,
            ["EntityId"] = entityId,
            ["EntityType"] = entityType,
            ["DisplayName"] = displayName,
            ["ChangedColumns"] = changedColumns,
            ["HasPreImage"] = !string.IsNullOrEmpty(preImageInfo),
            ["HasPostImage"] = !string.IsNullOrEmpty(postImageInfo)
        }))
        {
            logger.LogDebug("?? CHANGE DETAILS: {MessageId} | {Operation} | {EntityType}: {EntityId} ({DisplayName}) | Changed: [{ChangedColumns}] | {PreImageInfo} | {PostImageInfo}",
                messageId, operation, entityType, entityId, displayName, changedColumns, preImageInfo, postImageInfo);
        }
    }

    /// <summary>
    /// Log batch processing statistics
    /// </summary>
    public static void LogBatchProcessed(this ILogger logger,
        int messageCount,
        long totalDurationMs,
        int successCount,
        int errorCount)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["BatchSize"] = messageCount,
            ["SuccessCount"] = successCount,
            ["ErrorCount"] = errorCount,
            ["TotalDurationMs"] = totalDurationMs,
            ["AvgDurationPerMessage"] = messageCount > 0 ? totalDurationMs / messageCount : 0
        }))
        {
            var level = errorCount > 0 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "?? BATCH PROCESSED: {MessageCount} messages | ? {SuccessCount} success | ? {ErrorCount} errors | Total: {TotalDurationMs}ms | Avg: {AvgDuration}ms/msg",
                messageCount, successCount, errorCount, totalDurationMs, messageCount > 0 ? totalDurationMs / messageCount : 0);
        }
    }
}