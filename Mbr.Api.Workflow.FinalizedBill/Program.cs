using Mbr.Api.Workflow.FinalizedBill;
using Mbr.Api.Workflow.FinalizedBill.Middleware;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting web application");
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog for Audit/Exception Repository Context ──────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

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
            opts.Title                   = "Mbr.Api.Workflow.FinalizedBill API";
            opts.Theme                   = ScalarTheme.DeepSpace;
            opts.DefaultHttpClient       = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
            opts.ShowSidebar             = true;
            // opts.HideDownloadButton      = false; // Obsolete in Scalar 2.x
        });

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Mbr.Api.Workflow.FinalizedBill API V1");
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
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
