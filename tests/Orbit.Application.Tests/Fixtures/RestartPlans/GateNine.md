# Restart rerun captures

These fixtures project the original `toolCalls` and `groundingEvidence` without
argument changes from the gate 9a run against API `74c6e4f5` and consumer `fba2380cc`.
Tests bind each redacted habit ID to the corresponding domain-created habit in the
returned row order. The six captured titles and proposed dates remain verbatim.

The Portuguese reject attempt returned `Denied` in staging. The report does not
include its denial reason or distinguish the redacted IDs. Its local replay tests
the captured per-habit dates with valid owned IDs; it cannot establish why the
original call was denied. The replacement-plan test cancels an earlier preview
before replaying this proposal and verifies the replacement remains held.

Prompt assertions cover the exact-count, smaller-set, seven-day and no-question
instructions sent at the model-service port. They do not establish that the live
model follows those instructions. The coordinator owns the real-call pass^3 rerun.

| Source attempt | SHA-256 |
| --- | --- |
| `composition-restart-approve-en-r1.json` | `7c3b6d01051b67bf07dab079dcfb7f8b660b8571c8cbf7a5185ffbcd56266732` |
| `composition-restart-approve-en-r3.json` | `cca10e6124e37f53a0fd50c42750e86c70add6c41bd0d780a2d05779bd4943c0` |
| `composition-restart-approve-pt-BR-r1.json` | `0f843bcd334cf01eb75688b8615fc370ea141c6e06bcfd86072d34db44995910` |
| `composition-restart-preference-en-r1.json` | `916e92cf3fef1db6ab64741b00465c17f964a5ea4158c8732b21957f4aff4d20` |
| `composition-restart-preference-pt-BR-r2.json` | `61e9642997e130eb3a64a65bd9c7daceb6d82dfe2b28346e0d9b44cf88f042f4` |
| `composition-restart-reject-pt-BR-r2.json` | `3e955c6373034b3219705a804432f8f2443636f44b0918373d3c88d46f18e322` |
