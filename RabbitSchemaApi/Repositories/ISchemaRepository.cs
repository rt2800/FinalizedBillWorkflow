using Json.Schema;

namespace RabbitSchemaApi.Repositories;

public interface ISchemaRepository
{
    JsonSchema? GetSchema(string schemaName);
    IReadOnlyList<string> RegisteredSchemas { get; }
}
