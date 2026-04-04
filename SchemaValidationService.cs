using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using RabbitSchemaApi.Models;

namespace RabbitSchemaApi.Services;

public interface ISchemaValidationService
{
    /// <summary>
    /// Validates a raw JSON string against the schema registered under <paramref name="schemaName"/>.
    /// </summary>
    Task<SchemaValidationResult> ValidateAsync(string schemaName, JsonNode payload, CancellationToken ct = default);

    /// <summary>Returns the names of all registered schemas.</summary>
    IReadOnlyList<string> RegisteredSchemas { get; }
}

/// <summary>
/// Loads JSON Schema files at startup, caches compiled schemas, and validates
/// incoming payloads. Uses JsonSchema.Net which supports Draft 7 / 2019-09 / 2020-12.
/// </summary>
public sealed class SchemaValidationService : ISchemaValidationService
{
    // Compiled schemas keyed by their registered name (e.g. "order")
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SchemaValidationService> _logger;

    public IReadOnlyList<string> RegisteredSchemas => _schemas.Keys.ToList().AsReadOnly();

    public SchemaValidationService(
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<SchemaValidationService> logger)
    {
        _logger = logger;

        var entries = configuration
            .GetSection("SchemaRegistry")
            .Get<List<SchemaRegistryEntry>>() ?? [];

        if (entries.Count == 0)
            _logger.LogWarning("No schemas found in SchemaRegistry configuration section.");

        foreach (var entry in entries)
        {
            // Resolve relative paths against the content root (project root in dev, publish dir in prod)
            var fullPath = Path.IsPathRooted(entry.FilePath)
                ? entry.FilePath
                : Path.Combine(env.ContentRootPath, entry.FilePath);

            if (!File.Exists(fullPath))
            {
                _logger.LogError("Schema file not found for '{Name}': {Path}", entry.Name, fullPath);
                continue;
            }

            try
            {
                var json = File.ReadAllText(fullPath);
                var schema = JsonSchema.FromText(json);
                _schemas[entry.Name] = schema;
                _logger.LogInformation("Loaded schema '{Name}' v{Version} from {Path}",
                    entry.Name, entry.Version, fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse schema '{Name}' at {Path}", entry.Name, fullPath);
                throw new InvalidOperationException(
                    $"Cannot start: schema '{entry.Name}' at '{fullPath}' is not valid JSON Schema.", ex);
            }
        }
    }

    public Task<SchemaValidationResult> ValidateAsync(
        string schemaName,
        JsonNode payload,
        CancellationToken ct = default)
    {
        if (!_schemas.TryGetValue(schemaName, out var schema))
        {
            // Unknown schema — return a clear error rather than silently failing
            return Task.FromResult(SchemaValidationResult.Invalid(
            [
                new ValidationError("$", $"No schema registered with name '{schemaName}'.")
            ]));
        }

        // EvaluationOptions controls how deeply the result tree is populated.
        // OutputFormat.List gives us a flat list of all errors — ideal for API responses.
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            EvaluateAs = SpecVersion.Draft202012
        };

        var result = schema.Evaluate(payload, options);

        if (result.IsValid)
            return Task.FromResult(SchemaValidationResult.Valid());

        // Flatten the nested error tree into a list of ValidationError records.
        var errors = FlattenErrors(result).ToList();
        _logger.LogDebug("Schema '{Name}' validation failed with {Count} error(s)", schemaName, errors.Count);

        return Task.FromResult(SchemaValidationResult.Invalid(errors));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ValidationError> FlattenErrors(EvaluationResults results)
    {
        // The evaluation tree can be deeply nested; we recurse to collect leaf errors.
        if (!results.IsValid)
        {
            var path = results.InstanceLocation?.ToString() ?? "$";

            if (results.Errors is { Count: > 0 })
            {
                foreach (var (keyword, message) in results.Errors)
                {
                    yield return new ValidationError(path, message, keyword);
                }
            }

            if (results.Details is { Count: > 0 })
            {
                foreach (var detail in results.Details)
                foreach (var error in FlattenErrors(detail))
                    yield return error;
            }
        }
    }
}
