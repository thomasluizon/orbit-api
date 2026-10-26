# Provider evidence for terminal ranking reads

The captures contain real `gpt-6-luna` Responses API requests at low reasoning effort
and the response fields consumed by the SDK. The request prefix and strict tool
declarations come from `Orbit.Infrastructure.Tests/Fixtures/AiReservation/0.json.gz`.
The additional system message supplies the composition instructions and ranking
capabilities. The user messages are synthetic English and Portuguese ranking requests.
No account credentials are included. The response output items retain provider order.

`original-en.json.gz` captures the text-marker design: the output is only a
`get_habit_ranking` function call, with neither an assistant message nor the marker.
`en.json.gz` and `pt-BR.json.gz` capture the structured design: the output is still
only a function call, with the required `orbit_read_complete` boolean set to true.
Refresh with `OPENAI_API_KEY` and `python3 capture.py`; `OPENAI_BASE_URL` can select
the configured provider endpoint. This invokes the provider, not an app server.

`TerminalReadProviderTests` replays these responses through the installed SDK,
`AiIntentService`, the chat handler, the operation executor and the ranking tool.
It requires one model-call phase and one HTTP request for the complete read plan.
The original response must continue because it has no structured completion signal.

The installed OpenAI 2.14.0 assembly was decompiled with ILSpy to verify the
external fields. `ResponseResult.DeserializeResponseResult` maps `output` to
`OutputItems` in array order. `FunctionCallResponseItem` maps `call_id`, `name`
and `arguments` to `CallId`, `FunctionName` and `FunctionArguments`.
`MessageResponseItem` maps assistant `role` and `content`. The adapter scans all
message items for output text and all function items for calls, independently of
their relative order. An output containing only a function call therefore has no
framing text or text-marker completion signal.
