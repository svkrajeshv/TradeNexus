using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace NexusApp.Services;

/// <summary>
/// Builds the SignalR client used by the interactive-server components.
/// <para>
/// Because those components execute on the server, the connection is an outbound
/// loopback call the process makes back to itself. Using the browser-facing URL
/// (NavigationManager) breaks in deployment: behind a reverse proxy that host/port
/// is not reachable from inside the container, and HTTPS redirection makes the
/// self-call fail certificate validation - both surface as a TaskCanceledException
/// during /negotiate. Binding to the address Kestrel actually listens on (preferring
/// plain HTTP) keeps the hop local and reliable.
/// </para>
/// </summary>
public class HubConnectionFactory(IServer server, ILogger<HubConnectionFactory> logger)
{
    private static readonly string[] WildcardHosts = ["*", "+", "0.0.0.0", "[::]", "::"];

    public HubConnection Create(NavigationManager nav, string hubPath = "/tradingHub")
    {
        var baseUri = ResolveLoopbackBase() ?? new Uri(nav.BaseUri);
        var httpUri = new Uri(baseUri, hubPath.TrimStart('/'));

        // SkipNegotiation + WebSockets requires the ws/wss scheme rather than http/https.
        var hubUri = new UriBuilder(httpUri)
        {
            Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
        }.Uri;

        return new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                // Skip the negotiate step and go straight to WebSockets.
                //
                // On Azure App Service the loopback address routes back through the
                // IIS/ANCM front end, where request-filtering modules reject the
                // negotiate POST with "405 Method Not Allowed" - a layer that does not
                // exist when running locally, which is why this only failed once
                // deployed. SkipNegotiation removes that POST entirely; it is valid
                // precisely because a single fixed transport is being forced here.
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;

                // The loopback target is this very process; a self-signed or proxy-owned
                // certificate must not abort the handshake.
                options.HttpMessageHandlerFactory = static handler =>
                {
                    if (handler is HttpClientHandler http)
                    {
                        http.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }
                    return handler;
                };
            })
            .WithAutomaticReconnect()
            .Build();
    }

    private Uri? ResolveLoopbackBase()
    {
        try
        {
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count == 0)
            {
                return null;
            }

            // Plain HTTP first: it maps to a ws:// loopback, avoiding certificate
            // validation on a hop that never leaves the machine.
            var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                          ?? addresses.First();

            var builder = new UriBuilder(address);
            if (WildcardHosts.Contains(builder.Host, StringComparer.OrdinalIgnoreCase))
            {
                builder.Host = "127.0.0.1";
            }
            builder.Path = "/";

            return builder.Uri;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve a loopback hub address; falling back to the request base URI");
            return null;
        }
    }
}
