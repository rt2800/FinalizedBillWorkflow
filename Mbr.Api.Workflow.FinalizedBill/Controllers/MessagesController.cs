using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Services;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.BackgroundServices;
using Serilog.Context;

namespace Mbr.Api.Workflow.FinalizedBill.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class MessagesController : ControllerBase
{
    private readonly ISchemaValidationService _validator;
    private readonly IRabbitMqPublisher _publisher;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly IExternalApiService _externalApi;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        ISchemaValidationService validator,
        IRabbitMqPublisher publisher,
        IBackgroundTaskQueue taskQueue,
        IExternalApiService externalApi,
        ILogger<MessagesController> logger)
    {
        _validator = validator;
        _publisher = publisher;
        _taskQueue = taskQueue;
        _externalApi = externalApi;
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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpPost("{schemaName}")]
    [ProducesResponseType(typeof(ApiResponse<PublishReceipt>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>),          StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PublishMessage(
        [FromRoute] string schemaName,
        [FromBody]  JsonObject payload,
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

        if (payload is null)
            return BadRequest(ApiResponse<object>.Error("Request body must not be empty and must be a valid JSON object."));

        // ── 2. Validate against the registered JSON Schema ────────────────────
        var validationResult = await _validator.ValidateAsync(schemaName, payload, ct);

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
        var payloadString = payload.ToJsonString();

        // Extract MicBillId from payload and CorrelationId from headers
        var micBillId = payload["MicBillId"]?.ToString() ?? payload["orderId"]?.ToString() ?? "unknown";
        var correlationId = Request.Headers["x-correlation-id"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = HttpContext.TraceIdentifier;
        }

        // ── 3. Publish to RabbitMQ (Immediate with fallback) ─────────────────
        PublishReceipt receipt;
        using (LogContext.PushProperty("SchemaName", schemaName))
        using (LogContext.PushProperty("TargetQueue", targetQueue))
        using (LogContext.PushProperty("MicBillId", micBillId))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            receipt = await _publisher.PublishAsync(
                queueName: targetQueue,
                payload: payload,
                headers: new Dictionary<string, object?>
                {
                    ["x-schema-name"] = schemaName,
                    ["x-client-ip"] = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    ["x-correlation-id"] = correlationId,
                    ["x-mic-bill-id"] = micBillId
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
            Target = targetQueue,
            MicBillId = micBillId,
            CorrelationId = correlationId
        });

        await _taskQueue.EnqueueAsync(new BackgroundTask
        {
            Id = receipt.MessageId,
            Type = BackgroundTaskType.AuditLog,
            Payload = payloadString,
            Target = targetQueue,
            MicBillId = micBillId,
            CorrelationId = correlationId
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
    // POST /api/messages/acceptstatus
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a status update. If the format matches a specific Bill status structure,
    /// it is forwarded synchronously to the Client endpoint.
    /// Otherwise, it is processed as a generic JSON: enqueued for BEM post and SFTP upload.
    /// </summary>
    [Authorize(AuthenticationSchemes = "Basic")]
    [HttpPost("acceptstatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> AcceptStatus(
        [FromBody] JsonObject payload,
        CancellationToken ct)
    {
        var correlationId = Request.Headers["x-correlation-id"].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = HttpContext.TraceIdentifier;
        }

        var payloadString = payload.ToJsonString();

        // ── 1. Determine if it matches the specific format ────────────────────
        // { "BillId": "...", "BillSource": "...", "Status": 0|1, "BatchCorrelationId": "...", "Error": [...] }
        bool isSpecificFormat =
            payload.ContainsKey("BillId") &&
            payload.ContainsKey("BillSource") &&
            payload.ContainsKey("Status") &&
            payload.ContainsKey("BatchCorrelationId") &&
            payload.ContainsKey("Error");

        if (isSpecificFormat)
        {
            var statusNode = payload["Status"];
            if (statusNode is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var statusValue))
            {
                if (statusValue != 0 && statusValue != 1)
                {
                    isSpecificFormat = false;
                }
            }
            else
            {
                isSpecificFormat = false;
            }
        }

        if (isSpecificFormat)
        {
            // ── 2. Forward to Client Endpoint Synchronously ───────────────────
            var billId = payload["BillId"]?.ToString() ?? "unknown";
            var batchCorrId = payload["BatchCorrelationId"]?.ToString() ?? correlationId;

            _logger.LogInformation("Detected specific status format for BillId {BillId}. Forwarding to client endpoint.", billId);

            await _externalApi.PostToClientAsync(payloadString, billId, batchCorrId, ct);

            return Ok(ApiResponse<object>.Ok(new { }, "Status forwarded to client endpoint."));
        }
        else
        {
            // ── 3. Generic JSON: Process via Background Tasks ──────────────────
            var micBillId = payload["micbillid"]?.ToString() ?? "unknown";

            _logger.LogInformation("Generic JSON received. Enqueuing for BEM and SFTP.");

            await _taskQueue.EnqueueAsync(new BackgroundTask
            {
                Type = BackgroundTaskType.BemPost,
                Payload = payloadString,
                MicBillId = micBillId,
                CorrelationId = correlationId
            });

            await _taskQueue.EnqueueAsync(new BackgroundTask
            {
                Type = BackgroundTaskType.SftpUpload,
                Payload = payloadString,
                MicBillId = micBillId,
                CorrelationId = correlationId
            });

            return Accepted(ApiResponse<object>.Ok(new { }, "Generic JSON accepted for processing."));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // POST /api/messages/{schemaName}/validate  (dry-run — no publish)
    // ──────────────────────────────────────────────────────────────────────────

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpPost("{schemaName}/validate")]
    [ProducesResponseType(typeof(ApiResponse<ValidationSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ValidationSummary>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>),             StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ValidateOnly(
        [FromRoute] string schemaName,
        [FromBody]  JsonObject payload,
        CancellationToken ct)
    {
        if (!_validator.RegisteredSchemas.Contains(schemaName, StringComparer.OrdinalIgnoreCase))
            return NotFound(ApiResponse<object>.Error($"Schema '{schemaName}' is not registered."));

        if (payload is null)
            return BadRequest(ApiResponse<object>.Error("Request body must not be empty and must be a valid JSON object."));

        var result = await _validator.ValidateAsync(schemaName, payload, ct);

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
