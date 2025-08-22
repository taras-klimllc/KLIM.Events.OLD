using KLIM.Events.Logging;
using KLIM.Events.Messaging.Consumers;
using KLIM.Events.Messaging.Contracts;
using KLIM.Events.Service.Infrastructure.Outbox;
using KLIM.Events.Service.Infrastructure.ChangeTracking;
using KLIM.Events.Service.Infrastructure.HealthChecks;
using MassTransit;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddLogging(lb => lb.AddSerilog());

// Configuration
builder.Services.Configure<ChangeTrackingOptions>(builder.Configuration.GetSection("ChangeTracking"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("MassTransit:Outbox"));

// Outbox services
builder.Services.AddSingleton<SqlAuthenticationService>();
builder.Services.AddSingleton<OutboxRepository>();
builder.Services.AddSingleton<MessagePublisher>();

// Change tracking infrastructure - specialized projectors for each entity type
builder.Services.AddSingleton<IOutboxWriter, SqlOutboxWriter>();
builder.Services.AddSingleton<IChangeEventProjector, IssuerProjector>();
builder.Services.AddSingleton<IChangeEventProjector, DealProjector>();
builder.Services.AddSingleton<IChangeEventProjector, GenericDomainChangeProjector>();

// MassTransit
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<DataChangedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var mq = builder.Configuration.GetSection("MassTransit:RabbitMQ").Get<RabbitOptions>()!;
        cfg.Host(mq.Host, (ushort)mq.Port, mq.VirtualHost, h =>
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

        // Configure generic data change messages
        cfg.Message<DataChangedV1>(m => m.SetEntityName("klim.change.events"));
        cfg.Publish<DataChangedV1>(p => { p.ExchangeType = "topic"; });

        cfg.ReceiveEndpoint("klim.events.datachanged.v1", ep =>
        {
            ep.ConfigureConsumer<DataChangedConsumer>(context);
            ep.Bind<DataChangedV1>(b => { b.RoutingKey = "data.changed.v1"; });
        });
    });
});

// Background services
builder.Services.AddHostedService<ChangeTrackingPollingService>();
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddHostedService<OutboxDiagnosticService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddSqlServer(
        connectionString: GetSanitizedConnectionString(
            builder.Configuration.GetSection("ChangeTracking").Get<ChangeTrackingOptions>()!),
        name: "sql",
        tags: new[] { "readiness" })
    .AddRabbitMQ(tags: new[] { "readiness" });

builder.Services.AddHostedService<HealthEndpointHostService>();

await builder.Build().RunAsync();

static string GetSanitizedConnectionString(ChangeTrackingOptions options)
{
    if (string.IsNullOrEmpty(options.ConnectionString) || !options.UseAzureAd)
        return options.ConnectionString;

    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString);
    builder.Remove("User ID");
    builder.Remove("Password");
    return builder.ConnectionString;
}

public sealed record RabbitOptions(string Host, int Port, string VirtualHost, string Username, string Password);

public sealed class ChangeTrackingOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public int PollingIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 500;
    public bool UseAzureAd { get; set; } = false;
    public List<TrackedTableOption> Tables { get; set; } = new();

    public sealed record TrackedTableOption(string Schema, string Name, string Pk);
}

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    public string ConnectionString { get; set; } = string.Empty;
    public int DeliveryIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 100;
    public int MaxConcurrentDispatches { get; set; } = 10;
    public int DuplicateDetectionWindowMinutes { get; set; } = 30;
    public int RetentionDays { get; set; } = 7;
    public double CleanupIntervalHours { get; set; } = 6;
    public bool UseAzureAd { get; set; } = false;
}
