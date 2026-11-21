# Provider evidence for terminal ranking reads

The captures hold real `gpt-6-luna` Responses API requests at low reasoning effort. They also hold the response fields that the SDK reads.

The request prefix and the strict tool declarations come from `Orbit.Infrastructure.Tests/Fixtures/AiReservation/0.json.gz`. An added system message gives the composition instructions and the ranking capabilities. The user messages are synthetic English and Portuguese ranking requests. The files hold no account credential.

- `original-en.json.gz` captures the text-marker design. The output is only a `get_habit_ranking` function call.
- `en.json.gz` and `pt-BR.json.gz` capture the structured design. The output is a function call with `orbit_read_complete` set to true.

Refresh the files with `OPENAI_API_KEY` and `python3 capture.py`. `OPENAI_BASE_URL` can select the provider endpoint. This command calls the provider, not an app server.

`TerminalReadProviderTests` replays these responses through the SDK, `AiIntentService`, the chat handler, the operation executor and the ranking tool. The test requires one model call phase and one HTTP request for the complete read plan. The original response must continue, because it has no structured completion signal.
