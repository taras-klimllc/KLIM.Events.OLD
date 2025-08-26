namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Manages adaptive polling intervals based on message availability
/// </summary>
public sealed class PollingStrategy
{
    private const int MIN_INTERVAL_SECONDS = 1;
    private const int MAX_INTERVAL_SECONDS = 60;
    private const double BACKOFF_MULTIPLIER = 1.5;
    private const double SPEEDUP_FACTOR = 0.5;

    private int _emptyPolls;

    public TimeSpan AdjustInterval(TimeSpan currentInterval, int messageCount)
    {
        if (messageCount > 0)
        {
            _emptyPolls = 0;
            var newSeconds = Math.Max(currentInterval.TotalSeconds * SPEEDUP_FACTOR, MIN_INTERVAL_SECONDS);
            return TimeSpan.FromSeconds(newSeconds);
        }

        _emptyPolls++;
        if (_emptyPolls <= 3) return currentInterval;

        var backoffSeconds = Math.Min(currentInterval.TotalSeconds * BACKOFF_MULTIPLIER, MAX_INTERVAL_SECONDS);
        return TimeSpan.FromSeconds(backoffSeconds);
    }
}

/// <summary>
/// Handles cleanup timing logic
/// </summary>
public sealed class CleanupScheduler
{
    private DateTime _lastCleanup = DateTime.MinValue;

    public bool ShouldCleanup(double intervalHours) =>
        (DateTime.UtcNow - _lastCleanup).TotalHours >= intervalHours;

    public void MarkCleaned() => _lastCleanup = DateTime.UtcNow;
}

/// <summary>
/// SQL exception handling utilities
/// </summary>
public static class SqlExceptionHelper
{
    private static readonly int[] TransientErrorNumbers =
    {
        -2, 4060, 40197, 40501, 40613, 49918, 49919, 49920, 10928, 10929, 1205, 233, 18456
    };

    public static bool IsTransient(Microsoft.Data.SqlClient.SqlException ex) =>
        TransientErrorNumbers.Contains(ex.Number);
}