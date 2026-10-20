# Additive enum publication evidence

Tracks thomasluizon/orbit-tickets#1591. The preserved producer
`6aa73572faabcad29afc98900ab868bea02218de` remains in branch history. The semantic
carry initially observed `bf973e23534e285fc69299c11f1c9a800e350b72`; the final
carry target is `d10e175b1f90d031cd533f380b944e6035892ee4`. That target includes the merged bulk subtree persistence repair. Its domain reminder guards,
dates, limiter changes, converters and runtime DTOs are retained. Only OpenAPI
registration/configuration changes production source relative to that target.

## Verified external interfaces

[enum-schema-interfaces.txt](enum-schema-interfaces.txt) records the complete
public property/type sets observed by reflection on the actual installed schema
transformer-context and options objects. The detailed producer-receipt command below
emits the property inventory using `type.GetProperties()` and each `Name` and `PropertyType`; these
are observations rather than invented fixtures.

The installed packages are Microsoft.AspNetCore.OpenApi 10.0.13,
Microsoft.Extensions.ApiDescription.Server 10.0.13 and Microsoft.OpenApi 2.12.2.
Actual installed assembly decompilation established these construction points:

- `Microsoft.AspNetCore.OpenApi.OpenApiOptions` constructor assigns
  `ShouldInclude = description => description.GroupName == null || description.GroupName == DocumentName`.
  A newly named document therefore needs an explicit predicate for the existing
  `v1` or `v2` group. `ApiVersioningTests` resolves the real MVC explorer and named
  options, checks `DocumentName`, evaluates every description and checks generated
  request-header metadata. Both corrected documents contain 165 paths, matching
  their legacy version. No HTTP v3 is registered.
- `Microsoft.Extensions.ApiDescription.Tool.Commands.GetDocumentCommandWorker.GetDocumentPath`
  uses `fileName + ".json"` for document `v1`, otherwise
  `fileName + "_" + documentName + ".json"`. Actual normal builds with the existing
  `--file-name openapi --openapi-version OpenApi3_1` emitted all four catalog paths.
  `obj/Orbit.Api.OpenApiFiles.cache` contains exactly those four absolute output
  paths, one per line. Freshness resolves those lines and compares their complete
  set against the catalog.
- `OpenApiSchema.Type` is nullable `JsonSchemaType`; `Enum` is a JsonNode list;
  `AnyOf` is an IOpenApiSchema list; `Pattern`, `Minimum`, `Maximum` and
  `Description` are strings; `Metadata` is a string/object dictionary.
  The bounded branches serialize `Minimum` and `Maximum` as JSON numbers.
  `OpenApiSchemaTransformerContext.JsonTypeInfo` supplies the actual Type;
  `ParameterDescription` identifies input generation. The installed
  `OpenApiSchemaService.InnerApplySchemaTransformersAsync` carries that context
  through array items and properties. `x-schema-id` metadata determines component
  identity during reference resolution. Request reminder value objects need their
  own identities to keep response references canonical.

Re-derive installed construction evidence with `ilspycmd -t <type> <assembly>` for
those types, taking assemblies from the installed versions above. The generation
command is `dotnet build Orbit.slnx`; the normal build prints the complete generator
invocation and document filenames. This uses the real service registration and
source records, without a desired converter or copied DTO fixture.

