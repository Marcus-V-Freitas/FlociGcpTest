using Aspire.Hosting.ApplicationModel;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;

namespace Floci.Hosting.Gcp.Tests;

public sealed class PubSubProvisionerTests
{
    [Fact]
    public async Task CreatesSubscriptionWithCompleteConfigurationInOneRequest()
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        var requests = new List<(string Path, HttpMethod Method, string Body)>();
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<HttpRequestMessage>();
                var body = await request.Content!.ReadAsStringAsync();
                requests.Add((request.RequestUri!.OriginalString, request.Method, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")
        {
            AckDeadlineSeconds = 30,
            DeadLetterTopic = "dlq",
            MaxDeliveryAttempts = 9,
            MinimumRetryBackoff = TimeSpan.FromSeconds(2),
            MaximumRetryBackoff = TimeSpan.FromSeconds(20)
        }));
        var destinations = new Dictionary<(string, string), string>
        {
            [("project", "subscription")] = "http://api:8080/push"
        };
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance));

        // Act
        await provisioner.ProvisionAsync(state, destinations, CancellationToken.None);

        // Assert
        requests.Should().OnlyContain(request => request.Method == HttpMethod.Put);
        var subscriptionRequest = requests.Should().ContainSingle(request => request.Path.EndsWith("/subscription")).Which;
        var subscription = JsonParser.Default.Parse<Subscription>(subscriptionRequest.Body);

        subscription.Topic.Should().Be("projects/project/topics/topic");
        subscription.PushConfig.PushEndpoint.Should().Be("http://api:8080/push");
        subscription.AckDeadlineSeconds.Should().Be(30);
        subscription.DeadLetterPolicy.DeadLetterTopic.Should().Be("projects/project/topics/dlq");
        subscription.DeadLetterPolicy.MaxDeliveryAttempts.Should().Be(9);
        subscription.RetryPolicy.MinimumBackoff.Seconds.Should().Be(2);
        subscription.RetryPolicy.MaximumBackoff.Seconds.Should().Be(20);
        await client.Received(4).SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingSubscriptionOnlyAttemptsCreationOnce()
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")));
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance));
        var destinations = new Dictionary<(string, string), string>();

        // Act
        await provisioner.ProvisionAsync(state, destinations, CancellationToken.None);

        // Assert
        await client.Received(2).SendAsync(
            Arg.Is<HttpRequestMessage>(request => request.Method == HttpMethod.Put), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("http://external/push", "http://external/push")]
    [InlineData("https://external/push", "https://external/push")]
    public async Task UsesDeclaredPushUrlOrCreatesPullSubscription(string? pushUrl, string expectedEndpoint)
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        var requests = new List<(string Path, string Body)>();
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<HttpRequestMessage>();
            requests.Add((request.RequestUri!.OriginalString, await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")
        {
            PushEndpoint = pushUrl is null ? null : new Uri(pushUrl)
        }));
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance));

        // Act
        await provisioner.ProvisionAsync(state, new Dictionary<(string, string), string>(), CancellationToken.None);

        // Assert
        var request = requests.Should().ContainSingle(request => request.Path.EndsWith("/subscription")).Which;
        var subscription = JsonParser.Default.Parse<Subscription>(request.Body);
        subscription.PushConfig.PushEndpoint.Should().Be(expectedEndpoint);
        subscription.DeadLetterPolicy.Should().BeNull();
    }

    [Fact]
    public async Task SharedTopicsAndDeadLetterSubscriptionsAreCreatedOnce()
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        var paths = new List<string>();
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            paths.Add(call.Arg<HttpRequestMessage>().RequestUri!.OriginalString);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project",
            new MessageConfig("topic"),
            new MessageConfig("topic", "first") { DeadLetterTopic = "dlq" },
            new MessageConfig("topic", "second") { DeadLetterTopic = "dlq" }));
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance));

        // Act
        await provisioner.ProvisionAsync(state, new Dictionary<(string, string), string>(), CancellationToken.None);

        // Assert
        paths.Should().BeEquivalentTo(
        [
            "v1/projects/project/topics/topic",
            "v1/projects/project/topics/dlq",
            "v1/projects/project/subscriptions/dlq-sub",
            "v1/projects/project/subscriptions/first",
            "v1/projects/project/subscriptions/second"
        ]);
    }

    [Fact]
    public async Task TransientHttpFailureRetriesCreationUntilSuccess()
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK]);
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HttpResponseMessage(responses.Dequeue()));
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")));
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance, attempts: 3, delay: TimeSpan.Zero));

        // Act
        await provisioner.ProvisionAsync(state, new Dictionary<(string, string), string>(), CancellationToken.None);

        // Assert
        responses.Should().BeEmpty();
        await client.Received(3).SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PermanentHttpFailureStopsWithoutRetryingOrCreatingSubscriptions()
    {
        // Arrange
        var client = Substitute.For<HttpClient>();
        client.SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")));
        var provisioner = new PubSubProvisioner(client, new RetryExecutor(NullLogger.Instance, attempts: 3, delay: TimeSpan.Zero));

        // Act
        var error = await Record.ExceptionAsync(() => provisioner.ProvisionAsync(
            state, new Dictionary<(string, string), string>(), CancellationToken.None));

        // Assert
        error.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await client.Received(1).SendAsync(
            Arg.Is<HttpRequestMessage>(request => request.RequestUri!.OriginalString == "v1/projects/project/topics/topic"),
            Arg.Any<CancellationToken>());
        await client.Received(1).SendAsync(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>());
    }
}
