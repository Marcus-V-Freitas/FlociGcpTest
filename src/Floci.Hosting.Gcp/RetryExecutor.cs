using Google;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Net;

namespace Floci.Hosting.Gcp;

internal sealed class RetryExecutor
{
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger _logger;

    public RetryExecutor(
        ILogger logger,
        int attempts = 7,
        TimeSpan? delay = null)
    {
        _logger = logger;

        var builder = new ResiliencePipelineBuilder();

        if (attempts > 1)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = attempts - 1,
                Delay = delay ?? TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                OnRetry = args =>
                {
                    ProvisioningLog.Retrying(logger, args.Outcome.Exception,
                        args.AttemptNumber + 1, attempts - 1, args.RetryDelay);

                    return ValueTask.CompletedTask;
                }
            });
        }

        _pipeline = builder.Build();
    }

    public async Task ExecuteAsync(string operation, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["Operation"] = operation });

        await _pipeline.ExecuteAsync(async token => await action(token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    internal static bool IsTransient(Exception exception) => exception switch
    {
        GoogleApiException api => IsTransientStatus(api.HttpStatusCode),
        HttpRequestException http => http.StatusCode is null || IsTransientStatus(http.StatusCode.Value),
        _ => false
    };

    private static bool IsTransientStatus(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
