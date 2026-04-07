using Json.Schema;

namespace Mbr.Api.Workflow.FinalizedBill.Repositories;

public interface ISchemaRepository
{
    JsonSchema? GetSchema(string schemaName);
    IReadOnlyList<string> RegisteredSchemas { get; }
}
