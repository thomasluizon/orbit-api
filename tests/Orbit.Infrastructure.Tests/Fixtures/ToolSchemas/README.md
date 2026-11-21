# AI tool schema baseline

`inlined.json.gz` is the baseline of the inlined tool catalog from commit `8e6e3690`. It holds the unnormalized source schema of each registered chat tool. It also holds the normalized provider schemas for the legacy, composition and offer clients.

The export uses the real SDK request builder and a local HTTP transport. No request reaches OpenAI. The file holds schemas only. It holds no account data, no API credential and no provider response text.

## Amend the baseline

The bounded schema builders are the execution contract. The compact exporter recognizes the repeated habit structure, keeps one recursive habit definition and shares identical schema blocks inside a tool. The equivalence test expands each reference and compares the result with the baseline.

An amendment to the baseline follows these rules:

- Copy only the changed properties, descriptions and required entries from the real expanded SDK producer. Edit only the schema sites that the change names.
- Keep every other type, constraint, enum, required entry and inlined structure unchanged.
- Never regenerate the baseline from the compactor under test. The comparison would then compare the compactor with its own output.

The equivalence test applies these steps before it compares the schemas:

- It expands each reference.
- It projects the recursion to the server limit. The limit is five habit levels, including the root. For `create_sub_habit` the limit is four remaining levels.
- It removes the fields that the old leaf did not have: `parent_habit_id`, `sub_habits` and their required entries.
- It normalizes a nullable reference union to the equivalent type union.
- It removes only the depth sentence that the new schema adds.

The unbounded provider schema admits deeper shapes. The server check rejects them. The command handlers also check the configured depth against the existing parents.

## Regenerate the inlined fixture

Run this from the repository root. The exporter does not depend on the compactor, so the command copies it into the pinned source tree. The temporary tree is an offline build checkout. It does not start a server.

```sh
schema_workspace=$(mktemp -d)
git archive 8e6e3690 | tar -x -C "$schema_workspace"
cp tests/Orbit.Infrastructure.Tests/AI/AiInputBudgetTests.SchemaExport.cs "$schema_workspace/tests/Orbit.Infrastructure.Tests/AI/AiInputBudgetTests.SchemaExport.cs"
dotnet test "$schema_workspace/tests/Orbit.Infrastructure.Tests" -c Release --filter 'FullyQualifiedName~SchemaSnapshots_ExportCurrentContracts'
cp "${TMPDIR:-/tmp/}orbit-1916-tool-schemas.json.gz" tests/Orbit.Infrastructure.Tests/Fixtures/ToolSchemas/inlined.json.gz
```

The same exporter on the current source writes the compact schemas to the temporary file. Never copy that output over the baseline. The gzip container bytes can differ, so compare the decompressed JSON.
