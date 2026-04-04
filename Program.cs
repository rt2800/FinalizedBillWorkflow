using Scalar.AspNetCore;
using Serilog;
using RabbitSchemaApi.Middleware;
using RabbitSchemaApi.Services;

// ── Bootstrap Serilog early so startup errors are captured ───────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting RabbitSchemaApi...");

    var builder = WebApplication.CreateBuilder(args);

    // ── Logging ──────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, config) =>
        config
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}"));

    // ── Configuration binding ─────────────────────────────────────────────────
    builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMQ"));

    // ── Core services ─────────────────────────────────────────────────────────
    builder.Services
        .AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.WriteIndented        = false;
        });

    // ── Domain services ───────────────────────────────────────────────────────
    //
    // SchemaValidationService — Singleton because:
    //   • schemas are loaded from disk once at startup and cached forever
    //   • JsonSchema objects are thread-safe and immutable after construction
    builder.Services.AddSingleton<ISchemaValidationService, SchemaValidationService>();

    // RabbitMqPublisher — Singleton because:
    //   • a single AMQP connection should be shared across all requests
    //   • the client's own auto-recovery handles reconnects transparently
    builder.Services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

    // ── OpenAPI / Scalar ─────────────────────────────────────────────────────
    //
    // .NET 10 ships built-in OpenAPI document generation via AddOpenApi().
    // Scalar replaces Swagger UI with a modern, zero-config UI.
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title       = "RabbitSchema API";
            doc.Info.Version     = "v1";
            doc.Info.Description =
                "Accepts JSON payloads, validates them against registered OpenAPI/JSON Schema " +
                "definitions, and publishes conforming messages to RabbitMQ.";
            doc.Info.Contact = new() { Name = "Platform Team", Email = "platform@example.com" };
            return Task.CompletedTask;
        });
    });

    // ── Health checks ────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks();
    // TODO: add .AddRabbitMQ() from AspNetCore.HealthChecks.Rabbitmq if desired

    // ── Build ─────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    // Order is important — global exception handler must be first.

    app.UseMiddleware<GlobalExceptionMiddleware>();

    app.UseSerilogRequestLogging(opts =>
    {
        opts.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0}ms";
    });

    if (app.Environment.IsDevelopment())
    {
        // Map the auto-generated OpenAPI JSON document
        app.MapOpenApi();

        // Scalar UI — available at /scalar/v1
        app.MapScalarApiReference(opts =>
        {
            opts.Title                   = "RabbitSchema API";
            opts.Theme                   = ScalarTheme.DeepSpace;
            opts.DefaultHttpClient       = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
            opts.ShowSidebar             = true;
            opts.HideDownloadButton      = false;
        });
    }

    app.UseHttpsRedirection();
    app.UseRouting();

    app.MapControllers();
    app.MapHealthChecks("/health");

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
