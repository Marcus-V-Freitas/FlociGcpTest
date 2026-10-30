using FlociGcpTest.ApiService;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using NSubstitute;

namespace Floci.Hosting.Gcp.Tests;

public sealed class PubSubPlaygroundServiceFailureTests
{
    [Fact]
    public async Task InfrastructureFailureRemainsAnException()
    {
        // Arrange
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        var expectedFailure = new RpcException(new(StatusCode.Unavailable, "offline"));
        publisher.PublishAsync(Arg.Any<TopicName>(), Arg.Any<IEnumerable<PubsubMessage>>(), Arg.Any<CallSettings>())
            .Returns(Task.FromException<PublishResponse>(expectedFailure));
        var service = new PubSubPlaygroundService(publisher, subscriber, new("floci-local"));

        // Act
        var error = await Record.ExceptionAsync(() => service.PublishAsync("message", CancellationToken.None));

        // Assert
        error.Should().BeOfType<RpcException>().Which.Should().BeSameAs(expectedFailure);
    }
}
