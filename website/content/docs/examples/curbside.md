---
title: "Curbside"
linkTitle: "Curbside"
weight: 30
description: >
  Author watches across a PostgreSQL orders source and a MySQL vehicles source.
---

Orders live in PostgreSQL on `127.0.0.1:5433`. Vehicles live in MySQL on
`127.0.0.1:3309`. The catalog has no queries. The agent builds each watch
from a prompt, one task at a time. Inspector is at `http://127.0.0.1:8083/`.

```bash
cd examples/curbside-local-demo
./demo.sh
```

Click **Send streaming**. Wait until the subscription is active before you
send the next watch. A later watch request in the same Inspector session
starts another task. The first watch keeps its stream. A mapping answer
stays on the task that asked.

The repository README at `examples/curbside-local-demo/README.md` lists the
prompts and the row updates.
