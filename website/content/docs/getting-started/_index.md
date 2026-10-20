---
title: "Getting started"
linkTitle: "Getting started"
weight: 10
description: >
  Run the local accounting demo and receive one paid-invoice diff.
---

This tutorial runs one published watch. You will start the demo, ask for paid
invoices, mark a seeded invoice paid, and read the JSON artifact. This demo
has one published query, and authoring is off.

To run the host without this demo, [install the binary](install/).

## Prerequisites

- .NET 10 SDK. The [install page](install/) does not need it.
- Docker, with port `5432` free.
- `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL` set to an Azure OpenAI or Azure AI Foundry model. The host uses an Entra token, not an API key. Run `az login` first.

## Run the demo

```bash
git clone --recurse-submodules https://github.com/drasi-project/drasi-sentinel.git
cd drasi-sentinel
cd examples/accounting-local-demo
cp .env.example .env
```

Put the endpoint and model in `.env`, then start the demo:

```bash
./demo.sh
```

The script starts PostgreSQL, Sentinel, and the A2A Inspector. Inspector is at
`http://127.0.0.1:8081/`. The script prints the Agent Card URL. Paste that URL
into Inspector.

## Open the watch

After the card connects, click **Send streaming**. Send:

```text
Tell me when an invoice is paid. Keep the watch for 7 days.
```

Inspector shows a status update while the task is working, then a message that
the subscription is active. Keep that stream open.

## Cause one change

In another terminal, mark the seeded unpaid invoice paid:

```bash
docker exec drasi-sentinel-accounting-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d sentinel \
  -c "UPDATE invoices SET paid = true WHERE invoice_id = 'invoice-demo-001'"
```

That row enters the result set. Inspector shows an artifact update. The data
part has `type` `native_query_diff` and `diffType` `Add`. `data` is the row.
A text status update is not the change.

The message did not name the query. [Start a watch](../guides/start-a-watch/)
is what a message may contain.

## Next steps

- [Concepts](../concepts/) explains Add, Update, and Delete.
- [Keep the stream open](../guides/keep-the-stream/) covers reconnect and `Last-Event-ID`.
- [Ask for current results](../guides/current-results/) reads the rows already in the set.
