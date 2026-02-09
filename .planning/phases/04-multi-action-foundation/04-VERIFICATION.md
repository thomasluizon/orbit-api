---
phase: 04-multi-action-foundation
verified: 2026-02-09T19:52:20Z
status: gaps_found
score: 9/10 must-haves verified
re_verification: false
gaps:
  - truth: "When one habit in a bulk create fails validation, other habits still succeed"
    status: failed
    reason: "FluentValidation rejects entire request when any item has validation error"
    artifacts:
      - path: "src/Orbit.Application/Habits/Validators/BulkCreateHabitsCommandValidator.cs"
        issue: "RuleForEach validation runs before handler, causing 400 BadRequest"
    missing:
      - "Remove item-level validation from validator, rely on domain validation in handler"
---

# Phase 4: Multi-Action Foundation Verification Report

**Phase Goal:** AI can execute multiple actions per prompt with safe partial failure handling

**Verified:** 2026-02-09T19:52:20Z

**Status:** gaps_found

**Re-verification:** No (initial verification)

## Goal Achievement

### Observable Truths

9 of 10 truths verified (90%)

1. **User can request multiple habit creations in one prompt** - VERIFIED
   - Chat handler processes multiple CreateHabit actions (ProcessUserChatCommand.cs:90)
   - Test Chat_MultipleCreates_ShouldSucceedForAll passes

2. **User can log multiple habits at once** - VERIFIED
   - Handler supports multiple LogHabit actions
   - SystemPromptBuilder includes multi-log example

3. **AI returns habit breakdown with suggested sub-habits** - VERIFIED
   - AiActionType.SuggestBreakdown exists
   - AiAction.SuggestedSubHabits field present
   - Returns ActionStatus.Suggestion without DB write

4. **Partial failure handling in chat** - VERIFIED
   - Per-action try-catch wrapping (lines 94-134)
   - Single SaveChangesAsync after all actions
   - Successful entities persist despite later failures

5. **Chat shows per-action status** - VERIFIED
   - ChatResponse with ActionResult array
   - Contains Type, Status, EntityId, EntityName, Error fields

6. **Bulk create with parent-child relationships** - VERIFIED
   - BulkCreateHabitsCommand with nested SubHabits
   - Test BulkCreate_WithParentChild_ReturnsSuccess passes

7. **Bulk delete multiple habits** - VERIFIED
   - BulkDeleteHabitsCommand exists
   - Test BulkDelete_MultipleHabits_ReturnsSuccess passes

8. **Bulk create partial validation failure** - FAILED (BLOCKER)
   - FluentValidation rejects entire request on validation error
   - Test expects 200 with mixed results, gets 400 BadRequest
   - Validator runs BEFORE handler per-item try-catch

9. **Bulk delete partial failure** - VERIFIED
   - Per-item try-catch in handler
   - Test BulkDelete_PartialFailure_DeletesValidOnes passes

10. **Bulk create per-item status** - VERIFIED
    - BulkCreateResult with Results array
    - Each has Index, Status, HabitId, Error, Field

### Required Artifacts

All 10 artifacts verified (9 fully functional, 1 problematic):

- AiActionType.cs: SuggestBreakdown enum (8 lines) - VERIFIED
- AiAction.cs: SuggestedSubHabits field (22 lines) - VERIFIED
- ProcessUserChatCommand.cs: ActionResult + per-action handling (271 lines) - VERIFIED
- SystemPromptBuilder.cs: Multi-action examples + SuggestBreakdown (491 lines) - VERIFIED
- BulkCreateHabitsCommand.cs: Recursive SubHabits + partial success (144 lines) - VERIFIED
- BulkDeleteHabitsCommand.cs: Per-item try-catch (79 lines) - VERIFIED
- BulkCreateHabitsCommandValidator.cs: Validation (72 lines) - PROBLEMATIC (blocks partial success)
- HabitsController.cs: Bulk endpoints - VERIFIED
- AiChatIntegrationTests.cs: Multi-action tests - VERIFIED
- HabitsControllerTests.cs: Bulk tests - PARTIAL (1 of 6 fails)

### Key Links

All 7 key links verified as WIRED:

- Handler to SuggestBreakdown switch case
- Handler to ActionResult aggregation
- Prompt to SuggestedSubHabits schema
- Controller to BulkCreateCommand via MediatR
- Controller to BulkDeleteCommand via MediatR
- Handler to Habit.Create domain factory
- Handler to explicit AddAsync (EF Core gotcha)

### Requirements Coverage

All 5 MACT requirements SATISFIED:

- MACT-01: Multiple actions in single response - YES
- MACT-02: Independent action execution with error handling - YES
- MACT-03: AI habit decomposition - YES
- MACT-04: Log multiple habits at once - YES
- MACT-05: Per-action success/failure status - YES

Note: Validation gap affects bulk endpoint but not core chat pipeline requirements.

### Anti-Patterns

2 patterns found:

1. BLOCKER - BulkCreateHabitsCommandValidator line 19-20
   - RuleForEach with per-item validation in MediatR pipeline
   - Prevents partial success pattern
   - Entire request returns 400 on any validation error

2. INFO - BulkCreateHabitsCommand line 101
   - goto NextItem for control flow
   - Acceptable use case per user decision
   - Cleanly skips to next item when sub-habit fails

### Human Verification Required

4 items need human testing:

1. **Multi-Action Chat with Gemini** - Verify E2E AI behavior with complex multi-action prompts
2. **SuggestBreakdown UI Flow** - Test full confirmation flow from suggestion to bulk create
3. **Partial Failure UX** - Test error message clarity with mixed success/failure results
4. **Bulk Create Validation** - Once gap fixed, verify field-level error mapping

## Gaps Summary

### Gap 1: Bulk Create Validation Blocks Partial Success

**Truth:** When one habit in bulk create fails validation, other habits still succeed

**Status:** FAILED

**Root Cause:** FluentValidation MediatR pipeline behavior

**Technical Details:**
- BulkCreateHabitsCommandValidator uses RuleForEach for per-item validation
- Validation runs BEFORE handler in MediatR pipeline
- Empty title in item[1] causes 400 BadRequest for entire request
- Handler's per-item try-catch never executes
- Test expects 200 with mixed results, gets 400

**Impact:**
- Bulk create cannot achieve partial success for validation errors
- Chat pipeline unaffected (uses domain validation inside try-catch)
- Architecture inconsistency between chat and bulk endpoints

**Fix Options:**

**Option A (Recommended):** Remove item-level validation from validator
- Keep only structural validation (not empty, max 100 items)
- Let handler's Habit.Create() provide domain validation per item
- Pro: Matches chat pipeline pattern, enables partial success
- Con: Loses early validation feedback

**Option B:** Disable validation pipeline for bulk commands
- Custom MediatR behavior to skip validation
- Pro: Clean separation
- Con: Requires pipeline modification

**Option C:** Accept fail-fast validation
- Document 400 on any validation error
- Partial success only for domain errors
- Pro: Standard pattern
- Con: Doesn't meet success criteria

**Recommendation:** Option A - Remove per-item validation to achieve consistency with chat pipeline's domain validation pattern.

---

_Verified: 2026-02-09T19:52:20Z_
_Verifier: Claude (gsd-verifier)_
