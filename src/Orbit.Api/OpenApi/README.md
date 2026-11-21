# Selecting an OpenAPI artifact

[artifacts.json](artifacts.json) is the checked publication catalog. Generation, freshness
and compatibility use the same declarations. Each entry contains exactly `id`,
`httpVersion`, `revision`, `documentName`, `path`, `deprecated` and `replacement`. The first
five are strings and form the stable identity. `deprecated` is a boolean. `replacement` is
another identity or null. Each path is a repository-relative file.

| Identity | HTTP header selection | Revision | Repository path |
| --- | --- | --- | --- |
| `orbit-http-v1-r1` | omitted or `api-version: 1.0` | deprecated legacy | `src/Orbit.Api/openapi.json` |
| `orbit-http-v2-r1` | `api-version: 2.0` | deprecated legacy | `src/Orbit.Api/openapi_v2.json` |
| `orbit-http-v1-r2` | omitted or `api-version: 1.0` | corrected enums | `src/Orbit.Api/openapi_v1-r2.json` |
| `orbit-http-v2-r2` | `api-version: 2.0` | corrected enums | `src/Orbit.Api/openapi_v2-r2.json` |

The legacy files keep known inaccuracies. FrequencyUnit, DayOfWeek and InstanceStatus
promise integer-only output. ScheduledReminderWhen advertises snake_case names instead of
the canonical response names of MVC. The r2 entries replace them. Deprecation is catalog
metadata, so the bytes of an existing artifact keep their meaning.

An immutable consumer selection is a full 40-character producer commit plus an exact
catalog identity. Read the catalog at that commit and verify the identity, the HTTP version
and the path. Then read the artifact at the same commit. Reject an unknown identity, a
mismatched version, a missing file and a malformed commit. There is no arbitrary URL
selector and no fallback to another artifact. A pin with only a SHA selects `openapi.json`.

A revision changes the documentation only. It does not change a request header, a route, a
default or a runtime DTO. The r2 schemas publish the canonical output names plus an
undefined Int32 numeric fallback. Separate request components accept case-insensitive
names, comma-separated names and bounded numeric strings. Only the reminder inputs also
accept the aliases `same_day` and `day_before`. JSON Schema integer cannot distinguish the
tokens `1`, `1.0` and `1e0`. The MVC enum converter rejects the last two.

Run these commands from the repository root:

```sh
dotnet build Orbit.slnx
python3 .github/scripts/openapi_artifacts.py freshness
python3 .github/scripts/openapi_artifacts.py plan --base origin/main
python3 .github/scripts/openapi_artifacts.py evidence
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~HabitEnumContractTests|FullyQualifiedName~ReminderInputContractTests|FullyQualifiedName~HabitScheduleContractTests|FullyQualifiedName~ApiVersioningTests'
python3 -m unittest discover -s .github/scripts -p 'test_openapi_artifacts.py'
```

Freshness requires each catalog artifact to be committed, present and identical to the
normal generation, including the complete file list of the generator. The compatibility
planner reads the target commit and its catalog. A published identity or path cannot be
removed, renamed or substituted. An artifact that is absent from the target is a first
publication with no prior comparison.

When an OpenAPI input changes, the required `OpenAPI Breaking-Change Gate` runs the oasdiff
release that `test.yml` pins by checksum, with the ERR failure threshold. It validates the
inventory, then compares the v1 document and each additional stream. One failed comparison
fails the gate. When no OpenAPI input changes, the gate passes without a comparison.

The tooling rebuilds each canonical path from a validated ASCII catalog identity. It rejects
a file whose symlink leaves the repository. It never resolves a path that the generator cache
supplies. A target ref must resolve to a full 40-character hexadecimal commit before the
tooling reads a file at that commit.

## Contract documents

- [AdaptivePlacement.md](AdaptivePlacement.md)
- [AiDateContext.md](AiDateContext.md)
- [AiPlans.md](AiPlans.md)
- [AnswerComposition.md](AnswerComposition.md)
- [AppleIntegrations.md](AppleIntegrations.md)
- [EvalPendingOperations.md](EvalPendingOperations.md)
- [ExistingParentPreviews.md](ExistingParentPreviews.md)
- [GamificationReadBlocks.md](GamificationReadBlocks.md)
- [HabitMonth.md](HabitMonth.md)
- [HabitRanking.md](HabitRanking.md)
- [Onboarding.md](Onboarding.md)
- [QuestionBlocks.md](QuestionBlocks.md)
- [ServerConversations.md](ServerConversations.md)
