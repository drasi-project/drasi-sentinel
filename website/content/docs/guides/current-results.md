---
title: "Ask for current results"
linkTitle: "Current results"
weight: 30
description: >
  Read the rows already in the watched query, on the same task.
---

Current results are not a second watch. Send the request on the task a watch
already opened.

Use a new `messageId` and this text as the only text part:

```text
What are the current results for this task?
```

The output is an artifact update whose data `type` is
`current_query_results`. It contains `taskId`, `subscriptionId`, `queryId`,
`queryDescription`, and `rows`.

The host uses the subscription already on that task. [Start a watch](../start-a-watch/)
lists what a message must not contain.

This artifact does not replace later `native_query_diff` events. Keep the
stream open after you read the rows.
