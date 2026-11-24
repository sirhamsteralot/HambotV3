using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HambotV2.FuelUsageMonitor
{
    /// <summary>
    /// The ingestion endpoint's own Kestrel host: the route the phone app is pointed at, mapped for
    /// both methods the app is known to use. Built here rather than shared with the MCP surface so
    /// each inbound endpoint keeps its own key, address and enablement; started by the hosted
    /// service in production and directly by tests for a loopback smoke run.
    /// </summary>
    internal sealed class FuelUsageWebHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FuelUsageWebHost(WebApplication app, Uri baseUri)
        {
            _app = app;
            BaseUri = baseUri;
        }

        /// <summary>The bound address (the real port when the configured one is 0, as in tests).</summary>
        public Uri BaseUri { get; }

        public static async Task<FuelUsageWebHost> StartAsync(
            FuelUsageMonitorSettings settings,
            RequestDelegate handler,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(settings.ListenUri);
            builder.WebHost.ConfigureKestrel(options =>
            {
                // Both limits are set from configuration so the gate's own 414 and the body cap the
                // endpoint enforces stay authoritative: the framework never refuses first.
                options.Limits.MaxRequestBodySize = settings.MaxBodyBytes;
                options.Limits.MaxRequestLineSize = settings.MaxQueryBytes + settings.ListenPath.Length + 128;
            });

            WebApplication app = builder.Build();

            // GET and POST both reach the same handler: the app's method is not dependable. Only the
            // configured route is mapped, so a wrong URL is a plain 404 and never looks like a
            // working endpoint.
            string[] methods = [HttpMethods.Get, HttpMethods.Post];
            app.MapMethods(settings.ListenPath, methods, handler);

            await app.StartAsync(cancellationToken);

            string? address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

            return new FuelUsageWebHost(app, new Uri(address ?? settings.ListenUri));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
