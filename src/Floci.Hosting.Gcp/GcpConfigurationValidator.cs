using Aspire.Hosting.ApplicationModel;
using FluentValidation;

namespace Floci.Hosting.Gcp;

internal sealed class GcpConfigurationValidator : AbstractValidator<ProvisioningResource>
{
    public GcpConfigurationValidator()
    {
        var message = new InlineValidator<MessageConfig>();

        message.RuleFor(x => x).Must(x => x.PushEndpoint is null && x.DeadLetterTopic is null)
            .When(x => x.SubscriptionName is null)
            .WithMessage("Push and dead-letter configuration require a subscription.");

        message.When(x => x.SubscriptionName is not null, () =>
        {
            message.RuleFor(x => x.PushEndpoint)
                .Must(endpoint => endpoint is null || endpoint.IsAbsoluteUri && endpoint.Scheme is "http" or "https")
                .WithMessage("Push endpoint must be an absolute HTTP or HTTPS URL.");

            message.RuleFor(x => x.AckDeadlineSeconds).InclusiveBetween(10, 600);
            message.RuleFor(x => x.MaxDeliveryAttempts).InclusiveBetween(5, 100);
            message.RuleFor(x => x.MinimumRetryBackoff).GreaterThanOrEqualTo(TimeSpan.Zero);
            message.RuleFor(x => x.MaximumRetryBackoff)
                .GreaterThanOrEqualTo(x => x.MinimumRetryBackoff)
                .LessThanOrEqualTo(TimeSpan.FromSeconds(600));
        });

        RuleForEach(x => x.PubSubConfigs).ChildRules(config =>
            config.RuleForEach(x => x.MessageConfigs).SetValidator(message));

        RuleFor(x => x).Custom(ValidateRelationships);
    }

    private static void ValidateRelationships(ProvisioningResource state, ValidationContext<ProvisioningResource> context)
    {
        var mounts = 0;

        foreach (var annotation in state.Emulator.Annotations)
        {
            if (annotation is ContainerMountAnnotation { Target: "/app/data" } && ++mounts > 1)
            {
                context.AddFailure("Configure only one data mount for the emulator.");
                break;
            }
        }

        var buckets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var bucket in state.Buckets)
        {
            var project = bucket.ProjectId ?? state.DefaultProjectId;

            if (!buckets.TryAdd(bucket.Name, project) && buckets[bucket.Name] != project)
                context.AddFailure($"Bucket '{bucket.Name}' is declared in multiple projects.");
        }

        var subscriptions = new Dictionary<(string, string), MessageConfig>();

        foreach (var config in state.PubSubConfigs)
        {
            foreach (var message in config.MessageConfigs)
            {
                if (message.SubscriptionName is { } name && !subscriptions.TryAdd((config.ProjectId, name), message))
                    context.AddFailure($"Duplicate subscription '{config.ProjectId}/{name}'.");
            }
        }

        var destinations = new HashSet<(string, string)>();

        foreach (var destination in state.PushDestinations)
        {
            var key = (destination.ProjectId, destination.SubscriptionName);

            if (!destinations.Add(key))
                context.AddFailure($"Multiple push destinations for '{key}'.");

            if (!subscriptions.TryGetValue(key, out var subscription))
                context.AddFailure($"Push destination references undeclared subscription '{key}'.");
            else if (subscription.PushEndpoint is not null)
                context.AddFailure($"Subscription '{key}' has both an absolute URL and an Aspire destination.");

            if (destination.Endpoint.Scheme is not ("http" or "https"))
                context.AddFailure("A push destination must be an HTTP or HTTPS Aspire endpoint.");

            if (string.IsNullOrWhiteSpace(destination.Path) || !destination.Path.StartsWith('/')
                || destination.Path.StartsWith("//", StringComparison.Ordinal))
                context.AddFailure("Push path must start with a single '/'.");
        }
    }
}
