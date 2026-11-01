# Date and conversation replays

Tracks thomasluizon/orbit-tickets#1944 and the real-call harness in thomasluizon/orbit-tickets#26.

The bilingual reservation files are authored structured model plans derived from the
owner's report, not captured provider completions. Application tests replay their
tool output at `IAiIntentService`. They assert the approval preview and the context
supplied on the following turn for pending, approved, edited and rejected proposals.
The large-account case uses the production `query_habits` producer with 120 tasks
and inspects the real provider request builder through an in-process HTTP fake.

The SDK image-input tests use synthetic image bytes only to verify serialization.
The coordinator keeps the real reservation screenshot outside the repository and
owns the staging replay and model-quality assertions with the #26 harness. The
expected account-local result is December 31, 2026 at 20:30, followed by an answer
about that proposal without an ask block. These tests make no provider calls and
do not prove that a live model interprets the screenshot correctly.
