# Examples

The examples define curated continuous queries and task-stream reaction
policies. They report changes on the A2A task stream.

After a task has its subscription, ask for its current results. The agent
returns a task-stream data artifact for that task's curated query; do not
provide query, source, Cypher, or filter identifiers.

- [Invoice overdue](invoice-overdue/README.md) is a JWT-protected catalog.
- [Local accounting demonstration](accounting-local-demo/README.md) is an
  anonymous demo profile for A2A Inspector.

Set `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL` before running a host.
The local accounting demo generates its required private PostgreSQL files
during bootstrap. Other secret fixtures document required JWT files.
