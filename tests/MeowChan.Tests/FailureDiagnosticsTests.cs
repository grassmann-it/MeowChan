using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace MeowChan.Tests;

/// <summary>
/// Contract examples, not a second implementation of the exception mapper.
/// Every exception is constructed locally; no request, socket, file, or clock is used.
/// </summary>
public sealed class FailureDiagnosticsTests
{
    [Fact]
    public void ExistingConstructorAndThreeValueDeconstructionRemainAvailable()
    {
        FailureInfo original = new FailureInfo(FailureKind.Http, "Synthetic response", HttpStatusCode.Forbidden);
        var (kind, message, status) = original;

        Assert.Equal(FailureKind.Http, kind);
        Assert.Equal("Synthetic response", message);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(original.DiagnosticCode);
        Assert.Null(original.RetryAt);
        Assert.Null(original.NetworkStage);
        Assert.Null(new FailureInfo(FailureKind.Unknown, "Synthetic legacy failure").HttpStatusCode);

        FailureInfo annotated = original with
        {
            DiagnosticCode = "synthetic-producer-code",
            RetryAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)
        };
        var (annotatedKind, annotatedMessage, annotatedStatus) = annotated;
        Assert.Equal(kind, annotatedKind);
        Assert.Equal(message, annotatedMessage);
        Assert.Equal(status, annotatedStatus);
        Assert.Null(original.DiagnosticCode);
        Assert.Null(original.RetryAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void ObservedHttpStatusTakesPrecedenceOverConflictingTransportEvidence(HttpStatusCode status)
    {
        // Status is observed HTTP evidence, even if a custom transport also supplies
        // a contradictory error category/inner exception. No ban/login inference.
        Exception[] failures =
        [
            new HttpRequestException(HttpRequestError.NameResolutionError, "TLS timeout; HTTP 500",
                new SocketException((int)SocketError.ConnectionRefused), status),
            new HttpRequestException(HttpRequestError.ConnectionError, "DNS failure; HTTP 200",
                new AuthenticationException("Synthetic authentication failure"), status),
            new HttpRequestException(HttpRequestError.SecureConnectionError, "Disk full; HTTP 401",
                new SocketException((int)SocketError.HostNotFound), status)
        ];

        foreach (Exception exception in failures)
        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Http, actual.Kind);
            Assert.Equal(status, actual.HttpStatusCode);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, FailureNetworkStage.NameResolution)]
    [InlineData(HttpRequestError.ConnectionError, FailureNetworkStage.Connection)]
    [InlineData(HttpRequestError.SecureConnectionError, FailureNetworkStage.SecureConnection)]
    public void TypedHttpFailuresExposeOnlyTheirReportedNetworkStage(
        HttpRequestError error, FailureNetworkStage expectedStage)
    {
        Exception[] failures =
        [
            new HttpRequestException(error, "Opaque synthetic transport failure"),
            new HttpIOException(error, "Opaque synthetic response read failure")
        ];

        foreach (Exception exception in failures)
        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Network, actual.Kind);
            Assert.Equal(expectedStage, actual.NetworkStage);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Fact]
    public void ExplicitSecureConnectionErrorIsNotOverriddenByAnInnerDnsError()
    {
        HttpRequestException exception = new HttpRequestException(
            HttpRequestError.SecureConnectionError, "Synthetic opaque failure",
            new SocketException((int)SocketError.HostNotFound));

        FailureInfo actual = FailureInfo.FromException(exception, media: false);

        Assert.Equal(FailureKind.Network, actual.Kind);
        Assert.Equal(FailureNetworkStage.SecureConnection, actual.NetworkStage);
        Assert.Null(actual.HttpStatusCode);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, FailureNetworkStage.NameResolution)]
    [InlineData(SocketError.TryAgain, FailureNetworkStage.NameResolution)]
    [InlineData(SocketError.ConnectionRefused, FailureNetworkStage.Connection)]
    [InlineData(SocketError.ConnectionReset, FailureNetworkStage.Connection)]
    [InlineData(SocketError.NetworkUnreachable, FailureNetworkStage.Connection)]
    public void TypedSocketEvidenceSurvivesDirectAndHttpWrappedFailures(
        SocketError error, FailureNetworkStage expectedStage)
    {
        SocketException socket = new SocketException((int)error);
        Exception[] failures =
        [
            socket,
            new HttpRequestException("Opaque synthetic request failure", socket),
            new HttpIOException(HttpRequestError.Unknown, "Opaque synthetic body failure", socket)
        ];

        foreach (Exception exception in failures)
        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Network, actual.Kind);
            Assert.Equal(expectedStage, actual.NetworkStage);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    [InlineData(HttpRequestError.InvalidResponse)]
    public void TypedProtocolOrBodyFailureDoesNotInventDnsConnectionOrTlsStage(HttpRequestError error)
    {
        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(
                new HttpIOException(error, "DNS TLS connection timeout HTTP 429"), media);

            Assert.Equal(FailureKind.Network, actual.Kind);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData("DNS name resolution failed")]
    [InlineData("TLS certificate expired")]
    [InlineData("Connection refused by firewall")]
    [InlineData("HTTP 403 banned; HTTP 404 missing; HTTP 429 retry now")]
    [InlineData("HTTP timeout waiting for response body; disk full")]
    public void UnknownFailuresNeverAcquireFactsFromTheirMessage(string message)
    {
        Exception[] failures =
        [
            new Exception(message),
            new HttpRequestException(message),
            new HttpIOException(HttpRequestError.Unknown, message),
            new IOException(message),
            new AuthenticationException(message)
        ];

        foreach (Exception exception in failures)
        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Unknown, actual.Kind);
            // HttpIOException appends its typed error to Message; preserve the actual
            // exception message, not only the constructor's input string.
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerCancellationIsNotATimeoutEvenWithTransportEvidence(bool media)
    {
        using CancellationTokenSource caller = new CancellationTokenSource();
        caller.Cancel();
        Exception[] failures =
        [
            new OperationCanceledException(caller.Token),
            new TaskCanceledException("HTTP timeout", null, caller.Token),
            new OperationCanceledException("TLS timeout",
                new HttpRequestException(HttpRequestError.SecureConnectionError), caller.Token),
            new OperationCanceledException("DNS failed",
                new SocketException((int)SocketError.HostNotFound), caller.Token),
            new OperationCanceledException("Body deadline", new TimeoutException(), caller.Token)
        ];

        foreach (Exception exception in failures)
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Unknown, actual.Kind);
            Assert.Equal("operation-cancelled", actual.DiagnosticCode);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationWithoutCallerContextAlsoDoesNotEstablishTimeout(bool media)
    {
        foreach (Exception exception in new Exception[]
                 { new OperationCanceledException("HTTP timeout"), new TaskCanceledException("DNS timeout") })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(FailureKind.Unknown, actual.Kind);
            Assert.Equal("operation-cancelled", actual.DiagnosticCode);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
            Assert.Null(actual.RetryAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimeoutExceptionEstablishesDeadlineButNotNetworkStage(bool media)
    {
        TimeoutException exception = new TimeoutException("Synthetic opaque deadline",
            new HttpRequestException(HttpRequestError.NameResolutionError));

        FailureInfo actual = FailureInfo.FromException(exception, media);

        Assert.Equal(FailureKind.Timeout, actual.Kind);
        Assert.Equal(exception.Message, actual.Message);
        Assert.Null(actual.NetworkStage);
        Assert.Null(actual.HttpStatusCode);
        Assert.Null(actual.RetryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDataAndExplicitLocalIoRemainDistinctFromNetworkFailures(bool media)
    {
        // Concrete local exception types are evidence; the media flag or an I/O
        // message alone is not. Plain IOException is covered by Unknown above.
        Exception[] invalidData =
        [
            new InvalidDataException("Synthetic disk full / HTTP 429 text"),
            new FormatException("Synthetic connection refused text"),
            new System.Text.Json.JsonException("Synthetic TLS text")
        ];
        foreach (Exception exception in invalidData)
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);
            Assert.Equal(FailureKind.InvalidData, actual.Kind);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
        }

        Exception[] localIo =
        [
            new UnauthorizedAccessException("Synthetic DNS text"),
            new FileNotFoundException("Synthetic HTTP 404 text"),
            new DirectoryNotFoundException("Synthetic TLS text"),
            new PathTooLongException("Synthetic HTTP timeout text")
        ];
        foreach (Exception exception in localIo)
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);
            Assert.Equal(FailureKind.Storage, actual.Kind);
            Assert.Equal(exception.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
            Assert.Null(actual.HttpStatusCode);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void ProviderMetadataWinsOverExceptionTypeAndIsPreservedForBothOperationContexts(HttpStatusCode status)
    {
        FailureInfo reported = new FailureInfo(FailureKind.Http, "Opaque producer result", status)
        {
            DiagnosticCode = "vendor-unrecognized-code",
            RetryAt = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.FromHours(2))
        };
        ReportedFailureException exception = new ReportedFailureException(reported);

        foreach (bool media in new[] { false, true })
        {
            FailureInfo actual = FailureInfo.FromException(exception, media);

            Assert.Equal(reported, actual);
            Assert.Equal(status, actual.HttpStatusCode);
            Assert.Equal("vendor-unrecognized-code", actual.DiagnosticCode);
            Assert.Equal(reported.RetryAt, actual.RetryAt);
            Assert.Equal(reported.RetryAt!.Value.Offset, actual.RetryAt!.Value.Offset);
            Assert.Equal(reported.Message, actual.Message);
            Assert.Null(actual.NetworkStage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderNetworkStageAndUnknownWorkerDeadlineAreNotReinterpreted(bool media)
    {
        FailureInfo secure = new FailureInfo(FailureKind.Network, "Opaque producer observation")
        {
            DiagnosticCode = "vendor-secure-observation",
            NetworkStage = FailureNetworkStage.SecureConnection
        };
        FailureInfo workerDeadline = new FailureInfo(FailureKind.Unknown, "HTTP TLS timeout")
        {
            DiagnosticCode = "timeout"
        };

        Assert.Equal(secure, FailureInfo.FromException(new ReportedFailureException(secure), media));
        FailureInfo actual = FailureInfo.FromException(new ReportedFailureException(workerDeadline), media);
        Assert.Equal(workerDeadline, actual);
        Assert.Equal(FailureKind.Unknown, actual.Kind);
        Assert.Equal("timeout", actual.DiagnosticCode);
        Assert.Null(actual.NetworkStage);
        Assert.Null(actual.HttpStatusCode);
        Assert.Null(actual.RetryAt);
    }

    private sealed class ReportedFailureException : HttpRequestException, IFailureInfoProvider
    {
        internal ReportedFailureException(FailureInfo failureInfo)
            : base(HttpRequestError.ConnectionError, "Deliberately different outer failure",
                new SocketException((int)SocketError.HostNotFound), HttpStatusCode.BadGateway)
        {
            FailureInfo = failureInfo;
        }

        public FailureInfo FailureInfo { get; }
    }
}