The pinned action source is
[oasdiff-action breaking/action.yml](https://github.com/oasdiff/oasdiff-action/blob/b9325c9e0a27ab65b0da3b766522cedec6be81dc/breaking/action.yml).
Its declared `base`, `revision`, `fail-on` and `review` inputs were inspected directly.
Its Dockerfile uses `tufin/oasdiff:v1.33.0`. Its entrypoint runs
`oasdiff breaking "$base" "$revision" ... --fail-on ...` and preserves the exit
result. A real local `oasdiff --version` returned `oasdiff version 1.33.0`.
Actual `breaking --help` accepts git-ref/file comparisons and documents ERR exit 1;
real comparisons below confirm both success and rejection. No action response
field, summary, count or review URL is used to decide compatibility.

Exact source retrieval:

```sh
gh api -H 'Accept: application/vnd.github.raw+json' 'repos/oasdiff/oasdiff-action/contents/breaking/action.yml?ref=b9325c9e0a27ab65b0da3b766522cedec6be81dc'
gh api -H 'Accept: application/vnd.github.raw+json' 'repos/oasdiff/oasdiff-action/contents/breaking/Dockerfile?ref=b9325c9e0a27ab65b0da3b766522cedec6be81dc'
gh api -H 'Accept: application/vnd.github.raw+json' 'repos/oasdiff/oasdiff-action/contents/breaking/entrypoint.sh?ref=b9325c9e0a27ab65b0da3b766522cedec6be81dc'
```

## Complete generated interface evidence

[enum-schema-evidence.json](enum-schema-evidence.json) contains the complete designed
catalog, generated document root key/type sets, complete info objects, all affected
enum/input/reminder components including exact patterns, and every direct enum
reference location in each supported document. Both HTTP versions are recorded
independently. Regenerate with:

```sh
python3 .github/scripts/openapi_artifacts.py evidence > tests/Orbit.Infrastructure.Tests/Compatibility/enum-schema-evidence.json
```

The generated roots have exactly `openapi: string`, `info: object`, `paths: object`,
`components: object`, `tags: array`. Legacy info objects have `title` and `version`;
corrected info objects also have `description`. All are strings. Corrected response
enums have exactly `anyOf`; its branches are `{enum: string[], type: "string"}`
and `{maximum: 2147483647, minimum: -2147483648, type: "integer"}`.
Input enums additionally have `description: string` and a third branch
`{pattern: string, type: "string"}`. These seven components cover the four response
enums and the three actual input enums. InstanceStatus has no invented input field.

The four complete defined-name sets are Day/Week/Month/Year;
Sunday/Monday/Tuesday/Wednesday/Thursday/Friday/Saturday;
Pending/Completed/Overdue; SameDay/DayBefore. Undefined output retains numeric Int32
fallback. Actual serializer observations have complete reminder objects:

```json
{"when":"SameDay","time":"08:00:00.1230000"}
{"when":"DayBefore","time":"08:00:00.1230000"}
{"minutesBefore":null,"when":"SameDay","time":"08:00:00.1230000"}
{"minutesBefore":null,"when":"DayBefore","time":"08:00:00.1230000"}
{"minutesBefore":15,"when":null,"time":null}
```

These are synthetic serializer observations using real DTO constructors, not
complete authenticated HTTP responses. ScheduledReminderTime keys are exactly
`when`, `time`; RelativeReminderTime keys are exactly `minutesBefore`, `when`, `time`
in that order. The owning output tests assert full DTO equality and ordering.

## Producer preservation and regression checks

The unchanged focused command passed 294 tests with overflow still present:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~HabitEnumContractTests|FullyQualifiedName~ReminderInputContractTests|FullyQualifiedName~HabitScheduleContractTests|FullyQualifiedName~ApiVersioningTests' --nologo
```

After strengthening the integer evaluator and adding the overflow guard, before
fixing generation, this command failed all six cases for the intended reason:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~OpenApi_NumericOverflowIsRejectedBySchemaAndMvcSeparately --nologo
```

Both numeric overflow values and signed/zero-padded overflowing strings were
admitted by the old producer. Corrected revisions reject them. Separate binding
tests assert actual JsonException results. The evaluator uses exact normalized decimal digits and BigInteger
exponents for parsed numeric values, with schema bounds checked
independently. Controls accept unbounded integers above Int32 and equivalent
`1.0`/`1e0`, while bounded schemas reject overflow and precise fractional values.
MVC's rejection of noninteger token spellings is characterized separately.

`ProducerReceipt_RecordsActualMvcDtosAndRequestResults` was executed once with the
actual target AddApiPipeline source and once with the candidate registration.
Every other runtime source already matches that target. Normal generation was
allowed in both runs; candidate generation was restored afterward. Its 632
`RECEIPT` lines match byte-for-byte: enum outputs/extrema, full real response DTOs,
reminder output and every accepted owning request, plus accepted/rejected reminder
binding. Both extracted receipts have SHA-256
`7b26509224a8f212da45dad38d87fd006de19bccf61c4e853345a130e246e54d`.
The six existing `BoundValues_KeepValidatorAndDomainResults` cases also passed
against target source and candidate source.

Receipt reproduction uses the candidate test source with the selected producer's
AddApiPipeline implementation and all other runtime sources held constant:

```sh
dotnet test tests/Orbit.Infrastructure.Tests --filter 'FullyQualifiedName~ProducerReceipt_RecordsActualMvcDtosAndRequestResults|FullyQualifiedName~BoundValues_KeepValidatorAndDomainResults' --logger 'console;verbosity=detailed' --nologo
```

Legacy mismatch assertions remain explicit and green as assertions of known debt;
they do not claim those files are accurate. The original legacy-path accuracy probe
still rejects all six original enum components. Selected-revision accuracy tests
read only the explicitly named r2 files.

## Ordinary compatibility and negative controls

Both actual-target legacy comparisons exited 0 with `No changes detected`:

```sh
oasdiff breaking origin/main:src/Orbit.Api/openapi.json src/Orbit.Api/openapi.json --fail-on ERR
oasdiff breaking origin/main:src/Orbit.Api/openapi_v2.json src/Orbit.Api/openapi_v2.json --fail-on ERR
```

There is no r2 artifact on the target commit, so r2 is genuinely first publication.
The planner emits that fact explicitly and does not substitute another stream as
its baseline. Future comparisons derive the actual same-stream target commit/path.

For each of the four actual generated artifacts, a negative copy deleting only
GET `/api/Habits` exited 1 with one `api-removed-without-deprecation` ERR. A separate
copy appending only an additional endpoint exited 0. These use actual complete
generated documents, with `oasdiff breaking <baseline-copy> <changed-copy> --fail-on ERR`.
Corrected-stream control baselines are copies of the first publication, not claims
of preexisting target artifacts. No deliberate mutation remains in publication.

The publication tests use actual generated files in temporary git histories.
They prove complete target comparison plans; strict first publication; rejection
of deletion, renaming, missing target artifacts, candidate-as-baseline, wrong
version/path/catalog shape, stale or uncommitted output and deregistration; and
acceptance of compatible endpoint additions. The installed actionlint 1.7.12 validated the complete workflow, including the
`needs.<job>.result` string and `needs.<job>.outputs` expression schema. Live GitHub
job execution remains a publication check. The required v1 status depends on
all extra comparisons, with an explicit dependency-result check so skipped or
failed dependent jobs cannot satisfy it. Its action pin and ERR semantics remain.

The existing `.oasdiff.yaml` and strengthened removal severity file are unchanged.
Historical compatibility evidence in the neighboring README remains historical.
