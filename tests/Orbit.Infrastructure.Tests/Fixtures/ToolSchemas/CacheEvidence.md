# Astra cache evidence for ticket 1916

## Reproduction

`AiInputBudgetTests.LiveCache.cs` drives `ProcessUserChatCommandHandler`,
`SystemPromptBuilder`, `AiIntentService`, the installed SDK and a local HTTP transport.
The executor port returns typed read blocks through `ReadBlockFactory`; held items come
from `PendingOperationChangePreviewer`. Production offer planning issues actions between
turns. No OpenAI request is sent.

The account has 24 daily parents, two children each, eight tags, six goals and reminders.
IDs contain deterministic hash bytes so UUID token lengths remain representative.
The four messages, their order and conversation ID follow the work order. The clock
advances seven seconds per turn. The third request sees the water preview with both
children. One variant omits history; the other appends earlier transcript entries. The
current app area also changes between the second and third requests.

Repository reads have no `ORDER BY` in `GenericRepository.FindAsync` and `ProjectAsync`.
The fixture rotates their returned rows without changing any record. Previously, equal
or absent positions preserved this unspecified order in the prompt, including child
rows. Tags and offer account facts also inherited repository order. These are permitted
repository results, rather than an assertion about a specific PostgreSQL query plan.

The unchanged command on `4cdad930` exited zero with 27 tests:

```sh
dotnet test tests/Orbit.Infrastructure.Tests -c Release --filter 'FullyQualifiedName~AiInputBudgetTests'
```

After adding the sequence, the same command exited one: all six new cases failed while
the original 27 passed. Repeating against the original implementation with the final
boundary-aware estimator exited one with 10 failures and 23 passes. All six sequence
cases failed their cost assertion; the additional failures reflect the strengthened
layout and cache-boundary assertions. Restoring the implementation made all 33 pass.

The first changed raw HTTP request bytes on the original offer-client sequence were:

| Transition | History | Raw UTF-8 offset | Common model tokens | Producer at the difference |
| --- | --- | ---: | ---: | --- |
| turn 1 to 2 | absent | 242514 | 51991 | Parent habit row changes from routine 0 to routine 1 |
| turn 2 to 3 | absent | 258943 | 60377 | Rotated child row in `ActiveHabitsSection` |
| turn 3 to 4 | absent | 258943 | 60377 | Rotated child row in `ActiveHabitsSection` |
| turn 1 to 2 | growing | 240841 | 51626 | Transcript inserted before account context |
| turn 2 to 3 | growing | 241729 | 51813 | Transcript inserted before account context |
| turn 3 to 4 | growing | 242699 | 52020 | Transcript inserted before account context |

For turn 2 without history, 66,205 estimated input tokens minus 51,991 common tokens
leaves 14,214 tokens after the first difference. The ticket's staging measurement left
12,994 uncached tokens. The synthetic account is larger, so these offsets are not a
claim to reproduce the provider's exact tokenizer or staging account contents.

The fix orders account rows and relation lists deterministically. It sends the unchanged
system text and tool catalog first, then account indexes and offer account facts, then
date, client hints, placement, issued offers, preview state, history and the message.
The same fields, read requirements, approval gates and tool declarations remain present.
Neither message phrases nor habit titles choose tools.

For the corrected offer sequence, the reusable account boundary is about 65,920 tokens
after conservative rounding. Differences move to issued offers and the current app area,
after the complete account snapshot. Per-turn reports include raw byte offsets, token
prefix lengths, suffix bytes and costs under `Path.GetTempPath()` as
`orbit-1916-live-<client>-<history>.txt`. The estimator counts every chat provider call,
including seven calls for the legacy sequence and four for each composition sequence.
Only the static and account boundaries are prewarmed; no identical first message or
turn state is assumed. Preview and query identifiers use deterministic fixture values.

## Provider boundary and SDK evidence

