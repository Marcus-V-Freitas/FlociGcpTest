using Aspire.Hosting.ApplicationModel;

namespace Floci.Hosting.Gcp;

internal sealed record PushDestination(
    string ProjectId,
    string SubscriptionName,
    EndpointReference Endpoint,
    string Path);
