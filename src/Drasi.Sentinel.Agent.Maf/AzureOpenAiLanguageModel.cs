using Azure.Identity;
using Drasi.Sentinel.Application;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Drasi.Sentinel.Agent.Maf;

internal sealed class AzureOpenAiLanguageModel : ILanguageModel, IDisposable
{
    private readonly OpenAIClient _client;
    private readonly string _model;

    public AzureOpenAiLanguageModel(Uri endpoint, string model, ModelCredentialKind kind)
    {
        _model = model;
#pragma warning disable OPENAI001
        _client = new OpenAIClient(
            Credential(kind),
            new OpenAIClientOptions { Endpoint = NormalizeEndpoint(endpoint) });
#pragma warning restore OPENAI001
    }

    public async Task<string> CompleteAsync(
        string instructions,
        IAgentTools tools,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(messages);
#pragma warning disable OPENAI001
        var agent = _client.GetResponsesClient().AsAIAgent(
            model: _model,
            name: "DrasiSentinel",
            instructions: instructions,
            tools: tools.Tools.Select(tool => (AITool)new SessionFunction(tools, tool)).ToList());
#pragma warning restore OPENAI001
        try
        {
            return ModelReply.SingleCopy((await agent.RunAsync(ToChat(messages), session: null, options: null, cancellationToken)).ToString());
        }
        catch (AuthenticationFailedException exception)
        {
            throw new AgentFailureException(
                AgentFailure.CredentialUnavailable,
                "The configured agent credential is unavailable.",
                exception);
        }
        catch (ClientResultException exception)
        {
            throw new AgentFailureException(
                AgentFailure.ProviderUnavailable,
                "The configured agent endpoint or model is unavailable.",
                exception);
        }
    }

    public void Dispose() => (_client as IDisposable)?.Dispose();

    private static IList<ChatMessage> ToChat(IReadOnlyList<AgentMessage> messages) =>
        messages.Select(message => new ChatMessage(ToRole(message.Role), message.Text)).ToList();

    private static ChatRole ToRole(AgentRole role) => role switch
    {
        AgentRole.Assistant => ChatRole.Assistant,
        AgentRole.User => ChatRole.User,
        _ => throw new InvalidOperationException($"Unsupported agent role '{role}'."),
    };

    private static BearerTokenPolicy Credential(ModelCredentialKind kind) => kind switch
    {
        ModelCredentialKind.AzureCli => new BearerTokenPolicy(
            new AzureCliCredential(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) }),
            "https://ai.azure.com/.default"),
        _ => new BearerTokenPolicy(
            new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeAzureCliCredential = true,
                ExcludeInteractiveBrowserCredential = true,
            }),
            "https://ai.azure.com/.default"),
    };

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        const string suffix = "/chat/completions";
        var path = endpoint.AbsolutePath;
        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
        return new UriBuilder(endpoint) { Path = path.TrimEnd('/') + "/" }.Uri;
    }
}
