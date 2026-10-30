using Google;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace Floci.Hosting.Gcp.Tests;

public sealed class ResilienceTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public void ClassifiesHttpFailures(HttpStatusCode status, bool expectedTransient)
    {
        // Arrange
        var failure = new GoogleApiException("test", "failure") { HttpStatusCode = status };

        // Act
        var isTransient = RetryExecutor.IsTransient(failure);

        // Assert
        isTransient.Should().Be(expectedTransient);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void ClassifiesTransportFailures(HttpStatusCode? status, bool expectedTransient)
    {
        // Arrange
        var failure = new HttpRequestException("HTTP failure", null, status);

        // Act
        var isTransient = RetryExecutor.IsTransient(failure);

        // Assert
        isTransient.Should().Be(expectedTransient);
    }
    [Fact]
    public async Task RetriesTransientFailureUntilSuccess()
    {
        // Arrange
        var retry = CreateExecutor(attempts: 3);
        var attempts = 0;

        Task Operation(CancellationToken cancellationToken)
        {
            attempts++;

            if (attempts < 3)
                throw new HttpRequestException("starting", null, HttpStatusCode.ServiceUnavailable);

            return Task.CompletedTask;
        }

        // Act
        await retry.ExecuteAsync("test", Operation, CancellationToken.None);

        // Assert
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task AttemptsAreBounded()
    {
        // Arrange
        var retry = CreateExecutor(attempts: 3);
        var attempts = 0;

        Task Operation(CancellationToken cancellationToken)
        {
            attempts++;
            throw new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
        }

        // Act
        var error = await Record.ExceptionAsync(() => retry.ExecuteAsync("test", Operation, CancellationToken.None));

        // Assert
        error.Should().BeOfType<HttpRequestException>();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task PermanentFailuresAreNotRetried()
    {
        // Arrange
        var retry = CreateExecutor(attempts: 3);
        var attempts = 0;

        Task Operation(CancellationToken cancellationToken)
        {
            attempts++;
            throw new ArgumentException("invalid");
        }

        // Act
        var error = await Record.ExceptionAsync(() => retry.ExecuteAsync("test", Operation, CancellationToken.None));

        // Assert
        error.Should().BeOfType<ArgumentException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task CancellationInterruptsInFlightAttempt()
    {
        // Arrange
        var retry = CreateExecutor(attempts: 3);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Operation(CancellationToken token)
        {
            started.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        // Act
        var operation = retry.ExecuteAsync("test", Operation, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => operation);

        // Assert
        error.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task SingleAttemptDoesNotRetryFailure()
    {
        // Arrange
        var retry = CreateExecutor(attempts: 1);
        var attempts = 0;

        Task Operation(CancellationToken cancellationToken)
        {
            attempts++;
            throw new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
        }

        // Act
        var error = await Record.ExceptionAsync(() => retry.ExecuteAsync("test", Operation, CancellationToken.None));

        // Assert
        error.Should().BeOfType<HttpRequestException>();
        attempts.Should().Be(1);
    }

    private static RetryExecutor CreateExecutor(int attempts) => new(NullLogger.Instance, attempts, TimeSpan.Zero);
}
