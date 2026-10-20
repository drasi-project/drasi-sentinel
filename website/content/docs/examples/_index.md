---
title: "Examples"
linkTitle: "Examples"
weight: 50
description: >
  Runnable hosts in this repository.
---

Each example writes its journal, indexes, and authored queries under `data/`
next to its own `sentinel.yaml`. Two examples do not share those files.

Set `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_MODEL` to an Azure OpenAI or Azure AI Foundry model before you start a host. Run `az login` first. A generic OpenAI-compatible endpoint will not work.

- [Accounting](accounting/) has one published query and authoring off.
- [Building comfort](building-comfort/) has sources and no queries. The agent builds the watch.
- [Curbside](curbside/) has two database sources and no queries.
- [Invoice overdue](invoice-overdue/) is a JWT-protected catalog.
