---
title: "Invoice overdue"
linkTitle: "Invoice overdue"
weight: 40
description: >
  A JWT-protected catalog with one overdue-invoice watch.
---

This example is not an anonymous demo. Copy `secrets/secrets.json.example`
to `secrets/secrets.json`, mode `0600`, and fill in the CDC password, CDC
port, and JWT public key. Set the Azure OpenAI variables, then run:

```bash
cd examples/invoice-overdue
dotnet run --project ../../src/Drasi.Sentinel.Host -- sentinel.yaml
```

Connect Inspector to the Agent Card, click **Send streaming**, and send:

```text
Tell me when an invoice is overdue. Keep the watch for 7 days.
```

Keep the stream open, or reconnect with `tasks/subscribe` and
`Last-Event-ID`. The caller must present the JWT the manifest expects.
