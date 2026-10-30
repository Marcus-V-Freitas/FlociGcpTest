namespace Floci.Hosting.Gcp;

public sealed class PubSubConfig(string projectId, params MessageConfig[] messageConfigs)
{
    public string ProjectId { get; } = projectId;

    public IReadOnlyList<MessageConfig> MessageConfigs { get; } = Array.AsReadOnly(messageConfigs.ToArray());
}

