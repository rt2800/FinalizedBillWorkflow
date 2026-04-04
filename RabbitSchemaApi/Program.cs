using NLog;
using NLog.Web;
using RabbitSchemaApi;
using RabbitSchemaApi.Middleware;
using Scalar.AspNetCore;
using Serilog;

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Debug("init main");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog for Audit/Exception Repository Context ──────────────────────
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .CreateLogger();

    builder.Host.UseSerilog();

    // ── NLog ────────────────────────────────────────────────────────────────
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // ── Infrastructure & Domain Services ────────────────────────────────────
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddDomainServices();

    // ── Core services ─────────────────────────────────────────────────────────
    builder.Services
        .AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.WriteIndented        = false;
        });

    // ── OpenAPI / Scalar / Swagger ───────────────────────────────────────────
    builder.Services.AddSwaggerSupport();

    // ── Health checks ────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks();

    // ── Build ─────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    app.UseMiddleware<GlobalExceptionMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(opts =>
        {
            opts.Title                   = "RabbitSchema API";
            opts.Theme                   = ScalarTheme.DeepSpace;
            opts.DefaultHttpClient       = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
            opts.ShowSidebar             = true;
            opts.HideDownloadButton      = false;
        });

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "RabbitSchema API V1");
        });
    }

    app.UseHttpsRedirection();
    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");

    await app.RunAsync();
}
catch (Exception ex)
{
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    LogManager.Shutdown();
}
