using Aspire.Hosting.ApplicationModel;

namespace Floci.Hosting.Gcp;

internal sealed class ProvisioningResource(
    string name,
    FlociGcpContainerResource emulator,
    string projectId) : Resource(name)
{
    public FlociGcpContainerResource Emulator { get; } = emulator;

    public string DefaultProjectId { get; } = projectId;

    public List<BucketConfig> Buckets { get; } = [];
    
    public List<PubSubConfig> PubSubConfigs { get; } = [];
    
    public List<PushDestination> PushDestinations { get; } = [];

    public string? BucketFolder { get; set; }

    public string ProjectIds
    {
        get
        {
            var projects = new SortedSet<string>(StringComparer.Ordinal) { DefaultProjectId };

            foreach (var config in PubSubConfigs)
                projects.Add(config.ProjectId);

            foreach (var bucket in Buckets)
                projects.Add(bucket.ProjectId ?? DefaultProjectId);

            return string.Join(',', projects);
        }
    }
}