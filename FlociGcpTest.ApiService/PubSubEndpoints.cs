using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;

namespace FlociGcpTest.ApiService;

public static class PubSubEndpoints
{
    public static WebApplication MapPubSubEndpoints(this WebApplication app)
    {
        app.MapPost("/pubsub/publish", PublishAsync);
        app.MapGet("/pubsub/pull", async (PubSubPlaygroundService pubsub, CancellationToken ct) =>
            Results.Ok(await pubsub.PullAsync(ct)));
        app.MapGet("/pubsub/received", (ConcurrentQueue<ReceivedPush> messages) => Results.Ok(messages.ToArray()));
        app.MapPost("/pubsub/push", ReceivePush);
        return app;
    }

    private static async Task<IResult> PublishAsync([FromBody] string message, PubSubPlaygroundService pubsub, CancellationToken ct) =>
        Results.Ok(await pubsub.PublishAsync(message, ct));

    private static IResult ReceivePush(PushEnvelope envelope, ConcurrentQueue<ReceivedPush> messages, ILogger<ReceivedPush> logger)
    {
        if (string.IsNullOrWhiteSpace(envelope.Subscription) || envelope.Message is null
            || string.IsNullOrWhiteSpace(envelope.Message.MessageId) || envelope.Message.Data is null)
            return Results.BadRequest("Expected a Pub/Sub envelope with subscription, messageId and valid base64 data.");

        string data;
        try
        {
            data = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(envelope.Message.Data));
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            return Results.BadRequest("Expected a Pub/Sub envelope with subscription, messageId and valid base64 data.");
        }

        messages.Enqueue(new(envelope.Message.MessageId, envelope.Subscription, data));
        PlaygroundLog.PushReceived(logger, envelope.Message.MessageId, envelope.Subscription);
        return Results.NoContent();
    }
}
