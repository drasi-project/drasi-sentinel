# Drasi Sentinel

Sentinel is an A2A agent. You describe a supported change in plain language.
It binds one curated query to one durable task and reports later result
changes as JSON artifacts on that task.

This is [v0.1.0-alpha.1](https://github.com/drasi-project/drasi-sentinel/releases/tag/v0.1.0-alpha.1).
The host contract can still change. The
[documentation site](https://drasi-project.github.io/drasi-sentinel/)
is the full guide.

## Install the host

Download the archive for your machine from the alpha release. You do not need
the .NET SDK. The steps, file names, and run command are in
[Install the host](https://drasi-project.github.io/drasi-sentinel/docs/getting-started/install/).

The first argument is your manifest. The Agent Card is
`/.well-known/agent-card.json` on the URL the process prints.

## Open a watch

Read the Agent Card once. A skill tagged `continuous-query` is one watch.
Send one of its examples, or its description when it lists none, as the whole
user text. Include how long to keep the watch, such as `7d` or `none`.

Use `message/stream`. Do not send a query id, Cypher, a source name, a
filter, or a destination. The host binds the reaction. You do not choose it.

Keep that response open. An idle stream stays open for the life of the watch.
The host writes an SSE comment, `: ping`, at least every 15 seconds. A
comment is not an event. Do not set a read timeout.

A second watch is a new task.

## Read a change

A change is an artifact whose `type` is `native_query_diff`.

- `diffType` `Add` means the row entered the result set.
- `diffType` `Update` means a row still in the set changed.
- `diffType` `Delete` means the row left the result set.

A status update is not a change. To read the rows already in the set, send
`What are the current results for this task?` on that same task.

## If the stream drops

POST `tasks/subscribe` with the same task id. Set `Last-Event-ID` to the last
SSE id you applied. The host replays events after that id, then waits.

A host restart closes the old HTTP response. It does not end the watch.
Reconnect the same way. [Host restart](https://drasi-project.github.io/drasi-sentinel/docs/concepts/restart/)
says what the journal keeps.

Streaming is the delivery. A push URL is optional, and it belongs to that
task only.

