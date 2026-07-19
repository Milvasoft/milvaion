using Milvaion.Api.Mcp;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// Registration and mapping for the Milvaion MCP server.
/// </summary>
public static class McpExtensions
{
    /// <summary>
    /// Route the MCP endpoint is served from.
    /// </summary>
    public const string McpRoute = "/mcp";

    /// <summary>
    /// Adds the MCP server and its tools.
    /// </summary>
    public static IServiceCollection AddMilvaionMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpPermissionGuard>();

        services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "milvaion",
                Version = typeof(McpExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            };

            options.ServerInstructions =
                "Milvaion is a distributed job scheduler. Jobs are definitions with a schedule; occurrences are " +
                "individual executions of those jobs. When investigating a problem, start with list_failures " +
                "rather than list_occurrences - it is a much smaller set of jobs that exhausted their retries. " +
                "Use get_occurrence to read the logs and exception for a specific execution. " +
                "For questions about the system as a whole rather than one execution - what is going wrong, " +
                "which job is noisiest, what errors are new - call summarize_logs first: it returns counts " +
                "whose size does not grow with the log volume. Only then use search_logs, narrowed by what " +
                "the summary pointed at. " +
                "trigger_job and trigger_workflow cause real work to run in the user's environment; ask before " +
                "calling them. " +
                "Tools whose id parameter is a list - delete_failures, resolve_failures, delete_occurrences - are " +
                "bulk operations: collect every id first and make one call, never a call per record.";
        })
        .WithHttpTransport(options =>
        {
            // Stateless keeps any API replica able to serve any request, which matters because Milvaion is
            // routinely run behind a load balancer with several API instances. It also rules out
            // server-to-client requests such as sampling, which none of these tools need.
            options.Stateless = true;
        })
        // The assembly must be passed explicitly. The parameterless overload scans Assembly.GetEntryAssembly(),
        // which is this API when it runs normally but the test host when it runs under WebApplicationFactory -
        // so tool discovery either finds nothing or fails outright, and the failure surfaces as the host never
        // being built.
        .WithToolsFromAssembly(typeof(McpExtensions).Assembly)
        .WithPromptsFromAssembly(typeof(McpExtensions).Assembly);

        return services;
    }

    /// <summary>
    /// Maps the MCP endpoint.
    /// </summary>
    /// <remarks>
    /// Authorization is required at the endpoint, so an anonymous caller never reaches a tool. The default policy
    /// accepts both the login token and an api key, which means a developer can point an MCP client at this
    /// endpoint with nothing more than a key created in the dashboard. Per-permission checks happen inside the
    /// tools themselves - see <see cref="McpPermissionGuard"/>.
    /// </remarks>
    public static WebApplication MapMilvaionMcp(this WebApplication app)
    {
        app.MapMcp(McpRoute).RequireAuthorization();

        return app;
    }
}
