---
title: "Set a lifetime"
linkTitle: "Set a lifetime"
weight: 40
description: >
  Say how long to keep a watch, or answer the question on the same task.
---

Every watch has a lifetime. Put it in the opening message:

```text
Tell me when an invoice is paid. Keep the watch for 7 days.
```

Accepted tokens are `none`, or a positive integer plus `s`, `m`, `h`, or `d`.
`7d` is seven days. `none` means the watch does not expire, unless the
manifest caps it.

## If you leave it out

The agent asks how long to keep the watch. Answer on the same task. Do not
open a new task for the answer.

The agent asks only after it can start a watch. A request that matches no
catalog skill, on a host with authoring off, does not get a lifetime
question. The agent says the catalog does not cover it and stops.

## The host cap

`service.max_ttl` is the longest lifetime a caller may choose. Omit it, or
set `none`, for no cap. A requested lifetime longer than the cap is rejected.

When a watch is due, the host detaches it and marks it expired. A due
authored query is removed only when no live watch still uses it.
