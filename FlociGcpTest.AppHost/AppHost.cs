using Floci.Hosting.Gcp;

var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.FlociGcpTest_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

var gcp = builder.AddFlociGcpEnvironment("gcp")
    .WithBuckets("test-bucket")
    .WithBucketFolder("../bucket-data")
    .WithPubSubConfigs(
        new PubSubConfig("floci-local",
            new MessageConfig(
                TopicName: "test-topic",
                SubscriptionName: "test-push-subscription")
            {
                DeadLetterTopic = "test-topic-dlq",
                MaxDeliveryAttempts = 5,
                AckDeadlineSeconds = 60
            },
            new MessageConfig("test-topic", "test-subscription")
        )
    )
    .WithPushEndpoint("test-push-subscription", apiService.GetEndpoint("http"), "/pubsub/push")
    .WithFlociUI(ui => ui.WithHostPort(14500), containerName: "my-floci-ui");

apiService.WithReference(gcp);
await builder.Build().RunAsync();
