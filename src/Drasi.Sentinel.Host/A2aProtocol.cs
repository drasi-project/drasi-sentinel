using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Host;

public sealed class A2aProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record A2aUserMessage(string TaskId, string MessageId, string Text);

public enum A2aStreamingMethod { SendMessage, SendStreamingMessage, SubscribeToTask }

public sealed record A2aMessageRequest(
    JsonElement RequestId,
    A2aUserMessage Message,
    bool UsesInspectorV1,
    A2aStreamingMethod Method,
    PushNotificationConfig? PushConfig = null)
{
    public static A2aMessageRequest Parse(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            !StringComparer.Ordinal.Equals(version.GetString(), "2.0"))
        {
            throw new A2aProtocolException("invalid_request", "The request must be JSON-RPC 2.0.");
        }

        if (!request.TryGetProperty("id", out var requestId) ||
            requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new A2aProtocolException("invalid_request", "The JSON-RPC request id must be a string or number.");
        }

        if (!request.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String ||
            !TryMethod(method.GetString(), out var parsedMethod))
        {
            throw new A2aProtocolException("method_not_found", "The A2A method is not supported.");
        }

        var parameters = RequireObject(request, "params", "The A2A params must be an object.");
        if (parsedMethod == A2aStreamingMethod.SubscribeToTask)
        {
            var subscribedTaskId = OptionalString(parameters, "taskId") ?? OptionalString(parameters, "id")
                ?? throw new A2aProtocolException("invalid_params", "SubscribeToTask requires taskId.");
            return new A2aMessageRequest(
                requestId.Clone(),
                new A2aUserMessage(subscribedTaskId, $"subscribe:{subscribedTaskId}", string.Empty),
                StringComparer.Ordinal.Equals(method.GetString(), "SubscribeToTask"),
                parsedMethod);
        }
        var message = RequireObject(parameters, "message", "The message/send request must include a message.");
        if (!IsUserRole(RequiredString(message, "role")))
        {
            throw new A2aProtocolException("invalid_params", "Only user messages are accepted.");
        }

        var messageId = RequiredString(message, "messageId");
        var taskId = OptionalString(message, "taskId") ?? messageId;
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 1)
        {
            throw new A2aProtocolException("invalid_params", "A user message must contain exactly one text/plain part.");
        }

        var part = parts[0];
        if (part.ValueKind != JsonValueKind.Object ||
            !IsTextPart(part) ||
            !IsTextPlain(part))
        {
            throw new A2aProtocolException("invalid_params", "A user message must contain exactly one text/plain part.");
        }

        var text = RequiredString(part, "text");
        return new A2aMessageRequest(
            requestId.Clone(),
            new A2aUserMessage(taskId, messageId, text),
            StringComparer.Ordinal.Equals(method.GetString(), "SendMessage") ||
            StringComparer.Ordinal.Equals(method.GetString(), "SendStreamingMessage"),
            parsedMethod,
            A2aPushNotification.ReadMessageConfig(parameters));
    }

    private static bool TryMethod(string? method, out A2aStreamingMethod parsed)
    {
        if (StringComparer.Ordinal.Equals(method, "message/send") || StringComparer.Ordinal.Equals(method, "SendMessage"))
        {
            parsed = A2aStreamingMethod.SendMessage;
            return true;
        }
        if (StringComparer.Ordinal.Equals(method, "message/stream") || StringComparer.Ordinal.Equals(method, "SendStreamingMessage"))
        {
            parsed = A2aStreamingMethod.SendStreamingMessage;
            return true;
        }
        if (StringComparer.Ordinal.Equals(method, "tasks/subscribe") || StringComparer.Ordinal.Equals(method, "SubscribeToTask"))
        {
            parsed = A2aStreamingMethod.SubscribeToTask;
            return true;
        }
        parsed = default;
        return false;
    }

    private static bool IsUserRole(string role) =>
        StringComparer.Ordinal.Equals(role, "user") ||
        StringComparer.Ordinal.Equals(role, "ROLE_USER");

    private static bool IsTextPart(JsonElement part)
    {
        if (part.TryGetProperty("kind", out var kind))
        {
            return kind.ValueKind == JsonValueKind.String &&
                StringComparer.Ordinal.Equals(kind.GetString(), "text");
        }

        return part.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String;
    }

    private static JsonElement RequireObject(JsonElement objectNode, string propertyName, string message)
    {
        if (!objectNode.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new A2aProtocolException("invalid_params", message);
        }

        return property;
    }

    private static string RequiredString(JsonElement objectNode, string propertyName)
    {
        if (!objectNode.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new A2aProtocolException("invalid_params", $"{propertyName} must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement objectNode, string propertyName) =>
        objectNode.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static bool IsTextPlain(JsonElement part)
    {
        foreach (var name in new[] { "mimeType", "mediaType", "contentType" })
        {
            if (part.TryGetProperty(name, out var mediaType) &&
                (mediaType.ValueKind != JsonValueKind.String ||
                 !StringComparer.OrdinalIgnoreCase.Equals(mediaType.GetString(), "text/plain")))
            {
                return false;
            }
        }

        return true;
    }
}

public static class A2aPrincipalFactory
{
    private static readonly Principal AnonymousPrincipal = new(
        new TenantId("anonymous"),
        new OwnerId("a2a-anonymous"));

    public static Principal Create(
        ClaimsPrincipal claimsPrincipal,
        A2aAuthenticationManifest authentication) =>
        authentication switch
        {
            A2aJwtManifest => CreateFromJwt(claimsPrincipal),
            A2aAnonymousManifest => AnonymousPrincipal,
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        };

    public static Principal CreateFromJwt(ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.Identity?.IsAuthenticated is not true)
        {
            throw new A2aProtocolException("unauthorized", "A valid bearer identity is required.");
        }

        var subject = RequiredClaim(claimsPrincipal, "sub");
        var tenant = RequiredClaim(claimsPrincipal, "tenant");
        return new Principal(
            new TenantId(tenant),
            new OwnerId(DeriveOwnerId(tenant, subject)));
    }

    private static string RequiredClaim(ClaimsPrincipal claimsPrincipal, string claimType)
    {
        var value = claimsPrincipal.FindFirst(claimType)?.Value
            ?? (claimType == "sub" ? claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value : null);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new A2aProtocolException("missing_required_claim", $"The bearer token must contain a non-empty {claimType} claim.");
        }

        return value;
    }

    private static string DeriveOwnerId(string tenant, string subject)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant.Length}:{tenant}{subject.Length}:{subject}"));
        return $"a2a-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
