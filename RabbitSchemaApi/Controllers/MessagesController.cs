using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using RabbitSchemaApi.Models;
using RabbitSchemaApi.Services;
using RabbitSchemaApi.Repositories;
using Serilog.Context;

namespace RabbitSchemaApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class MessagesController : ControllerBase
{
    private readonly ISchemaValidationService _validator;
    private readonly IRabbitMqPublisher _publisher;
    private readonly IFinalizedBillRepository _repository;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        ISchemaValidationService validator,
        IRabbitMqPublisher publisher,
        IFinalizedBillRepository repository,
        ILogger<MessagesController> logger)
    {
        _validator = validator;
        _publisher = publisher;
        _repository = repository;
        _logger    = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // POST /api/messages/{schemaName}
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the request body against the named JSON Schema and, if valid,
    /// publishes the payload to the RabbitMQ queue that matches the schema name.
    /// </summary>
    /// <param name="schemaName">
    /// Name of a registered schema (e.g. <c>order</c>). 
    /// Must match a key in the <c>SchemaRegistry</c> config section.
    /// </param>
    /// <param name="queueName">
    /// Optional override for the target RabbitMQ queue name.
    /// Defaults to <paramref name="schemaName"/> when omitted.
    /// </param>
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
            // Read raw bytes so we can pass the JsonNode to the validator
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

            // 422 Unprocessable Entity: the JSON was syntactically valid but
            // semantically wrong (schema violations).
            return UnprocessableEntity(ApiResponse<ValidationSummary>.Fail(
                validationResult.Errors.Select(e => $"[{e.Path}] {e.Message}"),
                $"Payload does not conform to schema '{schemaName}'."));
        }

        // ── 4. Publish to RabbitMQ ─────────────────────────────────────────────
        var targetQueue = queueName ?? schemaName;

        using (LogContext.PushProperty("SchemaName", schemaName))
        using (LogContext.PushProperty("TargetQueue", targetQueue))
        {
            try
            {
                var receipt = await _publisher.PublishAsync(
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
                    "Message {MessageId} accepted for schema '{Schema}' → queue '{Queue}'",
                    receipt.MessageId, schemaName, targetQueue);

                // Audit log successful publication
                await _repository.AddAuditLogAsync(receipt.MessageId, payloadNode.ToJsonString(), targetQueue);

                return Accepted(ApiResponse<PublishReceipt>.Ok(receipt, "Payload validated and published successfully."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to publish to RabbitMQ queue '{Queue}'", targetQueue);

                // Log exception to DB
                await _repository.AddExceptionLogAsync(ex, context: $"PublishMessage to {targetQueue}");

                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    ApiResponse<object>.Error("Message broker is unavailable. Please retry shortly."));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GET /api/messages/schemas
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Returns a list of all schema names that this API will accept.</summary>
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

    /// <summary>
    /// Validates the payload against the named schema WITHOUT publishing to RabbitMQ.
    /// Useful for client-side pre-validation and debugging.
    /// </summary>
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

/// <summary>Returned by the dry-run validate endpoint.</summary>
public sealed record ValidationSummary(
    string SchemaName,
    bool IsValid,
    int ErrorCount,
    IReadOnlyList<ValidationError> Errors);
