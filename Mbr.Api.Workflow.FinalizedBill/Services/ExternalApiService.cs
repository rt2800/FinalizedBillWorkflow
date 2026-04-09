using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Mbr.Api.Workflow.FinalizedBill.Models;
using Mbr.Api.Workflow.FinalizedBill.Repositories;
using Mbr.Api.Workflow.FinalizedBill.Resilience;

namespace Mbr.Api.Workflow.FinalizedBill.Services;

public interface IExternalApiService
{
    Task PostToClientAsync(string payload, string? micBillId, string? correlationId, CancellationToken ct = default);
    Task PostToBemAsync(string payload, string? micBillId, string? correlationId, CancellationToken ct = default);
}

public sealed class ExternalApiService : IExternalApiService
{
    private readonly HttpClient _httpClient;
    private readonly ExternalEndpointsSettings _settings;
    private readonly IFinalizedBillRepository _repository;
    private readonly ILogger<ExternalApiService> _logger;

    public ExternalApiService(
        HttpClient httpClient,
        IOptions<ExternalEndpointsSettings> settings,
        IFinalizedBillRepository repository,
        ILogger<ExternalApiService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _repository = repository;
        _logger = logger;
    }

    public async Task PostToClientAsync(string payload, string? micBillId, string? correlationId, CancellationToken ct = default)
    {
        await SendRequestAsync(
            url: _settings.ClientEndpointUrl,
            payload: payload,
            token: null,
            micBillId: micBillId,
            correlationId: correlationId,
            context: "Client Endpoint POST",
            ct: ct);
    }

    public async Task PostToBemAsync(string payload, string? micBillId, string? correlationId, CancellationToken ct = default)
    {
        await SendRequestAsync(
            url: _settings.BemEndpointUrl,
            payload: payload,
            token: _settings.BemServiceToken,
            micBillId: micBillId,
            correlationId: correlationId,
            context: "BEM Endpoint POST",
            ct: ct);
    }

    private async Task SendRequestAsync(
        string url,
        string payload,
        string? token,
        string? micBillId,
        string? correlationId,
        string context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Skipping {Context} because URL is not configured.", context);
            return;
        }

        var policy = ResiliencePolicies.CreateRetryPolicy(_logger, context);

        try
        {
            await policy.ExecuteAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                if (!string.IsNullOrEmpty(correlationId))
                {
                    request.Headers.Add("x-correlation-id", correlationId);
                }

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
            });

            _logger.LogInformation("Successfully sent POST to {Url} (MicBillId: {MicBillId})", url, micBillId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send POST to {Url} after retries.", url);
            await _repository.AddExceptionLogAsync(ex, micBillId, correlationId, context);
            throw; // Re-throw so background processor/caller knows it failed
        }
    }
}
