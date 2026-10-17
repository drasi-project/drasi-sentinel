---
type: "docs"
title: "Drasi Sentinel"
linkTitle: "Drasi Sentinel"
no_list: true
hide_readingtime: true
description: "An A2A agent that reports Drasi query changes on one durable task"
cascade:
  - type: "docs"
---

<div class="hero-section hero-section--compact">
  <h1 class="hero-title">Ask for a change. Read each diff on one task.</h1>
  <p class="hero-subtitle">Sentinel is an A2A agent. You describe a supported change in plain language. It binds one curated query to one durable task and reports later result changes as JSON artifacts.</p>

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

<p class="section-intro">A watch is not a one-shot answer. The opening message selects the query. Later changes arrive on the same task.</p>

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
    <div class="flow-step__description">The host binds the curated query and reaction</div>
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

You do not send a query id, Cypher, a source name, a filter, or a destination. The host already has those. If the manifest enables query authoring, and no curated query covers the request, the agent can build one query against the configured sources and then open the watch.

Delivery is standard A2A JSON-RPC over SSE. Open the watch with `message/stream`. Reconnect with `tasks/subscribe` and `Last-Event-ID`. An idle stream stays open for the life of the watch. While it waits, the host writes an SSE comment at least every 15 seconds. That comment is not an event. Do not set a read timeout.

## When to use Sentinel

Sentinel fits a caller that needs a governed watch, not a private query string.

- A consuming agent should receive each result change, including rows that leave the result set, without polling.
- The catalog is curated. Callers select a published skill. They do not invent Cypher.
- The stream may stay open for hours or days. Reconnect from the last event id you applied.

## Documentation

<p class="section-intro">Start with the local accounting demo, then use the contract pages when you write a client.</p>

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
    <div class="doc-card__desc">Start a watch, keep the stream, and read current rows.</div>
  </a>
  <a href="docs/reference/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-code"></i></div>
    <div class="doc-card__title">Reference</div>
    <div class="doc-card__desc">Manifest fields, A2A methods, and artifact fields.</div>
  </a>
  <a href="docs/examples/" class="doc-card">
    <div class="doc-card__icon"><i class="fas fa-flask"></i></div>
    <div class="doc-card__title">Examples</div>
    <div class="doc-card__desc">Local demos and the JWT-protected catalog.</div>
  </a>
</div>
