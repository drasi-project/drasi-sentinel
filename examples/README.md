# Examples

The examples define sources, reactions, and, except for the curbside demo,
curated continuous queries. They report changes on the A2A task stream. The
local accounting demo also posts each native diff to a local webhook receiver.

After a task has its subscription, ask for its current results. The agent
returns a task-stream data artifact for that task's curated query; do not
provide query, source, Cypher, or filter identifiers.

- [Invoice overdue](invoice-overdue/README.md) is a JWT-protected catalog.
- [Local accounting demonstration](accounting-local-demo/README.md) is an
  anonymous demo profile for A2A Inspector.
- [Local curbside demonstration](curbside-local-demo/README.md) starts a
  PostgreSQL orders source, a MySQL vehicles source, and no queries. The agent
  builds the watches. Plugin refs are declared in `sentinel.yaml` and installed
  on startup.
- [Local building comfort demonstration](building-comfort-local-demo/README.md)
  starts the PostgreSQL building, floor, and room source from the Drasi
  building comfort tutorial, and no queries. The agent builds the watches.

Set `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL` before running a host.
The local accounting and curbside demos write owner-only `secrets/secrets.json`
during bootstrap. The invoice example uses the same file for its connection
string and JWT public key.
