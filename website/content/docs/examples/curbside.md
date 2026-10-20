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

Click **Send streaming** and send one watch:

```text
Tell me when a pickup order is ready for pickup. Keep the watch for 7 days.
```

Wait until the subscription is active. Then mark the seeded order ready:

```bash
docker exec drasi-sentinel-curbside-local-demo psql -v ON_ERROR_STOP=1 -U sentinel -d retail \
  -c "UPDATE orders SET status = 'ready' WHERE plate = 'A1234'"
```

The ready task shows `diffType` `Add` for plate `A1234`.

A second watch is a new task. Wait until the first subscription is active,
then send:

```text
Tell me when an order is ready for pickup and the vehicle with the same plate is at the curb. Keep the watch for 7 days.
```

After that subscription is active, move the vehicle to the curb:

```bash
docker exec -e MYSQL_PWD=sentinel-demo drasi-sentinel-curbside-mysql mysql -usentinel physical \
  -e "UPDATE vehicles SET location = 'Curbside' WHERE plate = 'A1234'"
```

The first watch keeps its stream. If the agent asks which stored value a word
refers to, answer on the task that asked. The other prompts are in
`examples/curbside-local-demo/prompts.sh`.
