using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Net;
using Encoding = System.Text.Encoding;

namespace Floci.Hosting.Gcp;

internal sealed class PubSubProvisioner(HttpClient client, RetryExecutor retry)
{
    public async Task ProvisionAsync(ProvisioningResource state,
        IReadOnlyDictionary<(string, string), string> destinations, CancellationToken ct)
    {
        var topics = new HashSet<(string, string)>();

        foreach (var config in state.PubSubConfigs)
        {
            foreach (var message in config.MessageConfigs)
            {
                if (topics.Add((config.ProjectId, message.TopicName)))
                    await CreateTopicAsync(config.ProjectId, message.TopicName, ct).ConfigureAwait(false);

                if (message.DeadLetterTopic is { } dlq && topics.Add((config.ProjectId, dlq)))
                    await CreateTopicAsync(config.ProjectId, dlq, ct).ConfigureAwait(false);
            }
        }

        var deadLetters = new HashSet<(string, string)>();

        foreach (var config in state.PubSubConfigs)
        {
            foreach (var message in config.MessageConfigs)
            {
                if (message.SubscriptionName is not { } name)
                    continue;

                if (message.DeadLetterTopic is { } dlq && deadLetters.Add((config.ProjectId, dlq)))
                {
                    await CreateSubscriptionAsync(new()
                    {
                        SubscriptionName = SubscriptionName.FromProjectSubscription(config.ProjectId, dlq + "-sub"),
                        TopicAsTopicName = TopicName.FromProjectTopic(config.ProjectId, dlq),
                        AckDeadlineSeconds = 60
                    }, ct).ConfigureAwait(false);
                }

                destinations.TryGetValue((config.ProjectId, name), out var destination);

                await CreateSubscriptionAsync(new()
                {
                    SubscriptionName = SubscriptionName.FromProjectSubscription(config.ProjectId, name),
                    TopicAsTopicName = TopicName.FromProjectTopic(config.ProjectId, message.TopicName),
                    AckDeadlineSeconds = message.AckDeadlineSeconds,
                    PushConfig = new() { PushEndpoint = destination ?? message.PushEndpoint?.AbsoluteUri ?? "" },
                    DeadLetterPolicy = message.DeadLetterTopic is { } topic ? new()
                    {
                        DeadLetterTopic = TopicName.FromProjectTopic(config.ProjectId, topic).ToString(),
                        MaxDeliveryAttempts = message.MaxDeliveryAttempts
                    } : null,
                    RetryPolicy = new()
                    {
                        MinimumBackoff = Duration.FromTimeSpan(message.MinimumRetryBackoff),
                        MaximumBackoff = Duration.FromTimeSpan(message.MaximumRetryBackoff)
                    }
                }, ct).ConfigureAwait(false);
            }
        }
    }

    private Task CreateTopicAsync(string project, string topic, CancellationToken ct) =>
        CreateAsync(TopicName.FromProjectTopic(project, topic).ToString(), "{}", ct);

    private Task CreateSubscriptionAsync(Subscription subscription, CancellationToken ct) =>
        CreateAsync(subscription.Name, JsonFormatter.Default.Format(subscription), ct);

    private Task CreateAsync(string name, string json, CancellationToken ct) =>
        retry.ExecuteAsync(name, async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"v1/{name}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Conflict)
                response.EnsureSuccessStatusCode();
        }, ct);
}
