#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
cd "$root"
dotnet test Drasi.Sentinel.slnx --filter "FullyQualifiedName~QueryAuthoringEvalTests|FullyQualifiedName~QueryAuthoringCoverageTests"
if [ "${SENTINEL_QUERY_EVAL:-}" = "1" ]; then
  echo "Model eval calls AZURE_OPENAI_MODEL at AZURE_OPENAI_ENDPOINT with the Azure CLI credential."
  dotnet test Drasi.Sentinel.slnx --filter "FullyQualifiedName~QueryAuthoringModelEval"
  if [ "${SENTINEL_QUERY_LANGUAGE_EVAL:-}" = "1" ]; then
    echo "Query language suite calls AZURE_OPENAI_MODEL for 50 scenarios."
    dotnet test Drasi.Sentinel.slnx --filter "FullyQualifiedName~Configured_model_scores_drasi_query_language"
  fi
else
  echo "Fixture evals do not call a model."
  echo "Set SENTINEL_QUERY_EVAL=1 plus AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_MODEL to score the configured model."
fi
