using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using RabbitSchemaApi.Models;

namespace RabbitSchemaApi.Services;

public interface ISftpService
{
    /// <summary>
    /// Uploads the JSON payload to the SFTP server.
    /// </summary>
    /// <param name="payload">The JSON node to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UploadAsync(JsonNode payload, CancellationToken ct = default);
}

public sealed class SftpService : ISftpService
{
    private readonly SftpSettings _settings;
    private readonly ILogger<SftpService> _logger;

    public SftpService(IOptions<SftpSettings> settings, ILogger<SftpService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task UploadAsync(JsonNode payload, CancellationToken ct = default)
    {
        try
        {
            // Filename requirement: FB_ prefix then timestamp (local time)
            // Added milliseconds and a short GUID for uniqueness in high-throughput scenarios.
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            var uniqueId = Guid.NewGuid().ToString("N")[..6];
            var fileName = $"FB_{timestamp}_{uniqueId}.json";

            var remoteFilePath = string.IsNullOrWhiteSpace(_settings.RemotePath)
                ? fileName
                : $"{_settings.RemotePath.TrimEnd('/')}/{fileName}";

            var jsonString = payload.ToJsonString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);

            using var client = new SftpClient(_settings.Host, _settings.Port, _settings.UserName, _settings.Password);

            _logger.LogDebug("Connecting to SFTP server {Host}:{Port}...", _settings.Host, _settings.Port);

            // SSH.NET is largely synchronous; wrapping in Task.Run.
            // Note: We avoid passing 'ct' directly to Task.Run if it's the request-scoped token,
            // as that would cancel the task before it finishes if the response is sent.
            // However, since we'll be awaiting this in the controller, it's safer.
            await Task.Run(() =>
            {
                client.Connect();
                using var ms = new MemoryStream(bytes);
                client.UploadFile(ms, remoteFilePath);
                client.Disconnect();
            }, ct);

            _logger.LogInformation("Successfully uploaded payload to SFTP as {FilePath}", remoteFilePath);
        }
        catch (Exception ex)
        {
            // Requirement: "It should log failure; should not return error to API client"
            _logger.LogError(ex, "Failed to upload payload to SFTP server.");
        }
    }
}
