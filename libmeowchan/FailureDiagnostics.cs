using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace MeowChan;

/// <summary>Transport evidence reported by a typed failure; not a phase timing or root cause.</summary>
public enum FailureNetworkStage { NameResolution, Connection, SecureConnection }

/// <summary>
/// Supplies passive failure evidence across adapter boundaries without a plugin SDK dependency.
/// Metadata is optional and transient; implementations must not derive action authority from it.
/// </summary>
public interface IFailureInfoProvider
{
    FailureInfo FailureInfo { get; }
}

/// <summary>Projects exception facts only; never parses messages, performs I/O, or schedules retries.</summary>
internal static class FailureDiagnostics
{
    internal static FailureInfo FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is IFailureInfoProvider provider)
            return provider.FailureInfo;

        // An outer cancellation/deadline remains that observation even if its inner
        // exception mentions a transport failure. No caller cancellation context exists here.
        return exception switch
        {
            OperationCanceledException => new FailureInfo(FailureKind.Unknown, exception.Message)
                { DiagnosticCode = "operation-cancelled" },
            TimeoutException => new FailureInfo(FailureKind.Timeout, exception.Message),
            HttpRequestException request when request.StatusCode != null =>
                new FailureInfo(FailureKind.Http, request.Message, request.StatusCode),
            HttpRequestException request => FromHttp(request, request.HttpRequestError),
            HttpIOException io => FromHttp(io, io.HttpRequestError),
            SocketException socket => FromSocket(socket, socket.Message),
            JsonException or System.Text.Json.JsonException or InvalidDataException or
                FormatException or OverflowException or InvalidCastException =>
                new FailureInfo(FailureKind.InvalidData, exception.Message),
            UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or
                DriveNotFoundException or PathTooLongException =>
                new FailureInfo(FailureKind.Storage, exception.Message),
            // Plain IOException can be a failed body read or a failed local write.
            // AuthenticationException alone does not distinguish TLS from other authentication.
            _ => new FailureInfo(FailureKind.Unknown, exception.Message)
        };
    }

    private static FailureInfo FromHttp(Exception exception, HttpRequestError error)
    {
        var stage = error switch
        {
            HttpRequestError.NameResolutionError => FailureNetworkStage.NameResolution,
            HttpRequestError.ConnectionError => FailureNetworkStage.Connection,
            HttpRequestError.SecureConnectionError => FailureNetworkStage.SecureConnection,
            _ => (FailureNetworkStage?)null
        };
        if (stage != null)
            return new FailureInfo(FailureKind.Network, exception.Message)
                { NetworkStage = stage, DiagnosticCode = HttpCode(error) };

        // Unknown is also used by policy/configuration rejections in custom HTTP
        // transports. Only refine it from a narrow typed HTTP inner chain, never
        // unwrap arbitrary application wrappers or combine AggregateException branches.
        if (error == HttpRequestError.Unknown)
        {
            var inner = exception.InnerException;
            for (var depth = 0; inner != null && depth < 8; depth++)
            {
                if (inner is SocketException socket)
                    return FromSocket(socket, exception.Message);
                if (inner is not HttpRequestException and not HttpIOException)
                    break;
                // An inner HTTP status or a more specific HTTP error is not evidence
                // for the outer unknown failure's transport stage.
                if (inner is HttpRequestException request &&
                    (request.StatusCode != null || request.HttpRequestError != HttpRequestError.Unknown))
                    break;
                if (inner is HttpIOException io && io.HttpRequestError != HttpRequestError.Unknown)
                    break;
                inner = inner.InnerException;
            }
        }

        // These are explicit transport/protocol observations, but do not establish
        // DNS, connection establishment, TLS, or the party responsible for the failure.
        var kind = error is HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse or
            HttpRequestError.ResponseEnded ? FailureKind.Network : FailureKind.Unknown;
        return new FailureInfo(kind, exception.Message) { DiagnosticCode = HttpCode(error) };
    }

    private static string? HttpCode(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "http-name-resolution",
        HttpRequestError.ConnectionError => "http-connection",
        HttpRequestError.SecureConnectionError => "http-secure-connection",
        HttpRequestError.HttpProtocolError => "http-protocol",
        HttpRequestError.InvalidResponse => "http-invalid-response",
        HttpRequestError.ResponseEnded => "http-response-ended",
        _ => null
    };

    private static FailureInfo FromSocket(SocketException socket, string message)
    {
        var stage = socket.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoRecovery or
                SocketError.NoData => FailureNetworkStage.NameResolution,
            SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted or
                SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or
                SocketError.NetworkReset => FailureNetworkStage.Connection,
            _ => (FailureNetworkStage?)null
        };
        // Socket timeout does not identify which transport phase expired. Other
        // socket errors can be local configuration/cancellation, so stay Unknown.
        var kind = stage != null || socket.SocketErrorCode == SocketError.TimedOut
            ? FailureKind.Network : FailureKind.Unknown;
        return new FailureInfo(kind, message)
        {
            NetworkStage = stage,
            DiagnosticCode = "socket-" + socket.SocketErrorCode
        };
    }
}
