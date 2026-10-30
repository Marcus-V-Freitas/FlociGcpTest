namespace Floci.Hosting.Gcp;

public sealed record MessageConfig(string TopicName, string? SubscriptionName = null)
{
    public Uri? PushEndpoint { get; init; }

    public string? DeadLetterTopic { get; init; }

    public int MaxDeliveryAttempts { get; init; } = 5;

    public int AckDeadlineSeconds { get; init; } = 60;

    public TimeSpan MinimumRetryBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumRetryBackoff { get; init; } = TimeSpan.FromSeconds(10);
}