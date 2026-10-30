using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FlociGcpTest.ApiService;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Floci.Hosting.Gcp.Tests;

[Trait("Category", "Integration")]
public sealed class PlaygroundTests : IAsyncLifetime
{
    private DistributedApplication _app = null!;
    private IDistributedApplicationTestingBuilder _builder = null!;
    private HttpClient _api = null!;
    private HttpClient _pubsub = null!;
    private HttpClient _secondApi = null!;
    private SubscriberServiceApiClient _subscriber = null!;
    private PublisherServiceApiClient _publisher = null!;
    private ProvisioningResource _state = null!;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FlociGcpTest_AppHost>(
            timeout.Token);
        _state = _builder.Resources.OfType<ProvisioningResource>().Single();
        var gcp = ConfigureResources();

        _app = await _builder.BuildAsync(timeout.Token);
        await _app.StartAsync(timeout.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("apiservice", timeout.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("second-api", timeout.Token);

        _api = _app.CreateHttpClient("apiservice", "http");
        _secondApi = _app.CreateHttpClient("second-api", "http");
        await CreateGcpClientsAsync(gcp.Resource, timeout.Token);
    }

    [Fact]
    public async Task RealPublishReachesTwoExplicitDestinationsAndPullSubscription()
    {
        // Arrange
        var payload = $"push-{Guid.NewGuid():N}";

        // Act
        using var publishResponse = await _api.PostAsJsonAsync("/pubsub/publish", payload);
        var published = await publishResponse.Content.ReadFromJsonAsync<PublishedMessage>();
        var firstReceipt = await WaitForReceiptAsync(_api, published!.MessageId);
        var secondReceipt = await WaitForReceiptAsync(_secondApi, published.MessageId);
        using var pullResponse = await _api.GetAsync("/pubsub/pull");
        var pulledMessages = await pullResponse.Content.ReadFromJsonAsync<string[]>();

        // Assert
        publishResponse.IsSuccessStatusCode.Should().BeTrue();
        firstReceipt.Data.Should().Be(payload);
        secondReceipt.Data.Should().Be(payload);
        firstReceipt.Subscription.Should().EndWith("/test-push-subscription");
        secondReceipt.Subscription.Should().EndWith("/second-push-subscription");
        pullResponse.IsSuccessStatusCode.Should().BeTrue();
        pulledMessages.Should().Contain(payload);
    }

    [Fact]
    public async Task StorageRoundTripsAndDeletesUnicodeContent()
    {
        // Arrange
        var fileName = $"integration-{Guid.NewGuid():N}.txt";
        const string expectedContent = "Conteúdo de teste: ação, café, ☁";

        // Act
        using var uploadResponse = await _api.PostAsJsonAsync($"/storage/upload?fileName={fileName}", expectedContent);
        var filesAfterUpload = await _api.GetFromJsonAsync<string[]>("/storage/list");
        var downloadedContent = await _api.GetStringAsync($"/storage/download?fileName={fileName}");
        using var deleteResponse = await _api.DeleteAsync($"/storage/object?fileName={fileName}");
        var filesAfterDelete = await _api.GetFromJsonAsync<string[]>("/storage/list");

        // Assert
        uploadResponse.IsSuccessStatusCode.Should().BeTrue();
        filesAfterUpload.Should().Contain(fileName);
        downloadedContent.Should().Be(expectedContent);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        filesAfterDelete.Should().NotContain(fileName);
    }

    [Fact]
    public async Task ExistingSubscriptionIsPreservedWithPendingMessages()
    {
        // Arrange
        var subscriptionName = SubscriptionName.FromProjectSubscription("floci-local", "test-subscription");
        var originalSubscription = await _subscriber.GetSubscriptionAsync(subscriptionName);
        var pendingPayload = "pending-" + Guid.NewGuid().ToString("N");
        await _publisher.PublishAsync(TopicName.FromProjectTopic("floci-local", "test-topic"),
            [new PubsubMessage { Data = ByteString.CopyFromUtf8(pendingPayload) }]);

        var changedConfiguration = new ProvisioningResource("existing-init", _state.Emulator, "floci-local");
        changedConfiguration.PubSubConfigs.Add(new("floci-local",
            new MessageConfig("test-topic", "test-subscription") { AckDeadlineSeconds = 30 }));
        var provisioner = new PubSubProvisioner(_pubsub, new RetryExecutor(NullLogger.Instance));
        var destinations = new Dictionary<(string, string), string>();

        // Act
        await provisioner.ProvisionAsync(changedConfiguration, destinations, CancellationToken.None);
        var currentSubscription = await _subscriber.GetSubscriptionAsync(subscriptionName);
        var pendingMessages = await _subscriber.PullAsync(subscriptionName, 10);

        // Assert
        currentSubscription.Should().BeEquivalentTo(originalSubscription);
        pendingMessages.ReceivedMessages.Should().Contain(message => message.Message.Data.ToStringUtf8() == pendingPayload);
    }

    [Fact]
    public async Task SubscriptionIsCreatedWithAllDeclaredSettings()
    {
        // Arrange
        const string subscriptionPath = "v1/projects/floci-local/subscriptions/test-push-subscription";

        // Act
        var json = await _pubsub.GetStringAsync(subscriptionPath);
        var subscription = JsonParser.Default.Parse<Subscription>(json);

        // Assert
        subscription.PushConfig.Should().NotBeNull();
        subscription.Topic.Should().Be("projects/floci-local/topics/test-topic");
        subscription.PushConfig.PushEndpoint.Should().EndWith("/pubsub/push");
        subscription.AckDeadlineSeconds.Should().Be(60);
        subscription.DeadLetterPolicy.DeadLetterTopic.Should().Be("projects/floci-local/topics/test-topic-dlq");
        subscription.DeadLetterPolicy.MaxDeliveryAttempts.Should().Be(5);
        subscription.RetryPolicy.MinimumBackoff.ToTimeSpan().Should().Be(TimeSpan.FromSeconds(1));
        subscription.RetryPolicy.MaximumBackoff.ToTimeSpan().Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MalformedPushReturnsBadRequest()
    {
        // Arrange
        var invalidEnvelope = new PushEnvelope(new("invalid-id", "!!!"), "test-subscription");

        // Act
        using var pushResponse = await _api.PostAsJsonAsync("/pubsub/push", invalidEnvelope);
        var pushError = await pushResponse.Content.ReadFromJsonAsync<string>();

        // Assert
        pushResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        pushError.Should().Be("Expected a Pub/Sub envelope with subscription, messageId and valid base64 data.");
    }

    [Fact]
    public async Task MissingBucketSourceFailsInitializationAndMarksResourceExited()
    {
        // Arrange
        _state.BucketFolder = Path.Combine(Path.GetTempPath(), "missing-floci-" + Guid.NewGuid().ToString("N"));
        var notifications = _app.ResourceNotifications;
        var loggers = _app.Services.GetRequiredService<ResourceLoggerService>();

        // Act
        var error = await Record.ExceptionAsync(() => GcpProvisioner.ProvisionAsync(
            _state, notifications, loggers, _builder.ExecutionContext, CancellationToken.None));
        var hasState = notifications.TryGetCurrentState(_state.Name, out var resourceEvent);

        // Assert
        error.Should().BeOfType<DirectoryNotFoundException>().Which.Message.Should().Contain(_state.BucketFolder);
        error!.StackTrace.Should().Contain(nameof(StorageProvisioner));
        hasState.Should().BeTrue();
        resourceEvent!.Snapshot.State!.Text.Should().Be(KnownResourceStates.Exited);
        resourceEvent.Snapshot.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task CanceledProvisioningMarksResourceExitedWithoutRethrowing()
    {
        // Arrange
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var notifications = _app.ResourceNotifications;
        var loggers = _app.Services.GetRequiredService<ResourceLoggerService>();

        // Act
        var error = await Record.ExceptionAsync(() => GcpProvisioner.ProvisionAsync(
            _state, notifications, loggers, _builder.ExecutionContext, stopping.Token));
        var hasState = notifications.TryGetCurrentState(_state.Name, out var resourceEvent);

        // Assert
        error.Should().BeNull();
        hasState.Should().BeTrue();
        resourceEvent!.Snapshot.State!.Text.Should().Be(KnownResourceStates.Exited);
        resourceEvent.Snapshot.ExitCode.Should().Be(1);
    }
    private IResourceBuilder<FlociGcpContainerResource> ConfigureResources()
    {
        _state.BucketFolder = null;

        foreach (var ui in _builder.Resources.OfType<FlociUIContainerResource>().ToArray())
            _builder.Resources.Remove(ui);

        var primaryApi = _builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "apiservice");
        _builder.CreateResourceBuilder(primaryApi).WithEndpoint("http", endpoint =>
        {
            endpoint.Port = null;
            endpoint.TargetPort = null;
        });

        var gcp = _builder.CreateResourceBuilder(_builder.Resources.OfType<FlociGcpContainerResource>().Single());
        var secondApi = _builder.AddProject<Projects.FlociGcpTest_ApiService>("second-api")
            .WithEndpoint("http", endpoint =>
            {
                endpoint.Port = null;
                endpoint.TargetPort = null;
            })
            .WithHttpHealthCheck("/health");
        secondApi.WithReference(gcp);

        // Declare after references to preserve the deferred configuration regression check.
        gcp.WithPubSubConfigs(new PubSubConfig("floci-local", new MessageConfig("test-topic", "second-push-subscription")))
            .WithPushEndpoint("second-push-subscription", secondApi.GetEndpoint("http"), "/pubsub/push");

        return gcp;
    }

    private async Task CreateGcpClientsAsync(FlociGcpContainerResource gcp, CancellationToken cancellationToken)
    {
        var endpoint = _app.GetEndpoint("gcp", gcp.PrimaryEndpoint.EndpointName);
        var host = $"{endpoint.Host}:{endpoint.Port}";
        _pubsub = new HttpClient { BaseAddress = endpoint };
        _subscriber = await new SubscriberServiceApiClientBuilder
        {
            Endpoint = host,
            ChannelCredentials = ChannelCredentials.Insecure
        }.BuildAsync(cancellationToken);
        _publisher = await new PublisherServiceApiClientBuilder
        {
            Endpoint = host,
            ChannelCredentials = ChannelCredentials.Insecure
        }.BuildAsync(cancellationToken);
    }

    private static async Task<ReceivedPush> WaitForReceiptAsync(HttpClient api, string messageId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        while (true)
        {
            var receipts = await api.GetFromJsonAsync<ReceivedPush[]>("/pubsub/received", timeout.Token);
            var receipt = receipts!.FirstOrDefault(receipt => receipt.MessageId == messageId);

            if (receipt is not null)
                return receipt;

            await Task.Delay(100, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();
        _pubsub?.Dispose();
        _secondApi?.Dispose();

        if (_app is not null)
            await _app.DisposeAsync();

        if (_builder is not null)
            await _builder.DisposeAsync();
    }
}
