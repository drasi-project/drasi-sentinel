using Drasi.Sentinel.Application;
using Drasi.Sentinel.Agent.Maf;
using Drasi.Sentinel.Host;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Drasi;

if (args.Length == 0)
{
    throw new ManifestLoadException("Pass the manifest .yaml or .yml path as the first argument.");
}

var manifest = SentinelManifestLoader.Load(args[0]);
var builder = WebApplication.CreateBuilder(args[1..]);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PostgresSentinelRepository>(_ =>
    PostgresSentinelRepository.Create(manifest.DatabaseConnectionString));
builder.Services.AddSingleton<IConversationRepository>(services =>
    services.GetRequiredService<PostgresSentinelRepository>());
builder.Services.AddSingleton<ITaskEventNotifier, TaskEventNotifier>();
builder.Services.AddSingleton(services => new NativeResultIngestion(
    services.GetRequiredService<IConversationRepository>(),
    services.GetRequiredService<ITaskEventNotifier>(),
    TimeProvider.System));
builder.Services.AddSingleton<INativeResultIngestion>(services =>
    services.GetRequiredService<NativeResultIngestion>());
builder.Services.AddSingleton(manifest.A2aAuthentication);
if (manifest.A2aAuthentication is A2aJwtManifest jwt)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options => A2aJwtAuthentication.Configure(options, jwt));
    builder.Services.AddAuthorization();
}

builder.Services.AddSingleton<DrasiNativeEngine>(services => new DrasiNativeEngine(
    new DrasiTopology(
        "drasi-sentinel",
        manifest.Directory,
        manifest.ExecutableCatalog.Sources.Values
            .Select(source => new DrasiPluginSourceDefinition(
                source.Id,
                source.Kind,
                source.Configuration,
                source.Bootstrap))
            .ToArray(),
        manifest.ExecutableCatalog.Queries.Values
            .Select(query => new DrasiQueryDefinition(query.Id, query.Cypher, query.SourceIds))
            .ToArray()),
    services.GetRequiredService<INativeResultIngestion>()));
builder.Services.AddSingleton<INativeEngine>(services =>
    services.GetRequiredService<DrasiNativeEngine>());
builder.Services.AddSingleton<IConversationSubscriptions>(services => new ConversationSubscriptions(
    manifest.SafeCatalog,
    services.GetRequiredService<INativeEngine>(),
    services.GetRequiredService<IConversationRepository>(),
    services.GetRequiredService<ITaskEventNotifier>(),
    TimeProvider.System));
if (manifest.Agent is { } agent)
{
    builder.Services.AddSingleton<ILanguageModel>(_ => MafLanguageModel.Create(
        agent.Url,
        agent.Model,
        manifest.Profile == ServiceProfile.Demo ? ModelCredentialKind.AzureCli : ModelCredentialKind.DefaultAzure));
    builder.Services.AddSingleton<IAgentResponder>(services => new CatalogAgentResponder(
        manifest.SafeCatalog,
        services.GetRequiredService<IConversationSubscriptions>(),
        services.GetRequiredService<ILanguageModel>()));
}
else
{
    builder.Services.AddSingleton<IAgentResponder, UnconfiguredAgentResponder>();
}

var app = builder.Build();
var engine = app.Services.GetRequiredService<INativeEngine>();
if (manifest.A2aAuthentication is A2aJwtManifest)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
await app.Services.GetRequiredService<PostgresSentinelRepository>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
_ = app.Services.GetRequiredService<NativeResultIngestion>()
    .RunAsync(app.Lifetime.ApplicationStopping);
await engine.StartAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    () => engine.DisposeAsync().AsTask().GetAwaiter().GetResult());

app.MapGet("/", () => Results.Ok(new { service = "Drasi Sentinel .NET", status = "ready" }));
IResult AgentCardResponse(HttpRequest request) =>
    Results.Json(
        AgentCard.Create(
            manifest.Profile,
            manifest.A2aAuthentication,
            new Uri($"{request.Scheme}://{request.Host}/"),
            manifest.SafeCatalog));
app.MapGet("/.well-known/agent-card.json", AgentCardResponse);
app.MapGet("/a2a/.well-known/agent-card.json", AgentCardResponse);
var a2a = app.MapPost("/a2a/", A2aEndpoint.SendAsync);
if (manifest.A2aAuthentication is A2aJwtManifest)
{
    a2a.RequireAuthorization();
}
app.MapGet("/healthz", () => Results.Ok(new { status = "ready" }));

app.Run();

public partial class Program;
