using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Resilience;
using Polly;

namespace Mbr.Api.Workflow.FinalizedBill.Services;

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
    private readonly string _fallbackDir;

    public SftpService(IOptions<SftpSettings> settings, ILogger<SftpService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _fallbackDir = Path.Combine(AppContext.BaseDirectory, "SftpFallback");
        Directory.CreateDirectory(_fallbackDir);
    }

    public async Task UploadAsync(JsonNode payload, CancellationToken ct = default)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var uniqueId = Guid.NewGuid().ToString("N")[..6];
        var fileName = $"FB_{timestamp}_{uniqueId}.json";

        var remoteFilePath = string.IsNullOrWhiteSpace(_settings.RemotePath)
            ? fileName
            : $"{_settings.RemotePath.TrimEnd('/')}/{fileName}";

        var jsonString = payload.ToJsonString();
        var bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);

        var policy = ResiliencePolicies.CreateRetryPolicy(_logger, $"SFTP Upload to {remoteFilePath}");

        try
        {
            await policy.ExecuteAsync(async () =>
            {
                using var client = new SftpClient(_settings.Host, _settings.Port, _settings.UserName, _settings.Password);

                _logger.LogDebug("Connecting to SFTP server {Host}:{Port}...", _settings.Host, _settings.Port);

                await Task.Run(() =>
                {
                    client.Connect();
                    using var ms = new MemoryStream(bytes);
                    client.UploadFile(ms, remoteFilePath);
                    client.Disconnect();
                }, ct);
            });

            _logger.LogInformation("Successfully uploaded payload to SFTP as {FilePath}", remoteFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload payload to SFTP server after retries. Falling back to local filesystem.");

            // Fallback: Local filesystem
            try
            {
                var fallbackPath = Path.Combine(_fallbackDir, fileName);
                await File.WriteAllBytesAsync(fallbackPath, bytes, ct);
                _logger.LogWarning("Payload saved to SFTP fallback: {FilePath}", fallbackPath);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogCritical(fallbackEx, "Failed to save payload to SFTP local fallback.");
            }
        }
    }
}
