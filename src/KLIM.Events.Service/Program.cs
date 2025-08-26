using KLIM.Events.Logging;
using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.ChangeTracking;
using KLIM.Events.Service.Infrastructure.HealthChecks;
using KLIM.Events.Service.Infrastructure.Outbox;
using MassTransit;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog once, rely solely on configuration (avoids duplicate console logs)
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    // Removed explicit .WriteTo.Console() to prevent duplicate console entries (console sink already in appsettings.json)
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

// Outbox services
builder.Services.AddSingleton<SqlAuthenticationService>();
builder.Services.AddSingleton<OutboxRepository>();
builder.Services.AddSingleton<MessagePublisher>();

// Change tracking infrastructure - specialized projectors for each entity type
builder.Services.AddSingleton<IOutboxWriter, SqlOutboxWriter>();
builder.Services.AddSingleton<IChangeEventProjector, IssuerProjector>();
builder.Services.AddSingleton<IChangeEventProjector, DealProjector>();
builder.Services.AddSingleton<IChangeEventProjector, GenericDomainChangeProjector>();

// MassTransit - KLIM Standards Compliant Configuration
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    // Publishing only (no consumer endpoint registered here)

    x.UsingRabbitMq((context, cfg) =>
    {
        var mq = context.GetRequiredService<IOptions<RabbitMQOptions>>().Value;

        cfg.Host(mq.Host, h =>
        {
            h.Username(mq.Username);
            h.Password(mq.Password);
        });

        cfg.UseConsumeFilter(typeof(CorrelationConsumeFilter<>), context);
        cfg.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
            r.Ignore<ArgumentException>();
        });

        // KLIM Standard: Configure messages to use shared domain exchange
        cfg.Message<DataChangedV1>(m => m.SetEntityName(mq.ExchangeName));
        cfg.Message<DomainChangeNotification>(m => m.SetEntityName(mq.ExchangeName));
        cfg.Message<DataChangeProcessed>(m => m.SetEntityName(mq.ExchangeName));

        // KLIM Standard: Configure as topic exchange
        cfg.Publish<DataChangedV1>(p =>
        {
            p.ExchangeType = "topic";
        });
        cfg.Publish<DomainChangeNotification>(p =>
        {
            p.ExchangeType = "topic";
        });
        cfg.Publish<DataChangeProcessed>(p =>
        {
            p.ExchangeType = "topic";
        });
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

// KLIM Standard: RabbitMQ Configuration Class
public sealed class RabbitMQOptions
{
    public string Host { get; init; } = "localhost";
    public string Username { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string ExchangeName { get; init; } = "klim.events"; // ✅ CLEAN: Simple and direct
    public string RoutingKeyPrefix { get; init; } = "klim.events"; // ✅ CONSISTENT: Same clean pattern
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
