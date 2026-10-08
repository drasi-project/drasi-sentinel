# A2A streaming architecture

`ConversationTask` is the durable owner-scoped aggregate. A task owns one
active `ConversationSubscription` and an ordered event journal.
`ConversationSubscriptions` binds the one allowed reaction for a model-selected
query, attaches the native reaction, and appends a subscription artifact. A
query with zero or many reactions does not load. An unknown query returns a
tool result and does not fail the task. The application owns the turn
instructions, governed tools, and `ILanguageModel` port. A model provider
implements that port. The host catches `AgentFailureException` and does not
depend on the provider assembly for the turn.

`GetCurrentTaskResults` takes no asset identifiers or filters. It finds the
caller's task-owned subscription, obtains current rows for that subscription's
curated query through `INativeEngine`, and appends a `current_query_results`
JSON artifact with the task, subscription, query, and rows. A task not owned by
the caller has no resolvable subscription.

Native results enter through `INativeResultIngestion`. It rejects inactive or
unknown subscriptions, then appends one JSON artifact for every native query
diff without filtering or deduplicating it. Artifacts preserve the native diff
type, raw JSON data, before/after values, grouping keys, and metadata when
supplied, plus the subscription, query, reaction, and occurrence time. The
journal is the delivery mechanism. If the manifest sets
`service.webhook.url`, the same native diff JSON is also posted there after
the journal write. A failed post does not drop the artifact or fail the watch.
The webhook is operator configuration, not a model-selected destination.

The Host maps journal entries to JSON-RPC SSE events for
`SendStreamingMessage` and `SubscribeToTask`. Readers replay entries after
`Last-Event-ID` before waiting for the notifier and always re-read durable
storage after waking. Native diff artifacts become A2A data parts, while
protocol status messages remain text. Current-result artifacts use the same
A2A data-part projection. A2A transport code does not leak into application or
native code.
