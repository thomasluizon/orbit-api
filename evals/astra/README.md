# Conversation replays

`en.json` and `pt-BR.json` are authored model plans for a reservation conversation in each locale. `large-account.json` is a case with 120 tasks.

`AiIntentServiceStreamingTests.ServerConversations.cs` and `AgentChatWriteHoldTests.DateContext.cs` replay `en.json` and `pt-BR.json`. They use the real pending-operation executor, the real store and the real provider request builder. No test reads `large-account.json`. `LargeAccountPayload_ProductionHabitReadKeepsMatchingItemsAtProvider` builds the same case in code. These tests make no provider call.
