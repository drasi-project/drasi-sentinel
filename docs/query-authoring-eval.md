# Tune query authoring from the eval

Change the authoring instructions, then prove the host still rejects a bad query and the configured model still commits a good one.

The instructions are the string `AgentTurn.Render` returns when you pass a `QueryAuthoringPrompt`. The text is `AuthoringPreamble`, `AuthoringBody`, and `LifetimeRule` in `src/Drasi.Sentinel.Application/AgentTurn.cs`. Tool descriptions are on `QueryWorkbench`. `Skill_states_the_rules_the_eval_depends_on_only_when_authoring_is_enabled` pins the phrases that text must contain. The fixture names that text the skill. An Agent Card skill is a curated query on the card. Do not edit `AgentCard.cs` to change how the model writes Cypher.

## Run the fixture eval

From the repository root, run:

```bash
./tests/query-authoring-eval.sh
```

The script runs `QueryAuthoringEvalTests` and `QueryAuthoringCoverageTests`. It does not call a model. A passing run prints `Fixture evals do not call a model.`

Those tests score `QueryText.Examine`, `QueryText.Scaffold`, commit rejection in `QueryAuthoring`, the tools on `QueryWorkbench`, and the instruction text. `QueryAuthoringHierarchyTests` is not in the script. `dotnet test Drasi.Sentinel.slnx` runs that class with the rest of the suite.

## Fix a fixture failure before a model call

A fixture failure means the host or the pinned instruction text changed. Fix that before you spend a model call. The script stops on the first failing `dotnet test`.

If a fixture rejects a query the watch needs, change the check in `QueryText` or `QueryAuthoring`. Keep the rejection for the bad shape.

If a commit that should fail was stored, reject it in `QueryAuthoring`. Assert the `CommitSkippedReason` text in `QueryAuthoringEvalTests`.

If `Skill_states_the_rules_the_eval_depends_on_only_when_authoring_is_enabled` fails, the instructions dropped a phrase the model is told to follow. Put the phrase back in `AuthoringBody`. Do not delete the assertion to make the test pass.

## Run the model eval

Sign in with the Azure CLI. The eval uses `AzureCliCredential` and the scope `https://ai.azure.com/.default`.

```bash
export SENTINEL_QUERY_EVAL=1
export AZURE_OPENAI_ENDPOINT='https://your-resource.openai.azure.com/'
export AZURE_OPENAI_MODEL='your-deployment'
./tests/query-authoring-eval.sh
```

The script runs the fixture tests first. If those pass, it prints `Model eval calls AZURE_OPENAI_MODEL at AZURE_OPENAI_ENDPOINT with the Azure CLI credential.` and runs `QueryAuthoringModelEval`. That test calls the deployment named by `AZURE_OPENAI_MODEL`. It stops after 3 minutes.

`dotnet test Drasi.Sentinel.slnx` does not call the model. `QueryAuthoringModelEval` returns without a call unless `SENTINEL_QUERY_EVAL` is `1`.

`Configured_model_authors_a_cross_source_join` scores `Tell me when an open order has a matching payment. Build the query against the configured sources and subscribe. Keep the watch for 1 day.` The fixture graph is `orders` on `orders-db` and `payments` on `payments-db`, from `QueryAuthoringEvalSupport`. The model must commit one query. The query must name both sources, both labels, and one join id in both `joins` and the Cypher. It must not contain `SELECT`. The join id is not fixed. The model must also call `get_graph_schema`, `get_source_schema`, `profile_source_schema`, or `list_sources`.

`Configured_model_uses_a_mapping_named_in_later_turns` replays a caller who first names an unmapped condition, then names the property and stored value in later messages. The first turn must not subscribe. Once those later messages name the mapping, the model must commit and subscribe. It must not ask again which label, property, or stored value to use. That case can take several minutes.

`Configured_model_asks_which_value_when_a_later_turn_names_only_the_property` posts Inspector-shaped `SendStreamingMessage` requests through the A2A endpoint. The follow-ups carry `contextId` and no `taskId`. They must stay on the first task. The property-only turn must not subscribe. The stored-value turn must commit both conditions, and the join must name the shared key. A relationship in the Cypher without that joins entry does not pass.

This case does not pass a `TtlPolicy`, and it does not pass the prompt as `callerText`. A missing `ttl` is stored as unbounded. A live turn with the default uncapped policy rejects a missing `ttl`. If the model asks for a lifetime and commits nothing, the case fails. Add the lifetime to the prompt in `QueryAuthoringModelEval` before you treat that failure as a bad Cypher rule. Do not delete the ask sentence in `LifetimeRule` to make this case pass.

## Read a model failure

The assertion prints the reply and every tool call.

```text
The model did not commit one query. Reply: ... Tools: get_graph_schema {} | add_query {...}
```

Use that list to pick the file you edit.

The model never called a schema tool. Tighten the sentence in `AuthoringBody` that names `get_graph_schema` and `get_source_schema`. You can also tighten the matching tool description on `QueryWorkbench`. Do not remove the assertion in `QueryAuthoringModelEval`.

The model wrote SQL, a comma join, a phrase filter, or a duration without `drasi.trueFor`. The fixture tests already reject those commits. The model ignored `CommitSkippedReason`, or it never called `add_query`. Add the recovery sentence next to the existing one in `AuthoringBody`. Use the words the host returns, including `declares no joins`, `Stored values`, and `trueFor`. The skill fixture must still contain those words.

The model committed two queries, subscribed to the wrong id, or did not subscribe. `AuthoringBody` tells the model to call `subscribe_to_changes` with the id in `Committed`. If the host kept an older query, that id can differ from the id the model passed. Point the sentence at `Committed.Id`.

The host stored a query the watch should not contain. That is not an instruction bug. Add a fixture that rejects it. Then add one sentence in `AuthoringBody` that repeats the new `CommitSkippedReason` text. Do not add an instruction the host will not enforce. The next turn can ignore that sentence and still commit.

Run the fixture script again. Then run the model eval once. A pass on this prompt is not a pass on every demo. After it passes, send one prompt from `examples/curbside-local-demo/prompts.sh` or `examples/building-comfort-local-demo/prompts.sh`. Read the committed query in that example's `data/authored-queries/` directory.

## Add a scored prompt

If the miss can be scored without a model, add a fact to `QueryAuthoringEvalTests` or `QueryAuthoringCoverageTests`. Those are the classes the script runs.

Put a host-only rule in `QueryAuthoringHierarchyTests` when the script does not need to run it. To make the script run that class, add the class name to the `--filter` in `tests/query-authoring-eval.sh`.

If the miss is a tool sequence the host cannot see, copy `Configured_model_authors_a_cross_source_join`. Change the user text and the assertion. Share the graph with the fixtures through `QueryAuthoringEvalSupport`. Keep the `SENTINEL_QUERY_EVAL` gate. Do not add a model call to the default `dotnet test` run.
