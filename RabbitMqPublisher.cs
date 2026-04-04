using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using RabbitSchemaApi.Models;

namespace RabbitSchemaApi.Services;

public interface IRabbitMqPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes a validated payload to the specified queue.
    /// Creates the queue if it does not already exist (idempotent declare).
    /// </summary>
    Task<PublishReceipt> PublishAsync(
        string queueName,
        object payload,
        IDictionary<string, object?>? headers = null,
        CancellationToken ct = default);
}

/// <summary>
/// Manages a single long-lived RabbitMQ connection + channel and exposes
/// a simple publish API. Uses the RabbitMQ.Client v7 async API.
/// 
/// The service is registered as a Singleton so the connection is shared
/// across all requests — exactly how the .NET client is designed to be used.
/// </summary>
public sealed class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly RabbitMqSettings _settings;
    private readonly ILogger<RabbitMqPublisher> _logger;

    // Lazily-initialized async connection and channel
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Tracks which queues we've already declared to avoid redundant declares
    private readonly HashSet<string> _declaredQueues = new(StringComparer.OrdinalIgnoreCase);

    public RabbitMqPublisher(IOptions<RabbitMqSettings> settings, ILogger<RabbitMqPublisher> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<PublishReceipt> PublishAsync(
        string queueName,
        object payload,
        IDictionary<string, object?>? headers = null,
        CancellationToken ct = default)
    {
        var channel = await GetChannelAsync(ct);

        // Idempotently ensure the queue exists (safe to call multiple times)
        await EnsureQueueDeclaredAsync(channel, queueName, ct);

        var messageId = Guid.NewGuid().ToString("D");
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        var body = Encoding.UTF8.GetBytes(json);

        // Build AMQP basic properties
        var props = new BasicProperties
        {
            MessageId   = messageId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent, // survive broker restart
            Timestamp   = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers     = BuildHeaders(headers)
        };

        // Publish to the default exchange with the queue name as the routing key.
        // For topic/fanout exchanges, update the exchange name and routing key here.
        await channel.BasicPublishAsync(
            exchange: _settings.ExchangeName,
            routingKey: queueName,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);

        _logger.LogInformation(
            "Published message {MessageId} to queue '{Queue}' ({Bytes} bytes)",
            messageId, queueName, body.Length);

        return new PublishReceipt
        {
            MessageId   = messageId,
            Queue       = queueName,
            Exchange    = _settings.ExchangeName,
            PublishedAt = DateTimeOffset.UtcNow
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Connection management — lazy, thread-safe, auto-reconnect capable
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        // Fast path — already initialised
        if (_channel is { IsOpen: true })
            return _channel;

        await _initLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock
            if (_channel is { IsOpen: true })
                return _channel;

            // Rebuild connection/channel (first call or after disconnect)
            _connection = await CreateConnectionAsync(ct);
            _channel    = await _connection.CreateChannelAsync(cancellationToken: ct);

            _logger.LogInformation("RabbitMQ connection established to {Host}:{Port}",
                _settings.HostName, _settings.Port);

            return _channel;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<IConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName    = _settings.HostName,
            Port        = _settings.Port,
            UserName    = _settings.UserName,
            Password    = _settings.Password,
            VirtualHost = _settings.VirtualHost,
            // Automatic recovery reconnects the client after network blips
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval  = TimeSpan.FromSeconds(10)
        };

        try
        {
            return await factory.CreateConnectionAsync(clientProvidedName: "RabbitSchemaApi", cancellationToken: ct);
        }
        catch (BrokerUnreachableException ex)
        {
            _logger.LogError(ex, "Cannot connect to RabbitMQ at {Host}:{Port}", _settings.HostName, _settings.Port);
            throw;
        }
    }

    private async Task EnsureQueueDeclaredAsync(IChannel channel, string queueName, CancellationToken ct)
    {
        if (_declaredQueues.Contains(queueName))
            return;

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,         // survives broker restart
            exclusive: false,      // shared across connections
            autoDelete: false,     // doesn't disappear when unused
            arguments: new Dictionary<string, object?>
            {
                // Dead-letter exchange — unprocessable messages go here
                ["x-dead-letter-exchange"] = _settings.DeadLetterExchange,
                // Optional TTL: messages expire after this many ms if not consumed
                // ["x-message-ttl"] = 86_400_000  // 24 h
            },
            cancellationToken: ct);

        _declaredQueues.Add(queueName);
        _logger.LogDebug("Queue '{Queue}' declared (durable=true, DLX='{DLX}')",
            queueName, _settings.DeadLetterExchange);
    }

    private static Dictionary<string, object?> BuildHeaders(IDictionary<string, object?>? extra)
    {
        var headers = new Dictionary<string, object?>
        {
            ["x-source"] = "RabbitSchemaApi",
            ["x-schema-validated"] = "true"
        };

        if (extra is not null)
            foreach (var (k, v) in extra)
                headers[k] = v;

        return headers;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Disposal
    // ──────────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _initLock.Dispose();
    }
}

/// <summary>Strongly-typed settings bound from appsettings.json "RabbitMQ" section.</summary>
public sealed class RabbitMqSettings
{
    public string HostName           { get; set; } = "localhost";
    public int    Port               { get; set; } = 5672;
    public string UserName           { get; set; } = "guest";
    public string Password           { get; set; } = "guest";
    public string VirtualHost        { get; set; } = "/";
    public string ExchangeName       { get; set; } = "";    // "" = default AMQP exchange
    public string DeadLetterExchange { get; set; } = "dlx";
}

/// <summary>Centralised JSON serialiser options.</summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
