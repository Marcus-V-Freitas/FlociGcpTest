using FlociGcpTest.ApiService;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using NSubstitute;

namespace Floci.Hosting.Gcp.Tests;

public sealed class PubSubPlaygroundServiceTests
{
    [Fact]
    public async Task PublishesOriginalPayloadWithRequestCancellation()
    {
        // Arrange
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        publisher.PublishAsync(Arg.Any<TopicName>(), Arg.Any<IEnumerable<PubsubMessage>>(), Arg.Any<CallSettings>())
            .Returns(new PublishResponse { MessageIds = { "published-id" } });
        var service = new PubSubPlaygroundService(publisher, subscriber, new("floci-local"));
        using var cancellation = new CancellationTokenSource();

        // Act
        var result = await service.PublishAsync("Olá", cancellation.Token);

        // Assert
        result.MessageId.Should().Be("published-id");
        await publisher.Received(1).PublishAsync(TopicName.FromProjectTopic("floci-local", "test-topic"),
            Arg.Is<IEnumerable<PubsubMessage>>(messages => messages.Single().Data.ToStringUtf8() == "Olá"),
            Arg.Is<CallSettings>(settings => settings.CancellationToken == cancellation.Token));
    }

    [Fact]
    public async Task PullAcknowledgesOnlyReceivedMessages()
    {
        // Arrange
        var publisher = Substitute.For<PublisherServiceApiClient>();
        string[] expectedAckIds = ["ack-one", "ack-two"];
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        using var cancellation = new CancellationTokenSource();
        var subscription = SubscriptionName.FromProjectSubscription("floci-local", "test-subscription");
        subscriber.PullAsync(subscription, 10, cancellation.Token).Returns(new PullResponse
        {
            ReceivedMessages =
            {
                new ReceivedMessage { AckId = "ack-one", Message = new() { Data = ByteString.CopyFromUtf8("Olá") } },
                new ReceivedMessage { AckId = "ack-two", Message = new() { Data = ByteString.CopyFromUtf8("café, ☁") } }
            }
        });
        var service = new PubSubPlaygroundService(publisher, subscriber, new("floci-local"));

        // Act
        var messages = await service.PullAsync(cancellation.Token);

        // Assert
        messages.Should().Equal("Olá", "café, ☁");
        await subscriber.Received(1).AcknowledgeAsync(subscription,
            Arg.Is<IEnumerable<string>>(ids => ids.SequenceEqual(expectedAckIds)), cancellation.Token);
    }

    [Fact]
    public async Task EmptyPullDoesNotAcknowledgeAnything()
    {
        // Arrange
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), 10, Arg.Any<CancellationToken>()).Returns(new PullResponse());
        var service = new PubSubPlaygroundService(Substitute.For<PublisherServiceApiClient>(), subscriber, new("floci-local"));

        // Act
        var messages = await service.PullAsync(CancellationToken.None);

        // Assert
        messages.Should().BeEmpty();
        await subscriber.DidNotReceive().AcknowledgeAsync(Arg.Any<SubscriptionName>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}
