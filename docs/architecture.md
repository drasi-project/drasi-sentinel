# How the host works

This page is for someone changing the .NET host. Caller-facing behavior is on the documentation site. The rules that must survive a change are in `AGENTS.md`.

## Executive overview

Sentinel is one process. It loads one YAML manifest, opens one Drasi engine, and serves one A2A JSON-RPC endpoint. A caller describes a change in text. The host binds one query and the reaction already listed for that query, then keeps that pair as one watch.

A watch is one `ConversationTask` owned by one caller, with one `ConversationSubscription`. Later messages do not add a second subscription to that task. A second watch is a new task.

Two paths meet in `data/journal.sqlite`.

The caller path is a model turn. `A2aEndpoint` accepts `message/stream`. `CatalogAgentResponder` asks `ILanguageModel` to call a governed tool. A new subscription is created only by that tool. Restart reattaches a subscription that is already in the journal. The model does not see source passwords, and it does not choose a destination.

The diff path does not involve the model. The Drasi engine emits a query result. `NativeResultIngestion` writes one JSON artifact to the task journal, then wakes SSE readers. A push URL, if the caller registered one on that task, is posted after that write. A failed post does not remove the journal row.

Streaming is the delivery. `tasks/subscribe` with `Last-Event-ID` replays the journal and then waits. Do not add a second transport as the primary contract.

The split that matters: `Drasi.Sentinel.Application` owns the turn, the tools, and the journal. `Drasi.Sentinel.Agent.Maf` is the only model client, and Application does not reference it. A new provider implements `ILanguageModel`. It does not grow a new way to open a watch.

## Four projects

`Drasi.Sentinel.Domain` has the task, subscription, and event types. It has no I/O.

`Drasi.Sentinel.Application` references Domain. It owns `CatalogAgentResponder`, `AgentTurn`, `AgentToolSession`, `ConversationSubscriptions`, `NativeResultIngestion`, `QueryAuthoring`, and `SqliteSentinelRepository`.

`Drasi.Sentinel.Agent.Maf` references Application. `MafLanguageModel.Create` returns the only `ILanguageModel`. That implementation is `AzureOpenAiLanguageModel`.

`Drasi.Sentinel.Host` references Application and Agent.Maf. `Program.cs` is the composition root. It loads the manifest, constructs the engine, and maps HTTP. Host code may catch `AgentFailureException`. Application code must not reference the Maf assembly.

`src/` stays domain-neutral. Invoice, room, and vehicle words belong under `examples/` and example fixtures.

## What starts before the process listens

`Program.cs` does this in order, and the order is the point.

1. Load the manifest. If the path is missing, the process throws and exits.
2. Open `data/journal.sqlite` and initialize it.
3. Start the `NativeResultIngestion` loop. It is a single reader over an unbounded channel.
4. Start the native engine, but not the source feeds.
5. `WatchExpiry.SweepAsync` expires watches that are already due.
6. `RestoreAuthoredQueriesAsync` loads authored Cypher from the journal, provisions each query on the engine, and adds it to `LiveCatalog`. Files under `data/authored-queries/` are rewritten from that journal. The engine does not load Cypher from those files.
7. `ConversationSubscriptions.RestoreWatchesAsync` reattaches subscriptions whose state is `Arming`, `Active`, or `Degraded`. A non-active one is saved as `Active`, then attached. A failed attach is marked `Degraded` and journaled. Cancelled and expired watches are not in that list.
8. `StartFeedsAsync` starts the sources. A source that started earlier could emit a change before the watch was attached.

The Agent Card is `/.well-known/agent-card.json`. It is built from `LiveCatalog` and the authoring flag, not from a static file.

## A message becomes a watch

`A2aEndpoint.SendAsync` is the JSON-RPC entry. JWT authentication, when `service.a2a.authentication.mode` is `jwt`, wraps that route only.

`WatchTurn.ContinuesOccupiedTask` decides whether the text stays on a task that already has a subscription. A detection sentence, one that contains when, whenever, once, until, while, or if, does not stay. The host opens a new task and leaves the first watch running. A short affirmation, a lifetime token, or a task-management sentence stays. That is how a follow-up answer reaches the task that asked.

`CatalogAgentResponder.RespondAsync` renders instructions with `AgentTurn.Render`, builds tools with `AgentToolSession.Create`, and calls `ILanguageModel.CompleteAsync`. The model can call `GetCurrentTaskResults`. That tool takes no query id and no filter. It resolves the caller's subscription on that task and appends a `current_query_results` artifact. A task the caller does not own has no subscription to resolve.

