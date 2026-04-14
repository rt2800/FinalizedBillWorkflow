namespace Mbr.Api.Workflow.FinalizedBill.Models;

/// <summary>
/// Unified API response envelope returned on every endpoint.
/// </summary>
/// <typeparam name="T">Type of the payload data.</typeparam>
public sealed record ApiResponse<T>
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }
    public IReadOnlyList<string>? Errors { get; init; }

    public static ApiResponse<T> Ok(T data, string message = "Request processed successfully") =>
        new() { Success = true, Message = message, Data = data };

    public static ApiResponse<T> Fail(IEnumerable<string> errors, string message = "Validation failed") =>
        new() { Success = false, Message = message, Errors = errors.ToList().AsReadOnly() };

    public static ApiResponse<T> Error(string error) =>
        new() { Success = false, Message = error, Errors = [error] };
}

/// <summary>
/// Result of a JSON schema validation attempt.
/// </summary>
public sealed record SchemaValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public static SchemaValidationResult Valid() =>
        new() { IsValid = true };

    public static SchemaValidationResult Invalid(IEnumerable<ValidationError> errors) =>
        new() { IsValid = false, Errors = errors.ToList().AsReadOnly() };
}

/// <summary>
/// Represents a single schema violation with a JSON pointer path and human-readable message.
/// </summary>
public sealed record ValidationError(string Path, string Message, string? SchemaKeyword = null);

/// <summary>
/// Represents a message successfully published to RabbitMQ.
/// </summary>
public sealed record PublishReceipt
{
    public required string MessageId { get; init; }
    public required string Queue { get; init; }
    public required string Exchange { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// Registration entry mapping a named schema to its JSON file path on disk.
/// Loaded from appsettings.json under "SchemaRegistry".
/// </summary>
public sealed class SchemaRegistryEntry
{
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
}

/// <summary>
/// Configuration settings for SFTP upload.
/// </summary>
public sealed class SftpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/";
}

/// <summary>Strongly-typed settings bound from appsettings.json "RabbitMQ" section.</summary>
public sealed class RabbitMqSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string ExchangeName { get; set; } = "";    // "" = default AMQP exchange
    public string DeadLetterExchange { get; set; } = "dlx";
}

/// <summary>Returned by the dry-run validate endpoint.</summary>
public sealed record ValidationSummary(
    string SchemaName,
    bool IsValid,
    int ErrorCount,
    IReadOnlyList<ValidationError> Errors);

/// <summary>
/// Configuration for external BEM and Client endpoints.
/// </summary>
public sealed class ExternalEndpointsSettings
{
    public string ClientEndpointUrl { get; set; } = string.Empty;
    public string BemEndpointUrl { get; set; } = string.Empty;
    public string BemServiceToken { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for Basic Authentication.
/// </summary>
public sealed class BasicAuthSettings
{
    public List<BasicAuthClient> Clients { get; set; } = [];
}

/// <summary>
/// Represents a client credential for Basic Authentication.
/// </summary>
public sealed class BasicAuthClient
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
