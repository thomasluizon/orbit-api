# Composition replay provenance

The `g10-` captures retain the case ID, repeat, visible answer and structured tool calls from
the #26 production report using API `728b1ea5` and harness `029de9342`. Their source attempt
names match the capture filenames after removing `g10-`.

- `composition-compound-legacy-en-r3.json` and `composition-compound-legacy-pt-BR-r2.json`
  exercise text fallback when legacy card capabilities coexist with no parts support.
- `composition-goals-pt-BR-r1.json` exercises query and review results in one answer.
- `composition-api-keys-en-r1.json` and `composition-api-keys-pt-BR-r1.json` exercise a read
  held for emailed-code verification.

Tests replay the captured names and arguments at `IAiIntentService`. Repositories and queries
supply deterministic account data through the real tools, read projection factory and executor.
The verification tests exercise the runtime authorization bridge and real policy separately;
they never treat the captured pending read as a successful key listing. #26 owns the live
bilingual rerun. These files introduce no model calls or alternative evaluation harness.
