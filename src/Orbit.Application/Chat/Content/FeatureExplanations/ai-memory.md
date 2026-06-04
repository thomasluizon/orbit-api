---
key: ai-memory
display_name: AI Memory
related_capabilities: [profile.ai-memory.write, user-facts.read]
related_surfaces: [ai-settings]
version: 1
derived_from:
  - src/Orbit.Application/Profile/Commands/SetAiMemoryCommand.cs Handle
  - src/Orbit.Application/UserFacts/Commands/CreateUserFactCommand.cs Handle
  - src/Orbit.Application/Common/AppConstants.cs MaxUserFacts
---

# AI Memory

AI memory lets the assistant remember compact facts about you across conversations, so you don't have to repeat context every time. **AI memory is a Pro feature** — the toggle to turn it on requires Pro.

## How it works

When memory is on, the assistant can save short facts it learns about you and recall them in later chats. You control this with a single on/off toggle.

## Limits

- Saved facts are capped at **50** (`MaxUserFacts`). Once you reach the cap, you'll need to delete some before new ones can be added.
- **Duplicate facts are rejected** — if a fact with the same text already exists (ignoring case), it won't be saved again.

## Turning it off

Turning memory off stops new facts from being stored. It's the switch that controls whether the assistant is allowed to remember anything new.
