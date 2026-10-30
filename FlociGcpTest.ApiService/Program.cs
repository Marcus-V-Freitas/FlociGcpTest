using FlociGcpTest.ApiService;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Cloud.Storage.V1;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(PlaygroundGcpSettings.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(_ => new StorageClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build());
builder.Services.AddSingleton(_ => new PublisherServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build());
builder.Services.AddSingleton(_ => new SubscriberServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build());
builder.Services.AddSingleton<PubSubPlaygroundService>();
builder.Services.AddSingleton<ConcurrentQueue<ReceivedPush>>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapStorageEndpoints();
app.MapPubSubEndpoints();

await app.RunAsync();

public partial class Program;
