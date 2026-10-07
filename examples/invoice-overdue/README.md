# Invoice overdue

This JWT-protected catalog reports overdue-invoice changes as artifacts on an
A2A conversation task. Configure the PostgreSQL and JWT secret files with mode
`0600`, set the Azure OpenAI environment variables, then run:

```bash
dotnet run --project ../../src/Drasi.Sentinel.Host -- sentinel.yaml
```

In A2A Inspector connect to the Agent Card, click **Send streaming**, and ask:

```text
Tell me as soon as an invoice is overdue.
```

Keep the task stream open or reconnect with `SubscribeToTask` and
`Last-Event-ID` to receive later observations.
