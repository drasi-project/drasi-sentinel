---
title: "Building comfort"
linkTitle: "Building comfort"
weight: 20
description: >
  Author a watch over building, floor, and room rows.
---

This demo starts the PostgreSQL building, floor, and room source from the
[Drasi building comfort tutorial](https://drasi.io/drasi-server/tutorials/building-comfort/).
The catalog has no queries. `query_authoring.enabled` is `true`.

PostgreSQL listens on `127.0.0.1:5732`. Inspector listens on
`http://127.0.0.1:8085/`. Port `5432` belongs to the accounting demo.

```bash
cd examples/building-comfort-local-demo
cp .env.example .env
./demo.sh
```

Send one watch per task. A comfort level you define in the message is part
of the query, not a stored column. After the subscription is active, the
demo README prints the `docker exec` update that pushes one floor out of
band.

The committed query is `data/authored-queries/<id>.yaml`.
