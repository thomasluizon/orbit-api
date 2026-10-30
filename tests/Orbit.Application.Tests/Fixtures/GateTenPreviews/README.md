# Write preview replays

The captures come from production gate 10 on API `728b1ea5`, harness `029de9342`.
They retain the model's tool names, arguments and reported result status, plus
successful typed projections used to seed the read ports. Account identifiers
remain redacted. Tests assign identifiers from the seeded domain entities.

`single-create-pt-r1` captures the empty lookup followed by a held creation.
`edge-free-limit-en-r1` captures the empty lookup followed by a capacity refusal.
These plans reproduce the composition failures through the real query and
capacity paths rather than supplying the final answer as a mock.

The duplicate-selection, brain-dump and restart captures retain successful
structured plans. The checklist replay combines the unsuccessful lookup from
repeat 3 with the successful append call from repeat 1. Its stored fixture
already contains the requested step checked, so the preview must preserve that
item and append the step unchecked.

The prompt assertions pin the guidance for the model failures. Replays prove
the composition of structured plans; they do not measure model reliability.
The coordinator owns the real-call rerun.
