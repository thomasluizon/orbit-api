# Captured honesty tool plans

These tool arguments come from the Gate 6 failing-case rerun, harness `be8ab21e9`
and staging API `43886600`. They retain the trace producer's `name`, `arguments`
and `resultStatus` fields without inventing a provider wire response.

`ambiguity-en-r1` retains the incomplete choice question, which the server preserves;
the prompt owns asking for both the target and the change. `missing-query` retains
only the `query_habits` call from `grounding-missing-pt-r2`, including its false
completion flag. `edge-skipped-pt-r1` retains the dated habit lookup.

The replay boundary is `IAiIntentService`, whose `AiResponse` and `AiToolCall`
contracts belong to this repository. Tool results are produced by the real tools
and operation executor. These are tool-plan replays, not raw provider HTTP captures.
The original attempts and the fixture reset script are the coordinator's read-only
`run-26-g6rerun` evidence. Re-derive the captures from the named attempt JSON files
by selecting their `toolCalls` arrays, filtering the missing case to `query_habits`.

The additional complete clarification cases construct `AiResponse` at the same
repository-owned port. They model the expected structured output in both languages;
they are not provider captures or evidence of live model adherence. The empty-lookup
cases assert completion from the produced typed scope and completeness metadata.
Compound nonterminal reads assert continuation into an issued `ask` block. Neither
tool selection nor response composition depends on matching the person's message.

Stored option labels and habit titles stay verbatim. Locale rules apply to generated
questions and status messages.

## Copy for coordinator approval

These are all user-facing strings added or retained by this ticket. Braced values
come from the resolved allowance limits, and stored habit titles prefix skip text.

| Surface | en | pt-BR |
| --- | --- | --- |
| Pro allowance | You've reached your daily AI message limit ({0}). | Você atingiu seu limite diário de mensagens de IA ({0}). |
| Free allowance | You've reached your daily AI message limit ({0}). Upgrade to Pro for {1} messages per day. | Você atingiu seu limite diário de mensagens de IA ({0}). Assine o Pro para ter {1} mensagens por dia. |
| Missing habit | No matching habit was found, so its streak is unavailable. | Não encontrei um hábito correspondente para consultar a sequência. |
| Today's skip | skipped today | hábito pulado hoje |
| Dated skip | skipped on this date | hábito pulado nesta data |
| Prompt clarification example | Which habit would you like to change, and what would you like to change? | Qual hábito você quer mudar e o que quer mudar nele? |

Both locale pipelines receive the same model-facing instructions below. They
contain the localized examples; the application does not substitute those examples
for the model's answer.

```text
HABIT CLARIFICATION: When the person asks to change a habit without naming the target or the change, ask both in one short question. English: Which habit would you like to change, and what would you like to change? Portuguese: Qual hábito você quer mudar e o que quer mudar nele? For a choice block, allow free text so the person can describe the change along with their selection. Do not show a habit list or prepare a write for an unspecified change.

HABIT STREAK SCOPE: For a named activity's streak, first call query_habits with search and include_metrics true. For example, Quantos dias seguidos eu toquei piano? uses search piano. Do not substitute get_gamification_overview or get_period_analytics for a named habit's streak. Never call get_habit_metrics with an empty or invented habit_id. A missing match means that habit and its streak are unavailable, not zero. Do not add account levels, XP, achievements or unrelated totals. If this lookup answers the whole request, set orbit_read_complete true; an empty lookup needs no further tool or model call.
```

The model-facing skip label is `SKIPPED ON DATE` in both locale pipelines.
The schedule summary template is also shared:

```text
Returned schedule for {date:yyyy-MM-dd}: {done} done, {remaining} remaining, {skipped} skipped (top-level entries).
```

`remaining` excludes the completed and skipped entries. The final answer uses the
localized typed fallback above, rather than displaying this tool summary.
