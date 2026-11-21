# Conversation replays

`en.json` and `pt-BR.json` are authored model plans for a reservation conversation in each locale. `large-account.json` is a case with 120 tasks.

`AiIntentServiceStreamingTests.ServerConversations.cs` and `AgentChatWriteHoldTests.DateContext.cs` replay `en.json` and `pt-BR.json`. They use the real pending-operation executor, the real store and the real provider request builder. No test reads `large-account.json`. `LargeAccountPayload_ProductionHabitReadKeepsMatchingItemsAtProvider` builds the same case in code. These tests make no provider call.

`choice-settlement.json` is the case of ticket 2127: the person answers one choice, and the assistant must not ask the opposite question. `ProcessUserChatCommandHandlerTests.ChoiceSettlement_OneAnsweredChoiceSettlesTheOppositeQuestionAndReachesOnePreview` replays it in en and pt-BR from `tests/Orbit.Application.Tests/Fixtures/ChoiceSettlement/`. The model rounds are authored from the owner screenshot, not captured from the provider. The test makes no provider call.
