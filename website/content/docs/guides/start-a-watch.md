---
title: "Start a watch"
linkTitle: "Start a watch"
weight: 10
description: >
  Select one published skill and open one A2A task.
---

This page is for a host that published skills. If no skill covers the change,
and the host has authoring on, use [Author a query](../author-a-query/).

Read the Agent Card at `/.well-known/agent-card.json` once. A skill tagged
`continuous-query` is one published query. Its description says what is
watched. Its examples, when present, are the messages that select it.

## Send one message

Choose a new `taskId` and `messageId`. POST `message/stream` to the JSON-RPC
URL on the card. The user text is one example from the chosen skill, or the
skill description when it lists no examples. That text is the whole message.

Include a lifetime in that text, or be ready to answer the question on the
same task. See [Set a lifetime](../set-a-lifetime/).

Do not send the skill id, a reaction id, Cypher, a source name, a filter, a
confirmation code, a destination, or a push URL. The host binds the reaction listed for that query. You do not choose it.

Keep the response open. A later change arrives on this stream as an artifact
update. See [Keep the stream open](../keep-the-stream/).

## A second watch is a new task

Do not send another watch on a task that already has a subscription. The host
starts a new task and keeps the first watch. If the agent asks a question,
answer on the task that asked. A new task would be a new watch.

To list watches you already opened, POST `tasks/list` with `params` `{}`.
Each entry has a task id, a status, and `metadata.queryId`.
