using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;

namespace Floci.Hosting.Gcp;

internal static class GcpProvisioner
{
    public static async Task ProvisionAsync(ProvisioningResource state, ResourceNotificationService notifications,
        ResourceLoggerService loggers, DistributedApplicationExecutionContext execution, CancellationToken stoppingToken)
    {
        var logger = loggers.GetLogger(state);

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        startup.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await UpdateStateAsync(notifications, state, KnownResourceStates.Running).ConfigureAwait(false);

            var destinations = await ResolveDestinationsAsync(state, execution, startup.Token).ConfigureAwait(false);

            await ProvisionServicesAsync(state, destinations, logger, startup.Token).ConfigureAwait(false);

            await UpdateStateAsync(notifications, state, KnownResourceStates.Finished, 0).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await UpdateStateAsync(notifications, state, KnownResourceStates.Exited, 1).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.Failed(exception, state.Emulator.Name);
            await UpdateStateAsync(notifications, state, KnownResourceStates.Exited, 1).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Dictionary<(string, string), string>> ResolveDestinationsAsync(ProvisioningResource state,
        DistributedApplicationExecutionContext execution, CancellationToken ct)
    {
        var context = new ValueProviderContext
        {
            ExecutionContext = execution,
            Caller = state.Emulator,
            Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
        };
        var destinations = new Dictionary<(string, string), string>();

        foreach (var destination in state.PushDestinations)
        {
            var baseUrl = await destination.Endpoint.GetValueAsync(context, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Push endpoint was not allocated.");

            var uri = new Uri(new Uri(baseUrl), destination.Path);

            destinations.Add((destination.ProjectId, destination.SubscriptionName), uri.AbsoluteUri);
        }

        return destinations;
    }

    private static async Task ProvisionServicesAsync(ProvisioningResource state,
        IReadOnlyDictionary<(string, string), string> destinations, ILogger logger, CancellationToken ct)
    {
        var endpoint = state.Emulator.PrimaryEndpoint;
        var hostPort = $"{endpoint.Host}:{endpoint.Port}";

        using var pubsub = new HttpClient { BaseAddress = new Uri($"{endpoint.Scheme}://{hostPort}/") };

        using var storage = await new StorageClientBuilder
        {
            BaseUri = $"{endpoint.Scheme}://{hostPort}/storage/v1/",
            UnauthenticatedAccess = true
        }.BuildAsync(ct).ConfigureAwait(false);

        var retry = new RetryExecutor(logger);

        await Task.WhenAll(
            new PubSubProvisioner(pubsub, retry).ProvisionAsync(state, destinations, ct),
            StorageProvisioner.ProvisionAsync(storage, state, retry, ct)).ConfigureAwait(false);
    }

    private static Task UpdateStateAsync(ResourceNotificationService notifications, ProvisioningResource state,
        string status, int? exitCode = null) => notifications.PublishUpdateAsync(state,
            snapshot => snapshot with { State = new(status, null), ExitCode = exitCode });
}
