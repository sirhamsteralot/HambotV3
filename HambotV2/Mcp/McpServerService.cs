using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HambotV2.Mcp
{
    /// <summary>
    /// Hosts the MCP endpoint in-process over streamable HTTP: a Kestrel web
    /// app started by this worker at <c>Mcp:ListenUri</c> with the session endpoint mapped at
    /// <see cref="EndpointPath"/>, the bearer-key check in front of everything, and
    /// the tools resolved from this host's <see cref="IMcpTools"/> instance. The endpoint runs
    /// only when enabled with a key configured; the startup log names the state and
    /// the listen address either way.
    /// </summary>
    internal sealed class McpServerService : BackgroundService
    {
        /// <summary>The streamable-HTTP session endpoint path under the listen address.</summary>
        internal const string EndpointPath = "/mcp";

        private readonly McpConfiguration _configuration;
        private readonly IMcpTools _tools;
        private readonly McpEndpointStatus _endpointStatus;
        private readonly ILogger<McpServerService> _logger;

        public McpServerService(
            IConfiguration configuration,
            IMcpTools tools,
            McpEndpointStatus endpointStatus,
            ILogger<McpServerService> logger)
        {
            _configuration = McpConfiguration.Parse(configuration);
            _tools = tools;
            _endpointStatus = endpointStatus;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.IsActive)
            {
                string reason = _configuration.Enabled
                    ? "Mcp:Enabled is true but no Mcp:ApiKey is configured"
                    : "Mcp:Enabled is false";
                _logger.LogInformation("MCP endpoint disabled: {Reason}. Configure Mcp:Enabled and Mcp:ApiKey to turn it on.", reason);
                _endpointStatus.SetState($"disabled ({reason})");
                return;
            }

            // The endpoint's own web app: loopback-facing by configuration, silent logging
            // (the tool layer logs through the main host), tools from this host's instance.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(_configuration.ListenUri);
            builder.Services.AddSingleton(_tools);
            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools<McpToolEndpoints>();

            WebApplication app = builder.Build();

            // Bearer-key gate: every request is checked before any tool logic runs.
            string apiKey = _configuration.ApiKey;
            app.Use(async (context, next) =>
            {
                if (!McpBearerAuthentication.IsValid(context.Request.Headers.Authorization, apiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });

            app.MapMcp(EndpointPath);

            string endpoint = $"{_configuration.ListenUri.TrimEnd('/')}{EndpointPath}";
            _logger.LogInformation("MCP endpoint listening at {Endpoint}", endpoint);
            _endpointStatus.SetState($"listening at {endpoint}");

            await app.RunAsync(stoppingToken);
        }
    }
}
