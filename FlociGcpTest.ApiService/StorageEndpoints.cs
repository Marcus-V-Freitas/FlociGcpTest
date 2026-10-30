using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace FlociGcpTest.ApiService;

public static class StorageEndpoints
{
    public static WebApplication MapStorageEndpoints(this WebApplication app)
    {
        app.MapPost("/storage/upload", UploadAsync);
        app.MapGet("/storage/list", ListAsync);
        app.MapGet("/storage/download", DownloadAsync);
        app.MapDelete("/storage/object", DeleteAsync);
        return app;
    }

    private static async Task<IResult> UploadAsync([FromQuery] string fileName, [FromQuery] string? bucketName, [FromBody] string content,
        StorageClient storage, PlaygroundGcpSettings settings, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var obj = await storage.UploadObjectAsync(bucketName ?? settings.BucketName, fileName, "text/plain", stream, cancellationToken: ct);
        return Results.Ok(new { obj.Name, obj.MediaLink });
    }

    private static async Task<IResult> ListAsync([FromQuery] string? bucketName, StorageClient storage, PlaygroundGcpSettings settings, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var obj in storage.ListObjectsAsync(bucketName ?? settings.BucketName).WithCancellation(ct))
            names.Add(obj.Name);
        return Results.Ok(names);
    }

    private static async Task<IResult> DownloadAsync([FromQuery] string fileName, [FromQuery] string? bucketName, StorageClient storage,
        PlaygroundGcpSettings settings, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await storage.DownloadObjectAsync(bucketName ?? settings.BucketName, fileName, stream, cancellationToken: ct);
        return Results.Text(Encoding.UTF8.GetString(stream.ToArray()), "text/plain");
    }

    private static async Task<IResult> DeleteAsync([FromQuery] string fileName, [FromQuery] string? bucketName, StorageClient storage,
        PlaygroundGcpSettings settings, CancellationToken ct)
    {
        await storage.DeleteObjectAsync(bucketName ?? settings.BucketName, fileName, cancellationToken: ct);
        return Results.NoContent();
    }
}