A published query with zero reactions, or with more than one, does not load. An unknown query is a tool result. It does not fail the task.

If the manifest has no `agent` block, the host registers `UnconfiguredAgentResponder`. A message then fails with `A2aAgentUnconfiguredException`.

## A diff becomes an artifact

`INativeResultIngestion.Enqueue` accepts a `NativeQueryDiff` from the engine. The loop calls `IngestAsync`.

`IngestAsync` looks up the watch by native subscription id. An unknown id throws. A cancelled, revoked, expired, or due watch is dropped. Any other state except `Arming`, `Active`, or `Degraded` throws.

The artifact type is `native_query_diff`. The JSON keeps `diffType`, `data`, `before`, `after`, `groupingKeys`, and `metadata` when the engine supplied them. Null fields are omitted. The host adds `subscriptionId`, `queryId`, `queryDescription`, `reactionId`, `occurredAt`, `querySequence`, and `diffOrdinal`.

`AppendDiffOnceAsync` stores a receipt of `querySequence:diffOrdinal` in the same transaction as the event. A repeat of that receipt commits nothing. A diff with no `querySequence` has no receipt. Deleting `data/indexes` while keeping the journal can emit a new sequence, and the old receipt does not collapse that emission.

After the insert, `ITaskEventNotifier.Notify` wakes SSE readers. Readers replay journal rows after `Last-Event-ID`, then wait, and re-read storage after each wake. While a stream waits, the host writes an SSE comment, `: ping`, at least every 15 seconds. That comment is not a journal row and has no id.

Push runs only after the journal insert. `HttpPushNotification` posts the artifact to the URL stored for that task. The model cannot set that URL. A failed post is swallowed. The artifact remains in the journal.

## Authoring is a second way to get the query

`service.query_authoring.enabled` must be true, and the manifest must contain exactly one reaction. `Program.cs` registers `QueryAuthoring` and `QueryWorkbench` only in that case. The reaction id passed to `QueryAuthoring` is `SafeCatalog.Reactions.Keys.Single()`.

The model can sample schema and probe a query through the workbench. `QueryText` and `QueryAuthoring` reject shapes the watch must not commit. A filter that matches no current rows can still be committed when that filter is the condition the caller asked to watch. The committed query is stored in the journal, registered on the engine, and then subscribed like a published query.

The model still cannot create a source or read a connection password. Source ids on the prompt are the ids from the manifest, not connection strings.

Instructions the model sees are `AuthoringPreamble`, `AuthoringBody`, and `LifetimeRule` inside `AgentTurn.Render`. Change those strings in `AgentTurn.cs`, not in `AgentCard.cs`. The fixture eval and the model eval are in `docs/query-authoring-eval.md`.

## The model client

`AzureOpenAiLanguageModel` calls the OpenAI Responses API. It does not call chat completions. A URL that ends in `/chat/completions` loses that suffix before the client is built.

The credential is an Entra token for `https://ai.azure.com/.default`. `service.profile` of `demo` uses `AzureCliCredential`. Any other profile uses `DefaultAzureCredential`, and that path excludes the Azure CLI. There is no API-key constructor. A generic OpenAI-compatible server will not authenticate.

`CompleteAsync` maps `AuthenticationFailedException` to `AgentFailure.CredentialUnavailable` and `ClientResultException` to `AgentFailure.ProviderUnavailable`. Both become `AgentFailureException`. The host handles that exception. The application does not catch provider types.

## Where to change it

| Change | Start here |
| --- | --- |
| What the model is told | `AgentTurn.Render` in `src/Drasi.Sentinel.Application/AgentTurn.cs` |
| Which tools the model can call | `AgentToolSession` and `QueryWorkbench` |
| How a subscription is created or restored | `ConversationSubscriptions` in `SentinelCommands.cs` |
| The diff JSON | `NativeResultIngestion.IngestAsync` |
| SSE and JSON-RPC mapping | `A2aEndpoint` and `A2aProtocol` |
| Whether a later message stays on the task | `WatchTurn.ContinuesOccupiedTask` |
| Manifest fields | `SentinelManifestLoader` |
| The Azure client | `AzureOpenAiLanguageModel` |
| Authoring eval | `docs/query-authoring-eval.md` |

Run `dotnet test Drasi.Sentinel.slnx` from the repository root after a change in `src/`.
