using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.ChangeTracking;
using KLIM.Events.Service.Infrastructure.HealthChecks;
using KLIM.Events.Service.Infrastructure.Outbox;
using MassTransit;
using Microsoft.Extensions.Options;
using Serilog;
using System.ComponentModel.DataAnnotations;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog once, rely solely on configuration (avoids duplicate console logs)
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddLogging(lb =>
{
    lb.AddSerilog();
    // Add a filter to suppress MassTransit SENT messages
    lb.AddFilter("MassTransit", LogLevel.Warning);
    lb.AddFilter("MassTransit.RabbitMqTransport", LogLevel.Error);
    lb.AddFilter("MassTransit.Transports", LogLevel.Error);
    lb.AddFilter((category, level) =>
    {
        // Filter out any log message that contains these patterns
        if (category?.StartsWith("MassTransit") == true && level == LogLevel.Information)
        {
            return false;
        }
        return true;
    });
});

// Configuration - consolidated database settings and KLIM-standard RabbitMQ
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<ChangeTrackingOptions>(builder.Configuration.GetSection("ChangeTracking"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("MassTransit:Outbox"));
builder.Services.Configure<RabbitMQOptions>(builder.Configuration.GetSection("RabbitMQ"));

// Configuration validation - ensure naming conventions are followed
builder.Services.AddOptionsWithValidateOnStart<RabbitMQOptions>();

// Outbox services
builder.Services.AddSingleton<SqlAuthenticationService>();
builder.Services.AddSingleton<OutboxRepository>();
builder.Services.AddSingleton<MessagePublisher>();

// Change tracking infrastructure - specialized projectors for each entity type
builder.Services.AddSingleton<IOutboxWriter, SqlOutboxWriter>();
builder.Services.AddSingleton<IChangeEventProjector, IssuerProjector>();
builder.Services.AddSingleton<IChangeEventProjector, DealProjector>();
builder.Services.AddSingleton<IChangeEventProjector, GenericDomainChangeProjector>();

// MassTransit - Publisher-Only Configuration
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    
    x.UsingRabbitMq((context, cfg) =>
    {
        var mq = context.GetRequiredService<IOptions<RabbitMQOptions>>().Value;

        cfg.Host(mq.Host, h =>
        {
            h.Username(mq.Username);
            h.Password(mq.Password);
        });

        // KLIM Standard: Configure messages to use environment-aware domain exchange
        cfg.Message<DataChangedV1>(m => m.SetEntityName(mq.FullExchangeName));
        cfg.Message<DomainChangeNotification>(m => m.SetEntityName(mq.FullExchangeName));

        // KLIM Standard: Configure as topic exchange for flexible routing
        cfg.Publish<DataChangedV1>(p => p.ExchangeType = "topic");
        cfg.Publish<DomainChangeNotification>(p => p.ExchangeType = "topic");
    });
});

// Background services
builder.Services.AddHostedService<ChangeTrackingPollingService>();
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddHostedService<OutboxDiagnosticService>();

// Health checks - use shared database configuration
var dbOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()!;
builder.Services.AddHealthChecks()
    .AddSqlServer(
        connectionString: GetSanitizedConnectionString(dbOptions),
        name: "sql",
        tags: new[] { "readiness" })
    .AddRabbitMQ(tags: new[] { "readiness" });

builder.Services.AddHostedService<HealthEndpointHostService>();

await builder.Build().RunAsync();

static string GetSanitizedConnectionString(DatabaseOptions options)
{
    if (string.IsNullOrEmpty(options.ConnectionString) || !options.UseAzureAd)
        return options.ConnectionString;

    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString);
    builder.Remove("User ID");
    builder.Remove("Password");
    return builder.ConnectionString;
}

/// <summary>
/// KLIM Standard: RabbitMQ Configuration Class
/// Implements enterprise naming conventions for exchanges, routing keys, and queues
/// </summary>
public sealed class RabbitMQOptions
{
    /// <summary>RabbitMQ host (e.g., "rabbitmq.company.com" or "localhost")</summary>
    [Required]
    public string Host { get; init; } = "localhost";
    
    /// <summary>RabbitMQ username for authentication</summary>
    [Required]
    public string Username { get; init; } = "guest";
    
    /// <summary>RabbitMQ password for authentication</summary>
    [Required]
    public string Password { get; init; } = "guest";
    
