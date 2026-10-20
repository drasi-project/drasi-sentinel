---
type: "docs"
title: "Drasi Sentinel"
linkTitle: "Drasi Sentinel"
no_list: true
hide_readingtime: true
description: "An A2A agent that reports result changes on one durable task"
cascade:
  - type: "docs"
---

<div class="hero-section hero-section--compact">
  <h1 class="hero-title">Ask for a change. Read each diff on one task.</h1>
  <p class="hero-subtitle">Sentinel is an A2A agent. You describe a change in plain language. It watches for that change on one durable task and reports each later change as a JSON artifact.</p>

  <div class="cta-group">
    <a href="docs/getting-started/" class="cta-button cta-button--primary">
      <i class="fas fa-rocket"></i>
      Get started
    </a>
    <a href="docs/concepts/" class="cta-button cta-button--secondary">
      <i class="fas fa-lightbulb"></i>
      How a watch works
    </a>
  </div>
</div>

## How a watch works

<p class="section-intro">A watch is not a one-shot answer. The opening message is the change you want. Later diffs arrive on the same task.</p>

<div class="flow-diagram">
  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-comment"></i>
    </div>
    <div class="flow-step__label">Describe the change</div>
    <div class="flow-step__description">Plain language, plus how long to keep the watch</div>
  </div>

  <div class="flow-arrow">
    <i class="fas fa-arrow-right"></i>
  </div>

  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-link"></i>
    </div>
    <div class="flow-step__label">One task, one subscription</div>
    <div class="flow-step__description">The host keeps the matching rows on one task</div>
  </div>

  <div class="flow-arrow">
    <i class="fas fa-arrow-right"></i>
  </div>

  <div class="flow-step">
    <div class="flow-step__icon">
      <i class="fas fa-bolt"></i>
    </div>
    <div class="flow-step__label">Read the diffs</div>
    <div class="flow-step__description">Each result change is a JSON artifact</div>
  </div>
</div>

A query is the condition that decides which rows match. The host keeps that set for the life of the watch. A diff is a row entering the set, changing, or leaving it. Drasi calls this a [continuous query](https://drasi.io/concepts/continuous-queries/).

You do not choose authoring in the message. The manifest does, with `service.query_authoring.enabled`. If that key is absent, authoring is off.

## When authoring is off

The host opens a watch only for a query it already published. It lists each of those queries as a skill. Your message has to match one. If none matches, the agent says so and stops. It does not write a query. The [accounting demo](docs/examples/accounting/) sets the flag to false. The procedure is [Start a watch](docs/guides/start-a-watch/).

## When authoring is on

A published skill still wins when your message matches one. If none matches, the agent writes one query from your description and then opens the watch. [Building comfort](docs/examples/building-comfort/) and [curbside](docs/examples/curbside/) publish no queries, so the first message is that description. The procedure is [Author a query](docs/guides/author-a-query/).

## Once the watch is open

The rest of the call does not depend on which mode created the query. You get one task, one subscription, and the same JSON artifact. What the message must not contain is in [Start a watch](docs/guides/start-a-watch/).

Delivery is standard A2A JSON-RPC over SSE. Open the watch with `message/stream`. Reconnect with `tasks/subscribe` and `Last-Event-ID`. An idle stream stays open for the life of the watch. While it waits, the host writes an SSE comment at least every 15 seconds. That comment is not an event. Do not set a read timeout.

## When to use Sentinel

Sentinel fits a caller that needs a governed watch, not a private query string.

- A consuming agent should receive each result change, including rows that leave the result set, without polling.
- The stream may stay open for hours or days. Reconnect from the last event id you applied.

## Documentation

<p class="section-intro">Start with the demo that matches the host you will run, then use the contract pages when you write a client.</p>

<div class="doc-cards">
  <a href="docs/getting-started/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-rocket"></i></div>
    <div class="doc-card__title">Getting started</div>
    <div class="doc-card__desc">Install the host, or run the accounting demo and see one paid-invoice diff.</div>
  </a>
  <a href="docs/concepts/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-lightbulb"></i></div>
    <div class="doc-card__title">Concepts</div>
    <div class="doc-card__desc">What a watch is, and what a diff means.</div>
  </a>
  <a href="docs/guides/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-book"></i></div>
    <div class="doc-card__title">Guides</div>
    <div class="doc-card__desc">Start a watch, keep the stream, read current rows, or author a query.</div>
  </a>
  <a href="docs/reference/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-code"></i></div>
    <div class="doc-card__title">Reference</div>
    <div class="doc-card__desc">Manifest fields, A2A methods, and artifact fields.</div>
  </a>
  <a href="docs/examples/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-flask"></i></div>
    <div class="doc-card__title">Examples</div>
    <div class="doc-card__desc">A catalog demo, two hosts that write the query, and a JWT-protected catalog.</div>
  </a>
</div>
