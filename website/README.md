# Drasi Sentinel documentation site

This directory is the source for the
[Drasi Sentinel documentation site](https://drasi-project.github.io/drasi-sentinel/).
Hugo and the Docsy theme build it. GitHub Pages publishes it from `main` via
[`.github/workflows/website.yml`](../.github/workflows/website.yml).

The site uses the same Docsy pin and brand styles as
[drasi-project/drasi-dotnet](https://github.com/drasi-project/drasi-dotnet/tree/main/website).

## Layout

```text
website/
  config.toml
  content/
    _index.md
    docs/
  themes/docsy/          Docsy, as a git submodule
  package.json
```

## Prerequisites

- Hugo extended 0.152.2 or newer. The SCSS pipeline needs the extended build.
- Node.js 22 or newer.
- The Docsy submodule. If you cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
```

## Preview

From `website/`:

```bash
npm install
npm --prefix themes/docsy install
hugo server
```

The preview is at `http://localhost:1313/drasi-sentinel/`.

A production build writes `website/public/`:

```bash
hugo --gc --minify
```

## Add a page

Put Markdown under `content/docs/`. A section uses `_index.md`. Set `weight`
to order the sidebar. Lowest comes first. Use relative links so they resolve
under `/drasi-sentinel/` on GitHub Pages.

```yaml
---
title: "Page title"
linkTitle: "Nav title"
weight: 25
description: >
  One line for the sidebar and search.
---
```

## Publish

Pushes to `main` that touch `website/` or the workflow file build the site
and deploy it. Pull requests build only. In the repository settings, set
Pages to GitHub Actions. `actions/configure-pages` supplies the base URL.
