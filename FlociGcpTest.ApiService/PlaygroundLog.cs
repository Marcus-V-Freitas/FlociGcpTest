namespace FlociGcpTest.ApiService;

internal static partial class PlaygroundLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Pub/Sub push received: {MessageId} from {Subscription}")]
    public static partial void PushReceived(ILogger logger, string messageId, string subscription);
}
