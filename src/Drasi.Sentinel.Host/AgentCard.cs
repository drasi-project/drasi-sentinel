using System.Reflection;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Host;

public static class AgentCard
{
    public static object Create(
        ServiceProfile profile,
        A2aAuthenticationManifest authentication,
        Uri publicBaseUri,
        SafeCatalog catalog,
        bool queryAuthoring = false) =>
        authentication switch
        {
            A2aJwtManifest => Protected(publicBaseUri, authentication, catalog, queryAuthoring),
            A2aAnonymousManifest when profile == ServiceProfile.Demo => Anonymous(publicBaseUri, catalog, queryAuthoring),
            A2aAnonymousManifest => throw new ArgumentException(
                "Anonymous A2A authentication is only valid for the demo profile.",
                nameof(profile)),
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        };

    private static object Protected(Uri publicBaseUri, A2aAuthenticationManifest authentication, SafeCatalog catalog, bool queryAuthoring)
    {
        var card = Card(publicBaseUri, catalog, queryAuthoring);
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

    private static object Anonymous(Uri publicBaseUri, SafeCatalog catalog, bool queryAuthoring) =>
        Card(publicBaseUri, catalog, queryAuthoring);

    private static Dictionary<string, object> Card(Uri publicBaseUri, SafeCatalog catalog, bool queryAuthoring) => new()
    {
        ["name"] = "Drasi Sentinel",
        ["description"] = queryAuthoring ? ConsumerContract + "\n\n" + AuthoringContract : ConsumerContract,
        ["version"] = ProductVersion,
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
        ["capabilities"] = new { streaming = true, pushNotifications = true },
        ["defaultInputModes"] = new[] { "text/plain" },
        ["defaultOutputModes"] = new[] { "text/plain", "application/json" },
        ["skills"] = Skills(catalog, queryAuthoring),
    };

    private static object[] Skills(SafeCatalog catalog, bool queryAuthoring)
    {
        var skills = catalog.Queries.Values
            .OrderBy(query => query.Id.Value, StringComparer.Ordinal)
            .Select(QuerySkill)
            .Cast<object>()
            .ToList();
        if (queryAuthoring)
        {
            skills.Add(new
            {
                id = "author-continuous-query",
                name = "Author continuous query",
                description = AuthoringSkillDescription,
                tags = new[] { "continuous-query", "authoring" },
                examples = new[] { "Tell me when rows match the condition I describe." },
                inputModes = new[] { "text/plain" },
                outputModes = new[] { "text/plain", "application/json" },
            });
        }

        skills.Add(new
        {
            id = "current-task-results",
            name = "Current task results",
            description = CurrentResultsDescription,
            tags = new[] { "task", "current-results" },
            examples = new[] { "What are the current results for this task?" },
            inputModes = new[] { "text/plain" },
            outputModes = new[] { "text/plain", "application/json" },
        });
        return [.. skills];
    }

    private static object QuerySkill(QueryCatalogItem query) => new
    {
        id = query.Id.Value,
        name = SkillName(query.Id.Value),
        description = $"{query.Description} {WatchNotice}",
        tags = new[] { "continuous-query", query.Id.Value },
        examples = query.Examples,
        inputModes = new[] { "text/plain" },
        outputModes = new[] { "text/plain", "application/json" },
    };

    private const string ConsumerContract =
        "One change watch is one durable A2A task and one curated subscription. A second watch needs a new task. If you send another watch on a task that already has a subscription, the host starts a new task and keeps the first watch. A mapping answer stays on the task that asked. The assistant asks how long to keep the watch when the message does not say. That answer is another message on the same task.\n\n" +
        "Read this description once, then pick a skill. A skill tagged continuous-query is one watch. Its description says what is watched. Its examples, when present, are utterances that select it. Send one example, or the description when the skill lists none, as the whole user text. Do not send the skill id, a reaction id, Cypher, a source name, a filter, a confirmation code, a destination, or a push URL. The host binds one curated reaction. You do not choose it.\n\n" +
        "The skill current-task-results is not a watch. Send its example only on the task a watch already opened. It returns the current rows for that task's query.\n\n" +
        "To manage every watch, POST method ListTasks or tasks/list with params {}. result.tasks lists each task id, status, and metadata.queryId for this caller. POST GetTask or tasks/get with params.id set to one of those ids. POST CancelTask or tasks/cancel with that id to end that watch. POST tasks/subscribe with that id to receive its diffs. A later request to detect a change is a new task. Do not send it on a task that already has a subscription.\n\n" +
        "POST JSON-RPC 2.0 to the url of the supportedInterfaces entry whose protocolBinding is JSONRPC. Use Content-Type application/json and Accept text/event-stream. capabilities.streaming is true and capabilities.pushNotifications is true. Streaming is the primary delivery. Use method message/stream to send a message. Use method tasks/subscribe to reconnect. Do not use message/send. To receive the same artifact updates without holding the stream, POST method tasks/pushNotificationConfig/set with that taskId and an absolute http or https url, or set configuration.taskPushNotificationConfig on the opening message/stream. That url belongs to that task only. The model does not choose it.\n\n" +
        "To open a watch, choose a taskId and a messageId. POST method message/stream. Set message.role to user, message.taskId to that taskId, and message.messageId to that messageId. message.parts must be one object whose mediaType is text/plain and whose text is one example from the chosen skill, or that skill's description when it lists no examples. Keep the response open for the life of the watch. While the host waits, it writes an SSE comment (`: ping`) at least every 15 seconds. A comment has no id and is not an event. Do not set a read timeout. Later changes arrive on this stream.\n\n" +
        "Each SSE event has an id line and a data line. The id is a decimal sequence. Keep the last id you applied. data is a JSON-RPC result. Read result.kind.\n\n" +
        "A status-update is not a change. An artifact-update is an output. A change is a part whose kind is data and whose data.type is native_query_diff. queryDescription is the catalog description of that query. diffType Add means the row entered the result set. diffType Update means a row still in the set changed. diffType Delete means the row left the result set. data is the row when present. before and after are present only when the host supplied them. Do not send another request to fill a missing field. Text parts are status, not changes.\n\n" +
        "If the stream drops, POST method tasks/subscribe with the same taskId. Set the Last-Event-ID header to the last SSE id you applied. Events after that id are replayed, then the stream waits. Omit the header only to replay from the start.\n\n" +
        "To read current rows, POST message/stream on the same taskId with a new messageId and the current-task-results example as the only text part. The output is an artifact-update data part whose type is current_query_results. It contains rows and queryDescription. It does not replace later native_query_diff events.";

    private const string AuthoringContract =
        "If no continuous-query skill matches the change you want, describe that change in plain language as the whole user text on a new task. Do not send Cypher, a source name, a filter, or a destination. The agent builds one query against the configured sources and opens one watch on that task.";

    private const string AuthoringSkillDescription =
        "This is a change watch for a condition that is not already a skill. Describe the change in plain language. The agent builds one query against the configured sources and opens one task. Do not send Cypher, a source name, a filter, or a destination. Follow the card description.";

    private const string WatchNotice =
        "This is a change watch, not a one-shot answer. Sending an example opens one task. Later changes arrive on that task as JSON artifacts. Follow the card description.";

    private const string CurrentResultsDescription =
        "This is not a second watch. Send an example on the task a watch already opened. The reply is the current rows for that task's query. Follow the card description.";

    private static string ProductVersion
    {
        get
        {
            var informational = typeof(AgentCard).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
                return "0.1.0-alpha.1";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    private static string SkillName(string id)
    {
        var words = id.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? id
            : string.Join(' ', words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
