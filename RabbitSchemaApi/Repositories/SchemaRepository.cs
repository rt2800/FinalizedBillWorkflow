using Json.Schema;
using Microsoft.Extensions.Options;
using RabbitSchemaApi.Models;

namespace RabbitSchemaApi.Repositories;

public sealed class SchemaRepository : ISchemaRepository
{
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SchemaRepository> _logger;

    public IReadOnlyList<string> RegisteredSchemas => _schemas.Keys.ToList().AsReadOnly();

    public SchemaRepository(
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<SchemaRepository> logger)
    {
        _logger = logger;

        var entries = configuration
            .GetSection("SchemaRegistry")
            .Get<List<SchemaRegistryEntry>>() ?? [];

        if (entries.Count == 0)
            _logger.LogWarning("No schemas found in SchemaRegistry configuration section.");

        foreach (var entry in entries)
        {
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

    public JsonSchema? GetSchema(string schemaName)
    {
        _schemas.TryGetValue(schemaName, out var schema);
        return schema;
    }
}
