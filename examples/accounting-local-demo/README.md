# Local accounting demonstration

Run `./demo.sh` after setting the Azure OpenAI endpoint and model. It starts
the local PostgreSQL CDC source, Sentinel, and the forked generic A2A
Inspector at `http://127.0.0.1:8081`. Enter the printed Agent Card URL in the
browser UI.

The demo binds PostgreSQL only to `127.0.0.1:5432`. It uses the local
`sentinel` user for both Sentinel state and CDC, and creates owner-only
`secrets/postgres.connection-string` and `secrets/postgres.password` files
for the manifest. If port 5432 is occupied, stop its owner before starting
the demo.

In Inspector send a message and ask:

```text
Tell me when an invoice is paid.
```

The manifest sets `service.query_authoring.enabled` to true, so a description
that does not match that skill can become a new query against the configured
source. The agent reads the schema, samples real values, tests the query, and
can diagnose the source or query before it opens one watch. It does not see
the database password, and it cannot add a source or insert rows.

The card exposes standard JSON-RPC/SSE. After the card connects, click
**Send streaming** to send this message with standard A2A
`SendStreamingMessage`. Inspector renders each task-status and artifact
update. To reconnect to a known working task, click **Resume task stream**,
which uses standard A2A `SubscribeToTask`. When the demo tells you that the
subscription is active, run its printed
`docker exec ... UPDATE invoices` command in another terminal. That update
marks the seeded unpaid invoice paid, which adds it to the result set, and
drives PostgreSQL CDC. Each native result diff
is delivered as an A2A JSON data artifact, retaining its type and raw JSON
data, before/after values, grouping keys, and metadata when supplied. The
same JSON is posted to `http://127.0.0.1:8082/` and appended to
`secrets/webhook-events.jsonl`. That post is optional and is not a second
watch. You can
also run the standard streaming test client from the repository root:

```bash
./tests/a2a-streaming-client.sh
```

Before and after the PostgreSQL updates, send a new message on the same task:

```text
What are the current results for this task?
```

Inspector displays a `current_query_results` data artifact containing the task,
subscription, curated query, and its current rows. Do not provide an asset ID,
source, Cypher, or filter.
