using System.Text.Json.Nodes;
using Json.Schema;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Repositories;

namespace Mbr.Api.Workflow.FinalizedBill.Services;

public interface ISchemaValidationService
{
    /// <summary>
    /// Validates a raw JSON string against the schema registered under <paramref name="schemaName"/>.
    /// </summary>
    ValueTask<SchemaValidationResult> ValidateAsync(string schemaName, JsonNode payload, CancellationToken ct = default);

    /// <summary>Returns the names of all registered schemas.</summary>
    IReadOnlyList<string> RegisteredSchemas { get; }
}

/// <summary>
/// Loads JSON Schema files at startup, caches compiled schemas, and validates
/// incoming payloads. Uses JsonSchema.Net which supports Draft 7 / 2019-09 / 2020-12.
/// </summary>
public sealed class SchemaValidationService : ISchemaValidationService
{
    private static readonly EvaluationOptions _defaultOptions = new()
    {
        OutputFormat = OutputFormat.List,
        EvaluateAs = SpecVersion.Draft202012
    };

    private readonly ISchemaRepository _repository;
    private readonly ILogger<SchemaValidationService> _logger;

    public IReadOnlyList<string> RegisteredSchemas => _repository.RegisteredSchemas;

    public SchemaValidationService(
        ISchemaRepository repository,
        ILogger<SchemaValidationService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public ValueTask<SchemaValidationResult> ValidateAsync(
        string schemaName,
        JsonNode payload,
        CancellationToken ct = default)
    {
        var schema = _repository.GetSchema(schemaName);
        if (schema == null)
        {
            // Unknown schema — return a clear error rather than silently failing
            return ValueTask.FromResult(SchemaValidationResult.Invalid(
            [
                new ValidationError("$", $"No schema registered with name '{schemaName}'.")
            ]));
        }

        var result = schema.Evaluate(payload, _defaultOptions);

        if (result.IsValid)
            return ValueTask.FromResult(SchemaValidationResult.Valid());

        // Flatten the nested error tree into a list of ValidationError records.
        var errors = FlattenErrors(result);
        _logger.LogDebug("Schema '{Name}' validation failed with {Count} error(s)", schemaName, errors.Count);

        return ValueTask.FromResult(SchemaValidationResult.Invalid(errors));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static List<ValidationError> FlattenErrors(EvaluationResults results)
    {
        var errors = new List<ValidationError>();
        var stack = new Stack<EvaluationResults>();
        stack.Push(results);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.IsValid) continue;

            var path = current.InstanceLocation?.ToString() ?? "$";

            if (current.Errors is { Count: > 0 })
            {
                foreach (var (keyword, message) in current.Errors)
                {
                    errors.Add(new ValidationError(path, message, keyword));
                }
            }

            if (current.Details is { Count: > 0 })
            {
                // Push details to stack to process them iteratively
                // Reversing to maintain same order as recursive if desired, but here order usually doesn't matter much for a flat list
                foreach (var detail in current.Details)
                {
                    stack.Push(detail);
                }
            }
        }

        return errors;
    }
}
