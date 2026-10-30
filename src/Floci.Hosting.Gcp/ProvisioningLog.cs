using Microsoft.Extensions.Logging;

namespace Floci.Hosting.Gcp;

internal static partial class ProvisioningLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient GCP failure; retry {Attempt}/{Attempts} in {Delay}")]
    public static partial void Retrying(this ILogger logger, Exception? exception, int attempt, int attempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "GCP provisioning failed for {Emulator}. Consumers will not start.")]
    public static partial void Failed(this ILogger logger, Exception exception, string emulator);
}
