using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;

namespace FlociGcpTest.ApiService;

public sealed class PubSubPlaygroundService(PublisherServiceApiClient publisher,
    SubscriberServiceApiClient subscriber, PlaygroundGcpSettings settings)
{
    public async Task<PublishedMessage> PublishAsync(string message, CancellationToken ct)
    {
        var payload = new PubsubMessage { Data = ByteString.CopyFromUtf8(message) };
        var topic = TopicName.FromProjectTopic(settings.ProjectId, settings.TopicId);
        var response = await publisher.PublishAsync(topic, [payload],
            CallSettings.FromCancellationToken(ct).WithExpiration(Expiration.FromTimeout(TimeSpan.FromSeconds(30))));
        return new(response.MessageIds.Single());
    }

    public async Task<string[]> PullAsync(CancellationToken ct)
    {
        var subscription = SubscriptionName.FromProjectSubscription(settings.ProjectId, settings.SubscriptionId);
        var response = await subscriber.PullAsync(subscription, maxMessages: 10, cancellationToken: ct);
        if (response.ReceivedMessages.Count == 0)
            return [];

        var messages = new string[response.ReceivedMessages.Count];
        var ackIds = new string[messages.Length];
        for (var index = 0; index < messages.Length; index++)
        {
            var received = response.ReceivedMessages[index];
            messages[index] = received.Message.Data.ToStringUtf8();
            ackIds[index] = received.AckId;
        }

        await subscriber.AcknowledgeAsync(subscription, ackIds, ct);
        return messages;
    }
}
