using System.Security.Claims;
using System.Text.Json;
using Drasi.Sentinel.Domain;
using Drasi.Sentinel.Host;

namespace Drasi.Sentinel.Tests;

public sealed class A2aStreamingTests
{
    [Fact]
    public void Inspector_streaming_requests_preserve_protobuf_user_parts()
    {
        using var document = JsonDocument.Parse("""
            {"jsonrpc":"2.0","id":"1","method":"SendStreamingMessage","params":{"message":{"role":"ROLE_USER","taskId":"task-1","messageId":"message-1","parts":[{"text":"Tell me about changes."}]}}}
            """);
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal(A2aStreamingMethod.SendStreamingMessage, request.Method);
        Assert.True(request.UsesInspectorV1);
        Assert.Equal("task-1", request.Message.TaskId);
    }

    [Fact]
    public void Follow_up_without_task_id_continues_the_context()
    {
        using var document = JsonDocument.Parse("""
            {"jsonrpc":"2.0","id":"2","method":"SendStreamingMessage","params":{"message":{"role":"ROLE_USER","contextId":"task-1","messageId":"msg-2","parts":[{"text":"vehicle location"}]}}}
            """);
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal("task-1", request.Message.TaskId);
        Assert.Equal("msg-2", request.Message.MessageId);
        Assert.Equal("vehicle location", request.Message.Text);
    }

    [Fact]
    public void Subscribe_to_task_accepts_inspector_method()
    {
        using var document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"SubscribeToTask","params":{"taskId":"task-1"}}""");
        var request = A2aMessageRequest.Parse(document.RootElement);
        Assert.Equal(A2aStreamingMethod.SubscribeToTask, request.Method);
        Assert.Equal("task-1", request.Message.TaskId);
    }

    [Fact]
    public void Agent_card_advertises_sse_streaming_json_rpc()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(AgentCard.Create(ServiceProfile.Demo, new A2aAnonymousManifest(), new Uri("http://localhost:5000/"), EmptyCatalog())));
        Assert.True(card.RootElement.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        Assert.Equal("JSONRPC", card.RootElement.GetProperty("supportedInterfaces")[0].GetProperty("protocolBinding").GetString());
    }

    [Fact]
    public void Agent_card_declares_a_single_standard_json_rpc_interface()
    {
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(AgentCard.Create(ServiceProfile.Demo, new A2aAnonymousManifest(), new Uri("http://localhost:5000/"), EmptyCatalog())));
        Assert.Single(card.RootElement.GetProperty("supportedInterfaces").EnumerateArray());
    }

    private static SafeCatalog EmptyCatalog() => new(
        new Dictionary<QueryId, QueryCatalogItem>(),
        new Dictionary<ReactionId, ReactionCatalogItem>());

    [Fact]
    public void Owner_derivation_remains_stable()
    {
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice"), new Claim("tenant", "contoso")], "Bearer"));
        Assert.Equal(A2aPrincipalFactory.CreateFromJwt(claims).Owner, A2aPrincipalFactory.CreateFromJwt(claims).Owner);
    }
}
