---
title: "Install the host"
linkTitle: "Install"
weight: 5
description: >
  Download a self-contained Sentinel host for your platform.
---

The host is a self-contained binary. You do not need the .NET SDK. The
current alpha is
[v0.1.0-alpha.1](https://github.com/drasi-project/drasi-sentinel/releases/tag/v0.1.0-alpha.1).
It is not a stable release. The host contract can still change. That tag stays
on this snapshot. The
[edge release](https://github.com/drasi-project/drasi-sentinel/releases/tag/edge)
is a later build from `main`, and it is also not stable.

## Pick an archive

| Machine | Archive |
| --- | --- |
| Linux x64 | `sentinel-linux-x64.tar.gz` |
| Linux arm64 | `sentinel-linux-arm64.tar.gz` |
| macOS Apple silicon | `sentinel-osx-arm64.tar.gz` |
| macOS Intel | `sentinel-osx-x64.tar.gz` |
| Windows x64 | `sentinel-win-x64.zip` |

`SHA256SUMS` on the same release lists a SHA-256 for each archive.

## Download

macOS Apple silicon:

```bash
gh release download v0.1.0-alpha.1 --repo drasi-project/drasi-sentinel --pattern sentinel-osx-arm64.tar.gz
tar -xzf sentinel-osx-arm64.tar.gz
```

That creates a `sentinel-osx-arm64` directory. The other Unix archives unpack
the same way. Use the file name from the table.

Windows:

```powershell
gh release download v0.1.0-alpha.1 --repo drasi-project/drasi-sentinel --pattern sentinel-win-x64.zip
Expand-Archive sentinel-win-x64.zip -DestinationPath .
```

## Run

Set the model endpoint, then pass the manifest path. This example is the
macOS Apple silicon archive. Change the directory name for your archive.

```bash
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_MODEL="your-model"
./sentinel-osx-arm64/sentinel path/to/sentinel.yaml
```

Windows:

```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_MODEL = "your-model"
.\sentinel-win-x64\sentinel.exe path\to\sentinel.yaml
```

The first argument is the manifest. If you omit it, the process stops and
prints that it needs a `.yaml` or `.yml` path.

The host installs the plugins declared in the manifest on startup. The first
start needs network access unless those plugins are already next to the
manifest. The agent does not see source passwords. See the
[manifest reference](../reference/manifest/) for the file shape.

The process prints the URL it listens on. The Agent Card is
`/.well-known/agent-card.json` on that URL.

The binary is the host only. It does not start PostgreSQL, MySQL, or
Inspector. The [accounting demo](../) does that from a clone of this
repository, and that path needs the .NET 10 SDK.
