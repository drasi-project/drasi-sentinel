# Drasi Sentinel

Sentinel is an A2A agent. You describe a change in plain language. It binds
one query to one durable task and reports later result changes as JSON
artifacts on that task.

The host either published that query as a skill, or it writes the query when
authoring is on. You do not choose that in the message. The manifest does.

This is [v0.1.0-alpha.1](https://github.com/drasi-project/drasi-sentinel/releases/tag/v0.1.0-alpha.1).
The host contract can still change. The
[documentation site](https://drasi-project.github.io/drasi-sentinel/)
is the full guide.

## Install the host

Download the archive for your machine from the alpha release. You do not need
the .NET SDK. The binary needs a YAML manifest before it can start. The
steps, the file, and the run command are in
[Install the host](https://drasi-project.github.io/drasi-sentinel/docs/getting-started/install/).

The model endpoint must be Azure OpenAI or Azure AI Foundry. The host uses an
Entra token and the Responses API. It does not accept an API key or a generic
OpenAI-compatible server.

The process prints a URL. The Agent Card is `/.well-known/agent-card.json`
on that URL.

## Open a watch

If the host published skills, send one skill example, or its description, as
the whole user text. Include how long to keep the watch. The call is
`message/stream`. [Start a watch](https://drasi-project.github.io/drasi-sentinel/docs/guides/start-a-watch/)
is the procedure, including what the message must not contain.

If no skill covers the change, and the host has authoring on, describe the
change instead. [Author a query](https://drasi-project.github.io/drasi-sentinel/docs/guides/author-a-query/)
is that path.

Keep the response open. A second watch is a new task.

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
[Keep the stream open](https://drasi-project.github.io/drasi-sentinel/docs/guides/keep-the-stream/)
covers idle streams and reconnect. A host restart closes the old response and
does not end the watch.

