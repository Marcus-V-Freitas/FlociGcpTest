namespace Floci.Hosting.Gcp;

public sealed record BucketConfig(
    string Name, 
    string? ProjectId = null);
