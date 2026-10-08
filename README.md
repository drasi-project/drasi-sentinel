# Drasi Sentinel for .NET

Sentinel is a governed A2A change-detection agent. Callers describe a supported
change in natural language. The bounded agent selects a curated query/reaction
pair, creates an active task-owned subscription, and reports later observations
on the same A2A task. Consumers can also ask for current results on that task.
The agent resolves the task's sole subscription and its curated query internally;
the request accepts no query, source, Cypher, or filter input.

The JSON-RPC A2A interface supports `SendMessage` for Inspector v1
compatibility and the standard SSE methods `SendStreamingMessage` and
`SubscribeToTask`. Streaming responses use durable task-event sequence IDs as
SSE IDs. Reconnect with `Last-Event-ID` to replay missed artifacts.

```bash
dotnet test Drasi.Sentinel.slnx
dotnet run --project src/Drasi.Sentinel.Host -- sentinel.yaml
```

The Agent Card is available at `/.well-known/agent-card.json`. It advertises
`JSONRPC` with `capabilities.streaming: true`. Inspector v1 `ROLE_USER`
messages and protobuf-style text parts remain supported.

## Persistence

PostgreSQL stores owner-scoped conversation tasks, task-owned subscriptions,
and append-only task events. Every native query diff appends an A2A JSON data
artifact to that journal. If `service.webhook.url` is set, the same JSON is
also posted to that URL after the journal write. Each artifact retains the native diff type, raw JSON
data, before/after values, grouping keys, and metadata when supplied, alongside
its subscription, query, reaction, and occurrence time.

Current-result requests append a `current_query_results` A2A JSON data artifact
to the same task journal. It contains `taskId`, `subscriptionId`, `queryId`, and
the native query `rows`.

## Manifest

Queries and reactions are curated assets. A natural-language request selects
only one approved query/reaction pair; source configuration and Cypher stay
private to the host.

```yaml
queries:
  - id: overdue-invoices
    description: Finds open invoices whose due date has passed.
    cypher: MATCH (invoice:Invoice) RETURN invoice.id AS invoice_id
    sources: [invoices]
    reactions: [notify-account]
reactions:
  - id: notify-account
```
