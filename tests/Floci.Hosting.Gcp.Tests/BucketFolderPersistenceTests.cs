using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using System.Net.Http.Json;

namespace Floci.Hosting.Gcp.Tests;

[Trait("Category", "Integration")]
public sealed class BucketFolderPersistenceTests
{
    [Fact]
    public async Task UploadedObjectSurvivesRecreatingTheEnvironmentWithTheSameFolder()
    {
        // Arrange
        using var directory = new TemporaryTestDirectory();
        var folder = directory.FullName;
        const string fileName = "persisted.txt";
        const string expectedContent = "Arquivo persistido: ação, café, ☁";

        CopyStorageFixture(Path.Combine(folder, "buckets"));

        // Act
        var initialState = await RunWithEnvironmentAsync(folder, async api =>
        {
            var files = await api.GetFromJsonAsync<string[]>("/storage/list");
            var seededContent = await api.GetStringAsync("/storage/download?fileName=samples/local.txt");
            using var upload = await api.PostAsJsonAsync($"/storage/upload?fileName={fileName}", expectedContent);

            return (Files: files, SeededContent: seededContent, UploadSucceeded: upload.IsSuccessStatusCode);
        });
        var persistedFiles = Directory.GetFiles(Path.Combine(folder, "state"), "*", SearchOption.AllDirectories);
        var (Files, Content) = await RunWithEnvironmentAsync(folder, async api =>
        {
            var files = await api.GetFromJsonAsync<string[]>("/storage/list");
            var content = await api.GetStringAsync($"/storage/download?fileName={fileName}");

            return (Files: files, Content: content);
        });

        // Assert
        initialState.Files.Should().Contain("samples/local.txt");
        initialState.SeededContent.Should().Be("Arquivo local do Cloud Storage: ação, café, ☁.\n");
        initialState.UploadSucceeded.Should().BeTrue();
        persistedFiles.Should().NotBeEmpty();
        Files.Should().Contain(fileName);
        Content.Should().Be(expectedContent);
    }

    [Fact]
    public async Task LocalFoldersCreateBucketsAndReloadNestedFilesOnStartup()
    {
        // Arrange
        using var directory = new TemporaryTestDirectory();
        var folder = directory.FullName;
        var bucketRoot = Path.Combine(folder, "buckets");
        var localFolder = Path.Combine(bucketRoot, "veragi", "documentos");
        var localFile = Path.Combine(localFolder, "ação.json");
        const string initialContent = "{\"message\":\"Conteúdo original: ação, ☁\"}";
        const string updatedContent = "{\"message\":\"Arquivo local atualizado\"}";
        var objectPath = Uri.EscapeDataString("documentos/ação.json");
        var downloadPath = $"/storage/download?bucketName=veragi&fileName={objectPath}";

        Directory.CreateDirectory(localFolder);
        Directory.CreateDirectory(Path.Combine(bucketRoot, "empty-bucket"));
        await File.WriteAllTextAsync(localFile, initialContent);

        // Act
        var (Content, EmptyBucketFiles) = await RunWithEnvironmentAsync(folder, async api =>
        {
            var content = await api.GetStringAsync(downloadPath);
            var emptyBucketFiles = await api.GetFromJsonAsync<string[]>("/storage/list?bucketName=empty-bucket");

            return (Content: content, EmptyBucketFiles: emptyBucketFiles);
        });
        await File.WriteAllTextAsync(localFile, updatedContent);
        await File.WriteAllTextAsync(Path.Combine(localFolder, "novo.txt"), "Novo arquivo");
        var reloadedState = await RunWithEnvironmentAsync(folder, async api =>
        {
            var content = await api.GetStringAsync(downloadPath);
            var files = await api.GetFromJsonAsync<string[]>("/storage/list?bucketName=veragi");
            var addedContent = await api.GetStringAsync("/storage/download?bucketName=veragi&fileName=documentos/novo.txt");

            return (Content: content, Files: files, AddedContent: addedContent);
        });

        // Assert
        Content.Should().Be(initialContent);
        EmptyBucketFiles.Should().BeEmpty();
        reloadedState.Content.Should().Be(updatedContent);
        reloadedState.Files.Should().Contain("documentos/ação.json").And.Contain("documentos/novo.txt");
        reloadedState.AddedContent.Should().Be("Novo arquivo");
        Directory.GetFiles(bucketRoot).Should().BeEmpty();
        (await File.ReadAllTextAsync(localFile)).Should().Be(updatedContent);
    }

    private static void CopyStorageFixture(string folder)
    {
        Directory.CreateDirectory(folder);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");

        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(folder, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static async Task<TResult> RunWithEnvironmentAsync<TResult>(string folder, Func<HttpClient, Task<TResult>> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var bucketFolder = Path.Combine(folder, "buckets");
        var stateFolder = Path.Combine(folder, "state");
        Directory.CreateDirectory(stateFolder);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stateFolder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FlociGcpTest_AppHost>(
            timeout.Token);
        builder.Resources.OfType<ProvisioningResource>().Single().BucketFolder = bucketFolder;

        foreach (var ui in builder.Resources.OfType<FlociUIContainerResource>().ToArray())
            builder.Resources.Remove(ui);

        var gcp = builder.Resources.OfType<FlociGcpContainerResource>().Single();
        builder.CreateResourceBuilder(gcp).WithDataBindMount(stateFolder);
        var apiResource = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "apiservice");
        builder.CreateResourceBuilder(apiResource).WithEndpoint("http", endpoint =>
        {
            endpoint.Port = null;
            endpoint.TargetPort = null;
        });

        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("apiservice", timeout.Token);
        using var api = app.CreateHttpClient("apiservice", "http");

        return await action(api);
    }
}
