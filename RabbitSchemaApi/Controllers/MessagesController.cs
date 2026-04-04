using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RabbitSchemaApi.Models;
using RabbitSchemaApi.Services;
using RabbitSchemaApi.Repositories;
using RabbitSchemaApi.BackgroundServices;
using Serilog.Context;

namespace RabbitSchemaApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class MessagesController : ControllerBase
{
    private readonly ISchemaValidationService _validator;
    private readonly IRabbitMqPublisher _publisher;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        ISchemaValidationService validator,
        IRabbitMqPublisher publisher,
        IBackgroundTaskQueue taskQueue,
        ILogger<MessagesController> logger)
    {
        _validator = validator;
        _publisher = publisher;
        _taskQueue = taskQueue;
        _logger    = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // POST /api/messages/{schemaName}
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the request body against the named JSON Schema and, if valid,
    /// publishes the payload to the RabbitMQ queue that matches the schema name.
    /// SFTP upload and Audit logging are handled in background tasks for performance.
    /// </summary>
    [Authorize]
    [HttpPost("{schemaName}")]
    [ProducesResponseType(typeof(ApiResponse<PublishReceipt>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PublishMessage(
        [FromRoute] string schemaName,
        [FromQuery] string? queueName,
        CancellationToken ct)
    {
        // ── 1. Check that the schema exists ───────────────────────────────────
        if (!_validator.RegisteredSchemas.Contains(schemaName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unknown schema requested: '{Schema}'", schemaName);
            return NotFound(ApiResponse<object>.Error(
                $"Schema '{schemaName}' is not registered. Available schemas: " +
                string.Join(", ", _validator.RegisteredSchemas)));
        }

        // ── 2. Read and parse the raw JSON body ───────────────────────────────
        JsonNode? payloadNode;
        try
        {
            using var reader = new StreamReader(Request.Body);
            var rawJson = await reader.ReadToEndAsync(ct);

            if (string.IsNullOrWhiteSpace(rawJson))
                return BadRequest(ApiResponse<object>.Error("Request body must not be empty."));

            payloadNode = JsonNode.Parse(rawJson);

            if (payloadNode is null)
                return BadRequest(ApiResponse<object>.Error("Request body is not valid JSON."));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Malformed JSON in request body");
            return BadRequest(ApiResponse<object>.Error($"Malformed JSON: {ex.Message}"));
        }

        // ── 3. Validate against the registered JSON Schema ────────────────────
        var validationResult = await _validator.ValidateAsync(schemaName, payloadNode, ct);

        if (!validationResult.IsValid)
        {
            _logger.LogInformation(
                "Payload rejected by schema '{Schema}' — {Count} violation(s)",
                schemaName, validationResult.Errors.Count);

            return UnprocessableEntity(ApiResponse<ValidationSummary>.Fail(
                validationResult.Errors.Select(e => $"[{e.Path}] {e.Message}"),
                $"Payload does not conform to schema '{schemaName}'."));
        }

        var targetQueue = queueName ?? schemaName;
        var payloadString = payloadNode.ToJsonString();

        // ── 4. Publish to RabbitMQ (Immediate with fallback) ─────────────────
        PublishReceipt receipt;
        using (LogContext.PushProperty("SchemaName", schemaName))
        using (LogContext.PushProperty("TargetQueue", targetQueue))
        {
            receipt = await _publisher.PublishAsync(
                queueName: targetQueue,
                payload: payloadNode,
                headers: new Dictionary<string, object?>
                {
                    ["x-schema-name"] = schemaName,
                    ["x-client-ip"] = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    ["x-correlation-id"] = HttpContext.TraceIdentifier
                },
                ct: ct);

            _logger.LogInformation(
                "Message {MessageId} processed for schema '{Schema}' → queue '{Queue}' (Exchange: {Exchange})",
                receipt.MessageId, schemaName, targetQueue, receipt.Exchange);
        }

        // ── 5. Enqueue Background Tasks (SFTP and Audit Logging) ──────────────
        // These are non-blocking for high performance.
        await _taskQueue.EnqueueAsync(new BackgroundTask
        {
            Id = receipt.MessageId,
            Type = BackgroundTaskType.SftpUpload,
            Payload = payloadString,
            Target = targetQueue
        });

        await _taskQueue.EnqueueAsync(new BackgroundTask
        {
            Id = receipt.MessageId,
            Type = BackgroundTaskType.AuditLog,
            Payload = payloadString,
            Target = targetQueue
        });

        return Accepted(ApiResponse<PublishReceipt>.Ok(receipt, "Payload validated and publication initiated."));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GET /api/messages/schemas
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("schemas")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<string>>), StatusCodes.Status200OK)]
    public IActionResult GetSchemas()
    {
        return Ok(ApiResponse<IReadOnlyList<string>>.Ok(
            _validator.RegisteredSchemas,
            $"{_validator.RegisteredSchemas.Count} schema(s) registered."));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // POST /api/messages/{schemaName}/validate  (dry-run — no publish)
    // ──────────────────────────────────────────────────────────────────────────

    [Authorize]
    [HttpPost("{schemaName}/validate")]
    [ProducesResponseType(typeof(ApiResponse<ValidationSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ValidationSummary>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>),             StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ValidateOnly(
        [FromRoute] string schemaName,
        CancellationToken ct)
    {
        if (!_validator.RegisteredSchemas.Contains(schemaName, StringComparer.OrdinalIgnoreCase))
            return NotFound(ApiResponse<object>.Error($"Schema '{schemaName}' is not registered."));

        JsonNode? payloadNode;
        try
        {
            using var reader = new StreamReader(Request.Body);
            var rawJson = await reader.ReadToEndAsync(ct);
            payloadNode = JsonNode.Parse(rawJson);

            if (payloadNode is null)
                return BadRequest(ApiResponse<object>.Error("Request body is not valid JSON."));
        }
        catch (JsonException ex)
        {
            return BadRequest(ApiResponse<object>.Error($"Malformed JSON: {ex.Message}"));
        }

        var result = await _validator.ValidateAsync(schemaName, payloadNode, ct);

        var summary = new ValidationSummary(
            SchemaName : schemaName,
            IsValid    : result.IsValid,
            ErrorCount : result.Errors.Count,
            Errors     : result.Errors
        );

        return Ok(result.IsValid
            ? ApiResponse<ValidationSummary>.Ok(summary, "Payload is valid.")
            : ApiResponse<ValidationSummary>.Fail(
                result.Errors.Select(e => $"[{e.Path}] {e.Message}"),
                "Payload has schema violations."));
    }
}