    /// <summary>
    /// Base exchange name following {company}.{domain} pattern
    /// Examples: "klim.events", "acme.commands", "contoso.notifications"
    /// </summary>
    [Required]
    [RegularExpression(@"^[a-z]+\.[a-z]+$", ErrorMessage = "Exchange name must follow 'company.domain' pattern with lowercase letters")]
    public string ExchangeName { get; init; } = "klim.events";
    
    /// <summary>
    /// Environment identifier for multi-environment deployments
    /// Examples: "dev", "staging", "prod"
    /// </summary>
    [Required]
    [RegularExpression(@"^[a-z]+$", ErrorMessage = "Environment must be lowercase letters only")]
    public string Environment { get; init; } = "dev";
    
    /// <summary>
    /// Routing key prefix following {company}.{domain} pattern
    /// Used as base for all message routing keys
    /// </summary>
    [Required]
    [RegularExpression(@"^[a-z]+\.[a-z]+$", ErrorMessage = "Routing key prefix must follow 'company.domain' pattern")]
    public string RoutingKeyPrefix { get; init; } = "klim.events";
    
    /// <summary>
    /// Template for data change event routing keys
    /// Pattern: {prefix}.{entity}.{operation}.{version}
    /// Example: "klim.events.issuer.created.v1"
    /// </summary>
    public string DataChangeRoutingKeyTemplate { get; init; } = "{prefix}.{entity}.{operation}.{version}";
    
    /// <summary>
    /// Routing key for domain notification messages
    /// Pattern: {prefix}.domain.notification.{version}
    /// </summary>
    public string DomainNotificationRoutingKey { get; init; } = "{prefix}.domain.notification.v1";
    
    /// <summary>
    /// Dead letter exchange suffix for failed message handling
    /// Results in: "{FullExchangeName}.dlq"
    /// </summary>
    public string DeadLetterSuffix { get; init; } = "dlq";
    
    /// <summary>
    /// Environment-aware full exchange name
    /// Pattern: {ExchangeName}.{Environment}
    /// Examples: "klim.events.dev", "klim.events.prod"
    /// </summary>
    public string FullExchangeName => $"{ExchangeName}.{Environment}";
    
    /// <summary>
    /// Dead letter exchange name for failed messages
    /// Pattern: {FullExchangeName}.{DeadLetterSuffix}
    /// Example: "klim.events.prod.dlq"
    /// </summary>
    public string DeadLetterExchangeName => $"{FullExchangeName}.{DeadLetterSuffix}";
    
    /// <summary>
    /// Generates a routing key for data change events
    /// </summary>
    /// <param name="entityType">Entity type (e.g., "issuer", "deal")</param>
    /// <param name="operation">Operation type (e.g., "created", "updated", "deleted")</param>
    /// <param name="version">Message version (e.g., "v1", "v2")</param>
    /// <returns>Formatted routing key</returns>
    public string GetDataChangeRoutingKey(string entityType, string operation, string version = "v1") =>
        DataChangeRoutingKeyTemplate
            .Replace("{prefix}", RoutingKeyPrefix)
            .Replace("{entity}", entityType.ToLowerInvariant())
            .Replace("{operation}", operation.ToLowerInvariant())
            .Replace("{version}", version.ToLowerInvariant());
    
    /// <summary>
    /// Generates a routing key for domain notifications
    /// </summary>
    /// <param name="version">Message version (default: "v1")</param>
    /// <returns>Formatted routing key</returns>
    public string GetDomainNotificationRoutingKey(string version = "v1") =>
        DomainNotificationRoutingKey
            .Replace("{prefix}", RoutingKeyPrefix)
            .Replace("{version}", version.ToLowerInvariant());
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public bool UseAzureAd { get; set; } = false;
}

public sealed class ChangeTrackingOptions
{
    public int PollingIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 500;
    public List<TrackedTableOption> Tables { get; set; } = new();

    public sealed record TrackedTableOption(string Schema, string Name, string Pk);
}

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int DeliveryIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 100;
    public int MaxConcurrentDispatches { get; set; } = 10;
    public int DuplicateDetectionWindowMinutes { get; set; } = 30;
    public int RetentionDays { get; set; } = 7;
    public double CleanupIntervalHours { get; set; } = 6;
    public bool UseAzureAd { get; set; } = false;
}