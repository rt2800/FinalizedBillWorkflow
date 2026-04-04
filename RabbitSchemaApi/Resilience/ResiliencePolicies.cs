using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Retry;

namespace RabbitSchemaApi.Resilience;

public static class ResiliencePolicies
{
    /// <summary>
    /// A standard retry policy: 3 retries with exponential backoff.
    /// Total of 4 attempts.
    /// </summary>
    public static AsyncRetryPolicy CreateRetryPolicy(ILogger logger, string operationName)
    {
        var delay = Backoff.ExponentialBackoff(TimeSpan.FromSeconds(1), retryCount: 3);

        return Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                delay,
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    logger.LogWarning(exception,
                        "Retry {RetryCount} for '{Operation}' after {Delay}ms due to {ErrorMessage}",
                        retryCount, operationName, timeSpan.TotalMilliseconds, exception.Message);
                });
    }
}
