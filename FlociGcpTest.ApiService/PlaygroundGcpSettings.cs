namespace FlociGcpTest.ApiService;

public sealed record PlaygroundGcpSettings(string ProjectId)
{
    public string TopicId => "test-topic";
    public string SubscriptionId => "test-subscription";
    public string BucketName => "test-bucket";

    public static PlaygroundGcpSettings FromConfiguration(IConfiguration configuration) => new(
        configuration["GOOGLE_CLOUD_PROJECT"]
        ?? throw new InvalidOperationException("Run this playground through its Aspire AppHost (GOOGLE_CLOUD_PROJECT is required)."));
}