The [OpenAI caching guide](https://developers.openai.com/api/docs/guides/prompt-caching)
defines eligible cache endpoints. Preserving arbitrary leading bytes is insufficient.
Chat requests add explicit boundaries after the static and account messages while keeping
implicit caching enabled. Older configured model families retain their existing request
shape. The added wire fragment on each reusable GPT-6 chat message is:

```json
{
  "type": "input_text",
  "text": "<existing prompt text>",
  "prompt_cache_breakpoint": { "mode": "explicit" }
}
```

The installed package is OpenAI 2.14.0. Its `openai.nuspec` identifies source revision
[`2e77b08828145f658ec04e49aec87abb1543c553`](https://github.com/openai/openai-dotnet/tree/2e77b08828145f658ec04e49aec87abb1543c553).
The installed assembly was decompiled, including `CreateResponseOptions`,
`ResponsePromptCacheRetentionPolicy`, `ResponseContentPart` and
`InternalItemContentInputText`. The evidence is:

- `CreateResponseOptions.PromptCacheRetentionPolicy` serializes `prompt_cache_retention`.
- The enum exposes `InMemory` (`in_memory`) and `Max24Hours` (`24h`). It does not expose
  `prompt_cache_options.ttl` or a typed content breakpoint.
- `InternalItemContentInputText.DeserializeInternalItemContentInputText` stores unknown
  JSON members in its `JsonPatch`; `JsonModelWriteCore` writes them back with `Patch.WriteTo`.
- `ModelReaderWriter.Read<ResponseContentPart>` therefore preserves the documented
  breakpoint without a new experimental API suppression. The local HTTP capture asserts
  the serialized field on both prefix messages and identical catalog bytes across turns.

To repeat the installed-source inspection with ILSpy:

```sh
ilspycmd -t OpenAI.Responses.CreateResponseOptions "$HOME/.nuget/packages/openai/2.14.0/lib/net10.0/OpenAI.dll"
ilspycmd -t OpenAI.Responses.ResponsePromptCacheRetentionPolicy "$HOME/.nuget/packages/openai/2.14.0/lib/net10.0/OpenAI.dll"
ilspycmd -t OpenAI.Responses.InternalItemContentInputText "$HOME/.nuget/packages/openai/2.14.0/lib/net10.0/OpenAI.dll"
```

## Daily retention model

The [Luna model page](https://developers.openai.com/api/docs/models/gpt-6-luna) gives
standard rates per million tokens: $0.10 input, $0.01 cached input, $0.125 cache writes
and $0.50 output. The [pricing table](https://platform.openai.com/pricing) lists the
12-hour write rate at $0.25. The guide distinguishes the minimum lifetime setting
`prompt_cache_options.ttl` (`30m` by default, or `12h`) from the older retention-policy
field. Setting the SDK's `Max24Hours` is not a request for the 12-hour lifetime.

Read-only production log command, using the service ID returned by `render services`:
Set `AI_COST_START_UTC` to midnight UTC on October 25, 2026 in RFC 3339 format.

```sh
render logs --resources srv-d6tc2isr85hc739bf75g --text 'AI cost' --start "$AI_COST_START_UTC" --limit 100 --output json
```

The real CLI returned consecutive JSON objects with `id`, `labels` (name/value pairs),
`message` and `timestamp`. Repeated daily snapshots from process restarts were
deduplicated by usage date, rather than added together. No production database was read.
The complete observed records are in [DailyCostEvidence.json](DailyCostEvidence.json).
Table dates use year/month/day.

| UTC usage date | All AI calls | Chat calls when available | Total USD |
| --- | ---: | ---: | ---: |
| 2026/10/25 | 87 | unavailable in this line | 0.0310 |
| 2026/10/26 | 107 | unavailable in this line | 0.1270 |
| 2026/10/27 | 99 | unavailable in this line | 0.0488 |
| 2026/10/28 | 106 | unavailable in this line | 0.0364 |
| 2026/10/29 | 200 | unavailable in this line | 0.3355 |
| 2026/10/30 | 1002 | 832 | 3.7336 |
| 2026/10/31 | 1328 | 1160 | 3.6374 |

The full 2026/10/31 line was emitted at 2026/11/01T00:00:44.389724613Z and repeated at
startup. It records chat cost $3.6030, 26,073,873 uncached input tokens, 27,581,261 cached
input tokens and 139,914 output tokens. The quota summary records mean messages per active
user 2.00 and p95 3. The 2026/11/01 partial line at 08:38:23 UTC has 17 AI calls and no
chat-purpose entry. It cannot validate this change or supply a full-day chat mean.

Aggregate provider calls include agent rounds and evaluation traffic. They do not prove
that this person's prefix is reused hundreds of times or reveal gaps between sessions.
The owner-spacing model therefore uses two sessions two hours apart, with one to two
turns each, rather than treating all 1,160 calls as one warm conversation.

Inputs: reusable prefix `P = 66,000` tokens, changing suffix `V = 1,000` tokens,
output `O = 204` tokens, `N` daily calls and `S` sessions. Prefix size rounds the captured
65,920-token offer account boundary; suffix size approximates the preview/history tail.
Output rounds up the ticket's 814 tokens over four calls. Each session's first call is
cold with default retention, later calls are within 30 minutes, and all sessions fit
within 12 hours. This deliberately assumes no helpful reuse from other users and no
survival beyond the minimum lifetime. A read does not restart twelve-hour coverage.

Costs per call, in USD:

```text
default = (S*P*0.125 + (N-S)*P*0.01 + N*V*0.125 + N*O*0.50) / (N*1000000)
extended = (P*0.25 + (N-1)*P*0.01 + N*V*0.25 + N*O*0.50) / (N*1000000)
```

| Calls | Sessions | Default mean USD | Extended mean USD |
| ---: | ---: | ---: | ---: |
| 1 | 1 | 0.0084770 | 0.0168520 |
| 2 | 2 | 0.0084770 | 0.0089320 |
| 3 | 2 | 0.0059470 | 0.0062920 |
| 4 | 2 | 0.0046820 | 0.0049720 |
| 3 | 3 | 0.0084770 | 0.0062920 |

Decision: keep default retention. Extended retention increases the modeled mean for
two sessions, including the latest logged mean of two account messages. Three separate
cold sessions within twelve hours make it cheaper; the daily logs cannot establish that
distribution. A hotter shared static prefix further reduces default costs. Prefix changes,
more than twelve hours between sessions or higher variable input reduce extended savings.
These daily cold-start estimates are separate from the warm four-turn $0.0009 gate.
