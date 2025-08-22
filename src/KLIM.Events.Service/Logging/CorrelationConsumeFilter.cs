using MassTransit;
using Serilog.Context;
using System.Collections.Concurrent;
using System.Reflection;

namespace KLIM.Events.Logging;

/// <summary>
/// Generic consume filter that enriches the Serilog <see cref="LogContext"/> with a CorrelationId for all consumed messages.
/// Order: runs early in the consume pipeline so that all subsequent logs include the correlation id.
/// Resolution strategy:
/// 1. MassTransit <see cref="ConsumeContext.CorrelationId"/>
/// 2. Message property named "CorrelationId" (Guid or string)
/// 3. MassTransit ConversationId
/// </summary>
public sealed class CorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>> where T : class
{
    // Cache reflection lookups for better performance with high throughput
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> PropertyCache = new();

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlationConsumeFilter");

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var correlationId = context.CorrelationId?.ToString();

        // Try message CorrelationId property (nullable string or Guid) via reflection if not set
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            var msg = context.Message;
            var msgType = msg.GetType();

            // Use cached property lookup for better performance
            var prop = PropertyCache.GetOrAdd(msgType, type =>
                type.GetProperty("CorrelationId", BindingFlags.Public | BindingFlags.Instance));

            if (prop != null)
            {
                var val = prop.GetValue(msg);
                if (val is Guid g) correlationId = g.ToString();
                else if (val is string s && !string.IsNullOrWhiteSpace(s)) correlationId = s;
            }
        }

        // Fallback to conversation ID if still empty
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = context.ConversationId?.ToString();

        // Additional fallback to InitiatorId if available (for request-response)
        if (string.IsNullOrWhiteSpace(correlationId) && context.InitiatorId.HasValue)
            correlationId = context.InitiatorId.Value.ToString();

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId ?? string.Empty))
        {
            // Also include message metadata for better tracing
            using (Serilog.Context.LogContext.PushProperty("MessageType", typeof(T).Name))
            using (Serilog.Context.LogContext.PushProperty("MessageId", context.MessageId?.ToString() ?? string.Empty))
            {
                await next.Send(context);
            }
        }
    }
}
