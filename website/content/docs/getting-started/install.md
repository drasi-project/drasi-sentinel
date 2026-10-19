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
| Linux x64 | [sentinel-linux-x64.tar.gz](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-linux-x64.tar.gz) |
| Linux arm64 | [sentinel-linux-arm64.tar.gz](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-linux-arm64.tar.gz) |
| macOS Apple silicon | [sentinel-osx-arm64.tar.gz](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-osx-arm64.tar.gz) |
| macOS Intel | [sentinel-osx-x64.tar.gz](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-osx-x64.tar.gz) |
| Windows x64 | [sentinel-win-x64.zip](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-win-x64.zip) |

[SHA256SUMS](https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/SHA256SUMS)
lists a SHA-256 for each archive.

## Download

macOS Apple silicon:

```bash
curl -fsSL -O https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-osx-arm64.tar.gz
tar -xzf sentinel-osx-arm64.tar.gz
```

That creates a `sentinel-osx-arm64` directory. The other Unix archives unpack
the same way. Use the file name from the table.

Windows:

```powershell
Invoke-WebRequest -Uri https://github.com/drasi-project/drasi-sentinel/releases/download/v0.1.0-alpha.1/sentinel-win-x64.zip -OutFile sentinel-win-x64.zip
Expand-Archive sentinel-win-x64.zip -DestinationPath .
```

## Run

The binary does not configure itself. You pass it one YAML file, the
manifest. That file names the sources to connect, whether
`service.query_authoring.enabled` is true, and the queries to publish when
authoring is off.
Write that file before you set any variables. The fields are in the
[manifest reference](../reference/manifest/). A complete file is the
[accounting manifest](https://github.com/drasi-project/drasi-sentinel/blob/main/examples/accounting-local-demo/sentinel.yaml).
That host has authoring off. [Building comfort](../examples/building-comfort/)
is a host with authoring on.

The file name can be anything that ends in `.yaml` or `.yml`. The first
argument is the path to that file. If you omit it, the process stops and
prints that it needs a `.yaml` or `.yml` path.

The example files do not store the model endpoint in YAML. The `agent` block
names the environment variables that hold it:

```yaml
agent:
  url: { env: AZURE_OPENAI_ENDPOINT }
  model: { env: AZURE_OPENAI_MODEL }
```

Set the variables your file names, then start the process. Those two names
are what the examples use. A different `env` name in your file means a
different variable. If a named variable is unset, the process stops and
prints that name.

macOS Apple silicon:

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

Change the directory name if you unpacked a different archive.

The host installs the plugins declared in the manifest on startup. The first
start needs network access unless those plugins are already next to the
manifest. The agent does not see source passwords. Passwords stay in the
secret file the manifest names, not in the command.

The process prints the URL it listens on. The Agent Card is
`/.well-known/agent-card.json` on that URL.

The binary is the host only. It does not start PostgreSQL, MySQL, or
Inspector. The [accounting demo](../) does that from a clone of this
repository, and that path needs the .NET 10 SDK.
