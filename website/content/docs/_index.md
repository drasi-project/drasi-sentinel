---
title: "Documentation"
linkTitle: "Docs"
menu: {main: {weight: 1}}
weight: 20
no_list: true
description: >
  Guides, concepts, and the A2A contract for Drasi Sentinel.
---

Sentinel is a governed A2A change-detection agent. A caller describes a
change. The agent binds one query, opens one subscription on one task, and
reports later changes on that task. On a catalog host the query is a published
skill. On an authoring host the agent writes it.

## Where to start

- [Install the host](getting-started/install/) downloads a binary for Linux, macOS, or Windows.
- [Getting started](getting-started/) runs the local accounting demo and shows one result diff.
- [Concepts](concepts/) follows one watch from the opening message through a restart.
- [Guides](guides/) cover starting a watch, keeping the stream, current results, lifetime, and query authoring.
- [Reference](reference/) lists the manifest fields and the A2A methods a client calls.
- [Examples](examples/) points at the runnable demos in this repository.
