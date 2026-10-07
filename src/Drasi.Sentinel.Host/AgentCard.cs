namespace Drasi.Sentinel.Host;

public static class AgentCard
{
    public static object Create(
        ServiceProfile profile,
        A2aAuthenticationManifest authentication,
        Uri publicBaseUri) =>
        authentication switch
        {
            A2aJwtManifest => Protected(publicBaseUri, authentication),
            A2aAnonymousManifest when profile == ServiceProfile.Demo => Anonymous(publicBaseUri, authentication),
            A2aAnonymousManifest => throw new ArgumentException(
                "Anonymous A2A authentication is only valid for the demo profile.",
                nameof(profile)),
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        };

    private static object Protected(Uri publicBaseUri, A2aAuthenticationManifest authentication)
    {
        var card = Card(publicBaseUri, authentication);
        card["securitySchemes"] = new
        {
            bearerAuth = new { type = "http", scheme = "bearer", bearerFormat = "JWT" },
        };
        card["security"] = new[]
        {
            new { bearerAuth = Array.Empty<string>() },
        };
        return card;
    }

    private static object Anonymous(Uri publicBaseUri, A2aAuthenticationManifest authentication) => Card(publicBaseUri, authentication);

    private static Dictionary<string, object> Card(Uri publicBaseUri, A2aAuthenticationManifest authentication) => new()
    {
        ["name"] = "Drasi Sentinel",
        ["description"] =
            "Governed A2A assistant for subscriptions to curated Drasi continuous queries. " +
            "It provides curated, governed change subscriptions and current task-bound results.",
        ["version"] = "0.1.0",
        ["protocolVersion"] = "1.0",
        ["supportedInterfaces"] = new[]
        {
            new
            {
                url = new Uri(publicBaseUri, "/a2a/").ToString(),
                protocolBinding = "JSONRPC",
                protocolVersion = "1.0",
            },
        },
        ["capabilities"] = new { streaming = true, pushNotifications = false },
        ["defaultInputModes"] = new[] { "text/plain" },
        ["defaultOutputModes"] = new[] { "text/plain" },
        ["skills"] = new[]
        {
            new
            {
                id = "governed-change-subscriptions",
                name = "Governed change subscriptions",
                description = "Create immediate catalog-governed subscriptions that report on the A2A task stream.",
            },
            new
            {
                id = "task-subscription-lifecycle",
                name = "Task subscription lifecycle",
                description = "List, cancel, or retrieve current results for the curated subscription owned by the current conversation task.",
            },
        },
    };

}
