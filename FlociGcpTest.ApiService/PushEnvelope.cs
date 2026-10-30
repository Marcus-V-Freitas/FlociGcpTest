namespace FlociGcpTest.ApiService;

public sealed record PushEnvelope(PushMessage? Message, string? Subscription);
