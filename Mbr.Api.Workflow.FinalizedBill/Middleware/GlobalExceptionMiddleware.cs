using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.Services;

namespace Mbr.Api.Workflow.FinalizedBill.Middleware;

/// <summary>
/// Global exception handler — catches unhandled exceptions, logs them, and
/// returns a consistent JSON error envelope rather than raw HTML/stack traces.
/// Registered in Program.cs before all other middleware.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error, just stop processing
            _logger.LogDebug("Request {TraceId} was cancelled by the client", context.TraceIdentifier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path} [{TraceId}]",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);

            await HandleExceptionAsync(context, ex);
            await WriteErrorResponseAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var repository = context.RequestServices.GetRequiredService<IFinalizedBillRepository>();
        var emailSender = context.RequestServices.GetRequiredService<IEmailSender>();

        var correlationId = context.Request.Headers["x-correlation-id"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        // We don't easily have MicBillId here as the body might have been consumed or not yet parsed.
        // In a real scenario, we might want to use a custom middleware to buffer the request body if needed.

        await repository.AddExceptionLogAsync(ex, correlationId: correlationId, context: $"{context.Request.Method} {context.Request.Path}");

        var subject = $"Critical Error in Mbr.Api.Workflow.FinalizedBill: {ex.Message}";
        var body = $"An unhandled exception occurred.\n\n" +
                   $"Method: {context.Request.Method}\n" +
                   $"Path: {context.Request.Path}\n" +
                   $"CorrelationId: {correlationId}\n" +
                   $"Exception: {ex.Message}\n\n" +
                   $"Stack Trace:\n{ex.StackTrace}";

        await emailSender.SendEmailAsync(subject, body);
    }

    private static async Task WriteErrorResponseAsync(HttpContext context, Exception ex)
    {
        context.Response.StatusCode  = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        var isDevelopment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>()
            .IsDevelopment();

        var response = ApiResponse<object>.Error(
            isDevelopment
                ? $"Internal error: {ex.Message}"          // expose detail in dev
                : "An unexpected error occurred.");         // hide detail in prod

        var json = JsonSerializer.Serialize(response, _jsonOptions);
        await context.Response.WriteAsync(json);
    }
}
