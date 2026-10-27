# Revision and question replay

These deterministic fixtures replay the two-turn conversation reported in
thomasluizon/orbit-tickets#1760 in English and Portuguese. They are derived replay
data, not new live provider captures. The response envelope and function call
shape come from the captured `TerminalReads/en.json.gz` response reviewed in
thomasluizon/orbit-api#929. Tool arguments and assistant words exercise the reported
conversation; pending and item IDs are bound to the first turn's real preview.

The installed OpenAI 2.14.0 assembly was decompiled with ILSpy before constructing
the assistant output. `MessageResponseItem.DeserializeMessageResponseItem` selects
assistant messages by `role`. `InternalResponsesAssistantMessage` reads and writes
the `content` array. `ResponseContentPart` dispatches `type: output_text` to
`InternalItemContentOutputText`, whose serializer reads and writes `text` and
`annotations`. These SDK serializers establish the response shape independently
of the fixture. Reproduce with `ilspycmd -t OpenAI.Responses.MessageResponseItem`,
then the three named types, against the installed OpenAI assembly.

`RevisionOpinion_ProviderReplayKeepsAnswerAndOnlyChangesTime` runs buffered provider
responses through the installed SDK, `AiIntentService`, the chat handler, operation
executor, revision tool and pending store. The handler's stream sink receives the
same composed sentence as JSON, with composition both enabled and disabled. The
test deliberately buffers provider output so it tests final chat composition
without inventing provider streaming events. Plain revision variants suppress the
same assistant text when `answer_question` is false.

The fourth response contains only preview narration. This proves the answer from
the revision response survives a later round. No live API key or app server is
needed to replay the fixtures.
