---
title: "Accounting"
linkTitle: "Accounting"
weight: 10
description: >
  One curated paid-invoice watch against local PostgreSQL.
---

This demo publishes one query, `invoice-paid`, and sets
`service.query_authoring.enabled` to `false`. A request that does not match
that skill does not start a watch.

PostgreSQL listens on `127.0.0.1:5432`. Inspector listens on
`http://127.0.0.1:8081/`.

```bash
cd examples/accounting-local-demo
cp .env.example .env
./demo.sh
```

In Inspector, click **Send streaming** and send:

```text
Tell me when an invoice is paid. Keep the watch for 7 days.
```

When the subscription is active, run the printed `docker exec` update. The
seeded unpaid invoice enters the result set. The artifact `diffType` is
`Add`.

`http://127.0.0.1:8082/` is a local capture URL a consuming agent can
register for that task. Until it does, `secrets/capture-events.jsonl` stays
empty.

The repository README at `examples/accounting-local-demo/README.md` has the
capture and current-results notes.
