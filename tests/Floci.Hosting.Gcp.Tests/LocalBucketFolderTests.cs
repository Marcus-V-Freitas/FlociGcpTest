using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FluentValidation;

namespace Floci.Hosting.Gcp.Tests;

public sealed class LocalBucketFolderTests
{
    [Fact]
    public void RelativeFolderUsesAppHostDirectoryWithoutMountingEmulatorState()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var expectedFolder = Path.GetFullPath("bucket-data", builder.AppHostDirectory);

        // Act
        var gcp = builder.AddFlociGcpEnvironment("gcp").WithBucketFolder("./bucket-data");

        // Assert
        var state = builder.Resources.OfType<ProvisioningResource>().Should().ContainSingle().Which;
        state.BucketFolder.Should().Be(expectedFolder);
        gcp.Resource.Annotations.OfType<ContainerMountAnnotation>().Should().BeEmpty();
    }

    [Fact]
    public void RepeatedFolderKeepsTheSameSourceWithoutAddingMounts()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var gcp = builder.AddFlociGcpEnvironment("gcp").WithBucketFolder("./bucket-data");
        var state = builder.Resources.OfType<ProvisioningResource>().Single();
        var originalFolder = state.BucketFolder;

        // Act
        gcp.WithBucketFolder("bucket-data");

        // Assert
        state.BucketFolder.Should().Be(originalFolder);
        gcp.Resource.Annotations.OfType<ContainerMountAnnotation>().Should().BeEmpty();
    }

    [Fact]
    public void RejectsDifferentSourceButAllowsSeparatePersistence()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var gcp = builder.AddFlociGcpEnvironment("gcp").WithBucketFolder("./bucket-data");
        var validator = new GcpConfigurationValidator();
        var state = builder.Resources.OfType<ProvisioningResource>().Single();

        // Act
        var folderError = Record.Exception(() => gcp.WithBucketFolder("./other-data"));
        gcp.WithDataVolume("emulator-state");
        var persistenceError = Record.Exception(() => validator.ValidateAndThrow(state));

        // Assert
        folderError.Should().BeOfType<InvalidOperationException>();
        persistenceError.Should().BeNull();
        gcp.Resource.Annotations.OfType<ContainerMountAnnotation>().Should().ContainSingle()
            .Which.Target.Should().Be("/app/data");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsMissingFolder(string? folder)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var gcp = builder.AddFlociGcpEnvironment("gcp");

        // Act
        var error = Record.Exception(() => gcp.WithBucketFolder(folder!));

        // Assert
        error.Should().BeAssignableTo<ArgumentException>();
    }
}
