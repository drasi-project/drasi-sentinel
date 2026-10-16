# Drasi Sentinel documentation site

This directory is the source for the
[Drasi Sentinel documentation site](https://supreme-guacamole-w9j9wne.pages.github.io/).
Hugo and the Docsy theme build it. The repository is private, so Pages serves
the site from the `gh-pages` branch at that host. A public repository would
use `https://drasi-project.github.io/drasi-sentinel/`. The Actions workflow
that matches drasi-dotnet is local commit `c3a2053`. This token cannot push
`.github/workflows/website.yml`.

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

The preview is at `http://localhost:1313/`.

A production build writes `website/public/`:

```bash
hugo --gc --minify
```

## Add a page

Put Markdown under `content/docs/`. A section uses `_index.md`. Set `weight`
to order the sidebar. Lowest comes first. Use relative links so they resolve
on the Pages host.

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

Pages is set to the `gh-pages` branch. Rebuild with the Pages base URL before
updating that branch. The local preview is `hugo server`.

The Actions workflow in local commit `c3a2053` is the drasi-dotnet publisher.
It builds on pull requests and deploys from `main`. Pushing it needs a token
with the `workflow` scope. After that file is on `main`, set Pages to GitHub
Actions. `actions/configure-pages` supplies the base URL.
