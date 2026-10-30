namespace Floci.Hosting.Gcp.Tests;

internal sealed class TemporaryTestDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("floci-gcp-");

    public string FullName => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
