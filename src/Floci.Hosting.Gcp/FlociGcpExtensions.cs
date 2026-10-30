using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;

namespace Floci.Hosting.Gcp;

public static class FlociGcpExtensions
{
    public static IResourceBuilder<FlociGcpContainerResource> AddFlociGcpEnvironment(
        this IDistributedApplicationBuilder builder,
        string name,
        string projectId = "floci-local")
    {
        var emulator = builder.AddFlociGcp(name, defaultProjectId: projectId);

        RegisterInitialization(emulator, projectId);

        return emulator;
    }

    public static IResourceBuilder<FlociGcpContainerResource> WithBuckets(
        this IResourceBuilder<FlociGcpContainerResource> builder,
        params string[] names)
    {
        var buckets = GetState(builder).Buckets;

        foreach (var name in names)
            buckets.Add(new(name));

        return builder;
    }

    public static IResourceBuilder<FlociGcpContainerResource> WithBucketConfigs(
        this IResourceBuilder<FlociGcpContainerResource> builder,
        params BucketConfig[] buckets)
    {
        GetState(builder).Buckets.AddRange(buckets);

        return builder;
    }

    public static IResourceBuilder<FlociGcpContainerResource> WithBucketFolder(
        this IResourceBuilder<FlociGcpContainerResource> builder,
        string localBucketFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBucketFolder);

        var state = GetState(builder);
        var folder = Path.GetFullPath(localBucketFolder, builder.ApplicationBuilder.AppHostDirectory);

        if (state.BucketFolder is { } existing)
        {
            if (Path.GetRelativePath(existing, folder) == ".")
                return builder;

            throw new InvalidOperationException("The emulator already has a different bucket folder configured.");
        }

        state.BucketFolder = folder;

        return builder;
    }

    public static IResourceBuilder<FlociGcpContainerResource> WithPubSubConfigs(
        this IResourceBuilder<FlociGcpContainerResource> builder,
        params PubSubConfig[] configs)
    {
        GetState(builder).PubSubConfigs.AddRange(configs);

        return builder;
    }

    public static IResourceBuilder<FlociGcpContainerResource> WithPushEndpoint(
        this IResourceBuilder<FlociGcpContainerResource> builder,
        string subscriptionName,
        EndpointReference endpoint,
        string path,
        string? projectId = null)
    {
        var state = GetState(builder);
        var index = state.PushDestinations.Count;

        state.PushDestinations.Add(new(projectId ?? state.DefaultProjectId, subscriptionName, endpoint, path));

        builder.WithEnvironment(context => context.EnvironmentVariables[$"FLOCI_ASPIRE_PUSH_{index}"] = endpoint);

        return builder;
    }

    public static IResourceBuilder<ProjectResource> WithReference(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<FlociGcpContainerResource> emulator)
    {
        var state = GetState(emulator);

        FlociHostingExtension.WithReference(project, emulator);

        project.WithEnvironment(context => context.EnvironmentVariables["GCP_PROJECT_IDS"] = state.ProjectIds);

        return project.WaitForCompletion(emulator.ApplicationBuilder.CreateResourceBuilder(state));
    }

    private static ProvisioningResource GetState(IResourceBuilder<FlociGcpContainerResource> builder)
    {
        foreach (var resource in builder.ApplicationBuilder.Resources)
        {
            if (resource is ProvisioningResource state && ReferenceEquals(state.Emulator, builder.Resource))
                return state;
        }

        throw new InvalidOperationException("Use AddFlociGcpEnvironment before configuring resources.");
    }

    private static void RegisterInitialization(
        IResourceBuilder<FlociGcpContainerResource> emulator,
        string projectId)
    {
        var app = emulator.ApplicationBuilder;
        var state = new ProvisioningResource($"{emulator.Resource.Name}-init", emulator.Resource, projectId);

        app.AddResource(state)
            .WithParentRelationship(emulator)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "gcp-provisioner",
                Properties = [],
                State = new(KnownResourceStates.Waiting, null),
            });

        app.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            new GcpConfigurationValidator().ValidateAndThrow(state);

            if (state.BucketFolder is { } folder)
                Directory.CreateDirectory(folder);

            return Task.CompletedTask;
        });

        emulator.OnResourceReady((_, context, ct) => GcpProvisioner.ProvisionAsync(
            state,
            context.Services.GetRequiredService<ResourceNotificationService>(),
            context.Services.GetRequiredService<ResourceLoggerService>(),
            app.ExecutionContext,
            ct));
    }
}
