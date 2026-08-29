using System.Net;
using System.Net.Sockets;

namespace NexusApp.Brokers;

/// <summary>
/// Failure classification for HTTP calls against a broker API.
/// Distinguishes failures that are safe to retry from genuine, terminal
/// broker rejections that must never be retried.
/// </summary>
internal enum FailureKind
{
    /// <summary>Call succeeded (HTTP 2xx). No retry needed.</summary>
    Success,

    /// <summary>
    /// The request provably never reached the exchange (connection refused,
    /// DNS failure, TLS handshake failure, socket error before send).
    /// Safe to retry even for non-idempotent operations such as order placement.
    /// </summary>
    ConnectFailure,

    /// <summary>
    /// A transient failure whose outcome is ambiguous — a timeout after send,
    /// or an HTTP 5xx / 429 returned by the gateway. Safe to retry for
    /// idempotent reads, but NOT for order placement (risk of duplicate order).
    /// </summary>
    Transient,

    /// <summary>
    /// A genuine terminal failure — the broker/RMS rejected the request
    /// (auth error, insufficient margin, invalid contract, HTTP 4xx other
    /// than 429). Must never be retried.
    /// </summary>
    Terminal
}

/// <summary>
/// Bounded retry-with-backoff primitives shared by broker API clients
/// (Angel One, AliceBlue, ...).
/// </summary>
internal static class TransientRetry
{
    public const int DefaultMaxAttempts = 3;

    private static readonly Random _jitter = new();

    /// <summary>
    /// Classifies an HTTP status code returned by a completed request.
    /// </summary>
    public static FailureKind Classify(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
            return FailureKind.Success;

        // Gateway overload / rate limiting / server-side faults are transient.
        if (status == HttpStatusCode.RequestTimeout ||        // 408
            status == HttpStatusCode.TooManyRequests ||       // 429
            (int)status >= 500)                               // 5xx
            return FailureKind.Transient;

        // Any other 4xx is a genuine client/broker rejection.
        return FailureKind.Terminal;
    }

    /// <summary>
    /// Classifies an exception raised while performing an HTTP call.
    /// </summary>
    public static FailureKind Classify(Exception ex)
    {
        // A cancelled task without external cancellation == HttpClient timeout.
        // The request may already have reached the server, so it is ambiguous.
        if (ex is TaskCanceledException or OperationCanceledException)
            return FailureKind.Transient;

        if (ex is HttpRequestException httpEx)
        {
            // A socket-level failure means the connection never completed,
            // so the request never reached the exchange — safe to retry.
            if (httpEx.InnerException is SocketException socketEx)
            {
                return socketEx.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused or
                    SocketError.HostNotFound or
                    SocketError.HostUnreachable or
                    SocketError.NetworkUnreachable or
                    SocketError.TryAgain or
                    SocketError.NoData or
                    SocketError.TimedOut => FailureKind.ConnectFailure,
                    _ => FailureKind.Transient
                };
            }

            // Other HttpRequestExceptions (e.g. connection reset mid-flight)
            // are ambiguous transients.
            return FailureKind.Transient;
        }

        return FailureKind.Terminal;
    }

    /// <summary>
    /// Computes the backoff delay for a given (zero-based) attempt index using
    /// exponential growth (500ms, 1s, 2s, ...) plus a small random jitter.
    /// </summary>
    public static TimeSpan BackoffFor(int attemptIndex)
    {
        var baseMs = 500 * (1 << Math.Min(attemptIndex, 5));
        var jitterMs = _jitter.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }
}
