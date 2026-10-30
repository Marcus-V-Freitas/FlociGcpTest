using FlociGcpTest.ApiService;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FluentValidation;

namespace Floci.Hosting.Gcp.Tests;

public sealed class ConfigurationTests
{
    private readonly GcpConfigurationValidator _validator = new();

    [Fact]
    public void InitializerExistsWithoutAnyConsumerAndUsesConfiguredProject()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        builder.AddFlociGcpEnvironment("gcp", "custom-project").WithBuckets("test-bucket");
        var initializers = builder.Resources.OfType<ProvisioningResource>().ToArray();
        var validationError = Record.Exception(() => _validator.ValidateAndThrow(initializers.Single()));

        // Assert
        initializers.Should().ContainSingle().Which.DefaultProjectId.Should().Be("custom-project");
        validationError.Should().BeNull();
    }

    [Fact]
    public void MissingSubscriptionFailsValidation()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var target = builder.AddContainer("target", "test").WithHttpEndpoint(targetPort: 8080);
        builder.AddFlociGcpEnvironment("gcp")
            .WithPushEndpoint("missing-subscription", target.GetEndpoint("http"), "/push");
        var initializer = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var error = Record.Exception(() => _validator.ValidateAndThrow(initializer));

        // Assert
        error.Should().BeOfType<ValidationException>().Which.Message.Should().Contain("undeclared subscription");
    }

    [Fact]
    public void ValidatesAfterAllDeclarationsRegardlessOfOrder()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var target = builder.AddContainer("target", "test").WithHttpEndpoint(targetPort: 8080);
        var gcp = builder.AddFlociGcpEnvironment("gcp")
            .WithPushEndpoint("test-subscription", target.GetEndpoint("http"), "/push")
            .WithPubSubConfigs(new PubSubConfig("floci-local", new MessageConfig("test-topic", "test-subscription")));
        var initializer = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var validConfigurationError = Record.Exception(() => _validator.ValidateAndThrow(initializer));
        gcp.WithPushEndpoint("test-subscription", target.GetEndpoint("http"), "/other");
        var duplicateDestinationError = Record.Exception(() => _validator.ValidateAndThrow(initializer));

        // Assert
        validConfigurationError.Should().BeNull();
        duplicateDestinationError.Should().BeOfType<ValidationException>()
            .Which.Message.Should().Contain("Multiple push destinations");
    }

    [Fact]
    public void RejectsDuplicateSubscriptions()
    {
        // Arrange
        var state = new ProvisioningResource("gcp-init", new FlociGcpContainerResource("gcp"), "floci-local");
        state.PubSubConfigs.Add(new("floci-local",
            new MessageConfig("topic-one", "same-subscription"),
            new MessageConfig("topic-two", "same-subscription")));

        // Act
        var error = Record.Exception(() => _validator.ValidateAndThrow(state));

        // Assert
        error.Should().BeOfType<ValidationException>().Which.Message.Should().Contain("Duplicate subscription");
    }

    [Fact]
    public void RejectsInvalidAckDeadline()
    {
        // Arrange
        var state = new ProvisioningResource("gcp-init", new FlociGcpContainerResource("gcp"), "floci-local");
        state.PubSubConfigs.Add(new("floci-local",
            new MessageConfig("test-topic", "test-subscription") { AckDeadlineSeconds = 1 }));

        // Act
        var error = Record.Exception(() => _validator.ValidateAndThrow(state));

        // Assert
        error.Should().BeOfType<ValidationException>().Which.Message.Should().Contain("AckDeadlineSeconds");
    }

    [Theory]
    [InlineData(9, 5, 0, 1, "AckDeadlineSeconds")]
    [InlineData(601, 5, 0, 1, "AckDeadlineSeconds")]
    [InlineData(60, 4, 0, 1, "MaxDeliveryAttempts")]
    [InlineData(60, 101, 0, 1, "MaxDeliveryAttempts")]
    [InlineData(60, 5, -1, 1, "MinimumRetryBackoff")]
    [InlineData(60, 5, 5, 2, "MaximumRetryBackoff")]
    [InlineData(60, 5, 0, 601, "MaximumRetryBackoff")]
    public void InvalidSubscriptionSettingsIdentifyTheFailingProperty(
        int ackDeadline, int deliveryAttempts, int minimumBackoff, int maximumBackoff, string expectedProperty)
    {
        // Arrange
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")
        {
            AckDeadlineSeconds = ackDeadline,
            MaxDeliveryAttempts = deliveryAttempts,
            MinimumRetryBackoff = TimeSpan.FromSeconds(minimumBackoff),
            MaximumRetryBackoff = TimeSpan.FromSeconds(maximumBackoff)
        }));

        // Act
        var result = _validator.Validate(state);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName.EndsWith(expectedProperty));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("//other-host/push")]
    [InlineData("")]
    public void RejectsInvalidAspirePushPaths(string path)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "test").WithHttpEndpoint(targetPort: 8080);
        builder.AddFlociGcpEnvironment("gcp")
            .WithPubSubConfigs(new PubSubConfig("floci-local", new MessageConfig("topic", "sub")))
            .WithPushEndpoint("sub", api.GetEndpoint("http"), path);
        var state = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var result = _validator.Validate(state);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("single '/'"));
    }

    [Theory]
    [InlineData("dcpclipath")]
    [InlineData("aspiredashboardpath")]
    public void AppHostBuildIncludesRequiredOrchestrationExecutables(string metadataKey)
    {
        // Arrange
        var appHostAssembly = typeof(Projects.FlociGcpTest_AppHost).Assembly;

        // Act
        var executable = appHostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(attribute.Key, metadataKey, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        // Assert
        executable.Should().NotBeNullOrWhiteSpace("the SDK must make the AppHost usable by a direct test runner");
        File.Exists(executable).Should().BeTrue("the orchestration executable generated by the SDK must exist");
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("http://localhost/push", null, false)]
    [InlineData(null, "dead-letter-topic", false)]
    [InlineData("http://localhost/push", "dead-letter-topic", false)]
    public void TopicOnlyConfigurationCannotContainSubscriptionPolicies(string? pushUrl, string? deadLetter, bool expectedValid)
    {
        // Arrange
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic")
        {
            PushEndpoint = pushUrl is null ? null : new Uri(pushUrl),
            DeadLetterTopic = deadLetter
        }));

        // Act
        var result = _validator.Validate(state);

        // Assert
        result.IsValid.Should().Be(expectedValid);
        result.Errors.Should().HaveCount(expectedValid ? 0 : 1);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost/push", true)]
    [InlineData("https://localhost/push", true)]
    [InlineData("/relative/push", false)]
    [InlineData("ftp://localhost/push", false)]
    public void SubscriptionAcceptsOnlyAbsoluteHttpPushUrls(string? pushUrl, bool expectedValid)
    {
        // Arrange
        var state = new ProvisioningResource("init", new FlociGcpContainerResource("gcp"), "project");
        state.PubSubConfigs.Add(new("project", new MessageConfig("topic", "subscription")
        {
            PushEndpoint = pushUrl is null ? null : new Uri(pushUrl, UriKind.RelativeOrAbsolute)
        }));

        // Act
        var result = _validator.Validate(state);

        // Assert
        result.IsValid.Should().Be(expectedValid);
        result.Errors.Should().HaveCount(expectedValid ? 0 : 1);
    }

    [Theory]
    [InlineData(null, "project", true)]
    [InlineData("project", "project", true)]
    [InlineData("project", "other-project", false)]
    public void DuplicateBucketDeclarationsMustReferToTheSameProject(string? firstProject, string secondProject, bool expectedValid)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddFlociGcpEnvironment("gcp", "project").WithBucketConfigs(
            new("bucket", firstProject), new("bucket", secondProject));
        var state = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var result = _validator.Validate(state);

        // Assert
        state.Buckets.Should().HaveCount(2);
        result.IsValid.Should().Be(expectedValid);
        result.Errors.Should().HaveCount(expectedValid ? 0 : 1);
    }

    [Theory]
    [InlineData("http", null, true)]
    [InlineData("https", null, true)]
    [InlineData("tcp", null, false)]
    [InlineData("http", "http://external/push", false)]
    public void AspirePushDestinationMustUseHttpAndHaveNoCompetingUrl(string scheme, string? absoluteUrl, bool expectedValid)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "test").WithEndpoint(name: "push", scheme: scheme, targetPort: 8080);
        builder.AddFlociGcpEnvironment("gcp", "project")
            .WithPubSubConfigs(new PubSubConfig("project", new MessageConfig("topic", "subscription")
            {
                PushEndpoint = absoluteUrl is null ? null : new Uri(absoluteUrl)
            }))
            .WithPushEndpoint("subscription", api.GetEndpoint("push"), "/push", projectId: "project");
        var state = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var result = _validator.Validate(state);

        // Assert
        state.PushDestinations.Should().ContainSingle().Which.ProjectId.Should().Be("project");
        result.IsValid.Should().Be(expectedValid);
        result.Errors.Should().HaveCount(expectedValid ? 0 : 1);
    }

    [Fact]
    public void MultipleEmulatorDataMountsAreRejected()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddFlociGcpEnvironment("gcp").WithDataVolume("first").WithDataVolume("second");
        var state = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var result = _validator.Validate(state);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("only one data mount");
    }

    [Fact]
    public void ConfiguringToolkitResourceWithoutEnvironmentInitializationFailsClearly()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var gcp = builder.AddFlociGcp("gcp");

        // Act
        var error = Record.Exception(() => gcp.WithBuckets("bucket"));

        // Assert
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("AddFlociGcpEnvironment");
    }

    [Fact]
    public void PlaygroundWithoutProjectConfigurationFailsBeforeCreatingGoogleClients()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var error = Record.Exception(() => PlaygroundGcpSettings.FromConfiguration(configuration));

        // Assert
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("GOOGLE_CLOUD_PROJECT");
    }
}
