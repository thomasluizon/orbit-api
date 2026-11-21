# Conversation replays

`en.json` and `pt-BR.json` are authored model plans for a reservation conversation in each locale. `large-account.json` is a case with 120 tasks.

`AiIntentServiceStreamingTests.ServerConversations.cs` and `AgentChatWriteHoldTests.DateContext.cs` replay these files. They use the real pending-operation executor, the real store and the real provider request builder. They make no provider call.
