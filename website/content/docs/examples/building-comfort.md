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

Send one watch per task. This message asks for an uncomfortable floor. The
comfort level is part of the query, not a stored column.

```text
Tell me when a floor average room comfort level is uncomfortable. A room comfort level is 50 plus (temperature - 72) plus (humidity - 42), plus (co2 - 500) / 25 when co2 is above 500, otherwise no co2 extra. Round each room level down, then average the rooms on that floor. Uncomfortable means that average is below 40 or above 50. Keep the watch for 7 days.
```

After the subscription is active, push one floor out of band:

```bash
docker exec drasi-sentinel-building-comfort psql -v ON_ERROR_STOP=1 -U sentinel -d building_comfort \
  -c "UPDATE \"Room\" SET temperature = 40, humidity = 20, co2 = 700 WHERE floor_id = 'floor_01_01'"
```

`floor_01_01` enters that result set. The committed query is
`data/authored-queries/<id>.yaml`. Room and building prompts are in
`examples/building-comfort-local-demo/prompts.sh`.
