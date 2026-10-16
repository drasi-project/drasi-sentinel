# Drasi Sentinel for .NET

Sentinel is a governed A2A change-detection agent. Callers describe a supported
change in natural language. The bounded agent selects a curated query/reaction
pair, creates an active task-owned subscription, and reports later observations
on the same A2A task. If the manifest enables query authoring, the agent can
also build a query against the configured sources and then subscribe to it.
Consumers can also ask for current results on that task.
The agent resolves the task's sole subscription and its curated query internally;
the request accepts no query, source, Cypher, or filter input.

The JSON-RPC A2A interface supports `SendMessage` for Inspector v1
compatibility and the standard SSE methods `SendStreamingMessage` and
`SubscribeToTask`. Streaming responses use durable task-event sequence IDs as
SSE IDs. Reconnect with `Last-Event-ID` to replay missed artifacts.

```bash
dotnet test Drasi.Sentinel.slnx
./tests/query-authoring-eval.sh
dotnet run --project src/Drasi.Sentinel.Host -- sentinel.yaml
```

`./tests/query-authoring-eval.sh` scores the query tools without a model. To
score the configured model, and to change the authoring instructions from a
failure, see [Tune query authoring from the eval](docs/query-authoring-eval.md).

The Agent Card is available at `/.well-known/agent-card.json`. It advertises
`JSONRPC` with `capabilities.streaming: true`. Inspector v1 `ROLE_USER`
messages and protobuf-style text parts remain supported.

## Persistence

The host stores conversation tasks, subscriptions, authored queries, and
append-only task events in `data/journal.sqlite`. Each committed authored
query is also written to `data/authored-queries/<id>.yaml`, with its id,
description, Cypher, sources, and joins. The agent does not repeat that
Cypher on the task. The Drasi engine stores query
indexes in RocksDB and plugin state in a redb file under the same `data/`
directory, next to the manifest. A query probe deletes its index when the
probe finishes. The next process start removes a probe index left by a crash.
`service.max_ttl` is the longest lifetime a caller may choose. Omit it,
or set `none`, for no cap. The agent asks how long to keep a watch when the
message does not say. A due watch is detached and marked expired. A due
authored query is removed only when no live watch still uses it.
The source write-ahead log is `data/wal`.
It does not resume a Postgres or MySQL source. Set `service.data` to another
directory, relative to the manifest or absolute, when those files should live
elsewhere. A Postgres source keeps its read position in the replication slot
named by that source. The state file stays locked until the process exits.
Exit the process before starting another against the same data directory. A new
process reopens the indexes, reattaches each watch that has not ended on its
stored reaction id, and then starts sources. A change the source or the query
outbox still holds is written to that watch's journal.

Every native query diff appends an A2A JSON data artifact to that journal. A consuming
agent may register one push notification URL for that task with
`tasks/pushNotificationConfig/set`, or on the opening `message/stream` as
`configuration.taskPushNotificationConfig`. Each later diff is posted to that
URL as an `artifactUpdate`. A failed post does not drop the journaled artifact.
Streaming remains the primary delivery, and the model does not choose the URL.
Each artifact retains the native diff type, raw JSON
data, before/after values, grouping keys, and metadata when supplied, alongside
its subscription, query, reaction, and occurrence time. `queryDescription` is the catalog description of that query.

Current-result requests append a `current_query_results` A2A JSON data artifact
to the same task journal. It contains `taskId`, `subscriptionId`, `queryId`,
`queryDescription`, and the native query `rows`.

## Manifest

Queries and reactions are curated assets. A natural-language request selects
only one approved query/reaction pair; source configuration and Cypher stay
private to the host. Set `service.query_authoring.enabled` to `true` only when
the manifest has exactly one reaction. The agent may then create queries against
the declared sources. It still cannot create sources, choose a destination, or
see connection settings.

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
