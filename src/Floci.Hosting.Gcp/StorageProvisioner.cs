using Google;
using Google.Cloud.Storage.V1;
using System.Net;

namespace Floci.Hosting.Gcp;

internal static class StorageProvisioner
{
    public static async Task ProvisionAsync(
        StorageClient client,
        ProvisioningResource state,
        RetryExecutor retry,
        CancellationToken ct)
    {
        var buckets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bucket in state.Buckets)
        {
            if (!buckets.Add(bucket.Name))
                continue;

            await retry.ExecuteAsync(
                $"bucket {bucket.Name}",
                token => EnsureBucketAsync(client, bucket, state.DefaultProjectId, token),
                ct).ConfigureAwait(false);
        }

        if (state.BucketFolder is not { } root)
            return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var bucketName = Path.GetFileName(folder);

            if (buckets.Add(bucketName))
            {
                await retry.ExecuteAsync($"bucket {bucketName}",
                    token => EnsureBucketAsync(client, new(bucketName), state.DefaultProjectId, token), ct)
                    .ConfigureAwait(false);
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var objectName = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

                await retry.ExecuteAsync($"object {bucketName}/{objectName}", async token =>
                {
                    using var stream = File.OpenRead(file);
                    await client.UploadObjectAsync(bucketName, objectName, "application/octet-stream", stream,
                        cancellationToken: token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task EnsureBucketAsync(
        StorageClient client,
        BucketConfig bucket,
        string defaultProjectId,
        CancellationToken ct)
    {
        try
        {
            await client.CreateBucketAsync(
                bucket.ProjectId ?? defaultProjectId,
                bucket.Name,
                new CreateBucketOptions { RetryOptions = RetryOptions.Never },
                ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Conflict)
        {
        }
    }
}
