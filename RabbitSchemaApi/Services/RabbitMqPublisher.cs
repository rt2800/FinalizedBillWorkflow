using System.Text.Json.Nodes;
using EasyNetQ;
using Microsoft.Extensions.Options;
using RabbitSchemaApi.Models;
using RabbitSchemaApi.Repositories;
using RabbitSchemaApi.Resilience;
using Polly;

namespace RabbitSchemaApi.Services;

/// <summary>
/// A wrapper for JSON messages published via EasyNetQ.
/// Using a named type ensures a consistent topic and serialization.
/// </summary>
public record JsonMessage(string Content, string SchemaName);

public interface IRabbitMqPublisher : IDisposable
{
    Task<PublishReceipt> PublishAsync(
        string queueName,
        JsonNode payload,
        IDictionary<string, object?>? headers = null,
        CancellationToken ct = default);
}

public sealed class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly RabbitMqSettings _settings;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly IBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _fallbackDir;

    public RabbitMqPublisher(
        IOptions<RabbitMqSettings> settings,
        ILogger<RabbitMqPublisher> logger,
        IServiceScopeFactory scopeFactory)
    {
        _settings = settings.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;

        var connectionString = $"host={_settings.HostName};port={_settings.Port};username={_settings.UserName};password={_settings.Password};virtualHost={_settings.VirtualHost}";
        _bus = RabbitHutch.CreateBus(connectionString);

        _fallbackDir = Path.Combine(AppContext.BaseDirectory, "FailedMessages");
        Directory.CreateDirectory(_fallbackDir);
    }

    public async Task<PublishReceipt> PublishAsync(
        string queueName,
        JsonNode payload,
        IDictionary<string, object?>? headers = null,
        CancellationToken ct = default)
    {
        var messageId = Guid.NewGuid().ToString("D");
        var payloadString = payload.ToJsonString();
        var schemaName = headers?.ContainsKey("x-schema-name") == true ? headers["x-schema-name"]?.ToString() ?? "unknown" : "unknown";

        var policy = ResiliencePolicies.CreateRetryPolicy(_logger, $"RabbitMQ Publish to {queueName}");

        try
        {
            await policy.ExecuteAsync(async () =>
            {
                // Convert JsonNode to a string and wrap in a POCO to ensure correct serialization by EasyNetQ
                var message = new JsonMessage(payloadString, schemaName);
                await _bus.PubSub.PublishAsync(message, x => x.WithTopic(queueName), ct);
            });

            _logger.LogInformation("Successfully published message {MessageId} to queue '{Queue}' via EasyNetQ.", messageId, queueName);

            return new PublishReceipt
            {
                MessageId = messageId,
                Queue = queueName,
                Exchange = _settings.ExchangeName,
                PublishedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish to RabbitMQ after retries. Falling back to DB storage.");

            // Resolve IFinalizedBillRepository within a scope to avoid captive dependency
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IFinalizedBillRepository>();

            // Fallback 1: Oracle DB failed_messages table
            try
            {
                await repository.AddFailedMessageAsync(messageId, payloadString, queueName, ex.Message);
                _logger.LogInformation("Message {MessageId} saved to failed_messages table.", messageId);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Failed to save to Oracle DB. Falling back to local filesystem.");

                // Fallback 2: Local filesystem
                var filePath = Path.Combine(_fallbackDir, $"{messageId}.json");
                await File.WriteAllTextAsync(filePath, payloadString, ct);
                _logger.LogWarning("Message {MessageId} saved to local fallback: {FilePath}", messageId, filePath);
            }

            return new PublishReceipt
            {
                MessageId = messageId,
                Queue = queueName,
                Exchange = "FALLBACK",
                PublishedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void Dispose()
    {
        (_bus as IDisposable)?.Dispose();
    }
}
