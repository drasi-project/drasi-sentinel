# Drasi Sentinel documentation site

This directory is the source for the
[Drasi Sentinel documentation site](https://supreme-guacamole-w9j9wne.pages.github.io/).
Hugo and the Docsy theme build it. Pages is set to GitHub Actions.

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

[`.github/workflows/website.yml`](../.github/workflows/website.yml) builds this
site and deploys it from `main`. Pull requests build only.
`actions/configure-pages` supplies the base URL. The local preview is
`hugo server`.
