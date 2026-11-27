# Coding standards

The reviewer reads this file: Pullfrog and the local review. Workers do not load it. The reviewer reads it from the base branch with `git show origin/main:CODING_STANDARDS.md`, so a pull request never changes the rules that judge it.

## Contents

- How review uses this file
- Code
- Defects that no gate catches
- Tests
- Tests that lie
- Smells

## How review uses this file

1. Review has two axes, and each runs apart from the other. Standards asks "is it built right?" and reads this file. Spec asks "is it the right thing?" and reads the linked ticket. Report the two axes apart. Never merge or rerank them.
2. Every finding cites one rule id of this file, one smell name with the hunk, or one line of the ticket. A finding with no citation is not a finding.
3. Skip what an analyzer, the compiler, `dotnet format` or a test already enforces.
4. A rule of this file wins over a smell. A smell is always a judgement call.
5. Act on the findings that have a citation, then stop. Never review again and again until a review comes back clean.
6. A mechanical rule gets a deterministic check: an analyzer, a type, a test or a hook. A rule here names its check when it has one. A rule that fires on good code is deleted.

## Code

- CODE-1 **No workaround:** Find the root cause of each bug. Do not commit `TODO`, `FIXME`, `HACK`, an empty `catch`, or an unjustified `null!`.
- CODE-2 **No dead code:** Delete a method, a type, a parameter or a branch when your change leaves it unused.
- CODE-3 **Logging:** Inject `ILogger<T>`. Write the message in English with PascalCase properties. Use Debug for routine success and AI call detail. Use Information only for a rare business event. Use Warning for a recoverable fault. Use Error for a fault that needs action. Do not add a `LogError` that repeats an exception that Sentry captures.
- CODE-4 Validate each edge case: date and time ranges, numeric bounds, required fields, and fields that exclude each other. A frontend check does not count.

## Defects that no gate catches

Review checks each of these defects.

- DEFECT-1 A DTO field that you rename, remove or retype while a shipped client still reads it. Also `MinSupportedVersion` that you raise before the carrying build is live.
- DEFECT-2 A user-facing date that the code computes from a UTC instant.
- DEFECT-3 A background job that assumes the server-local "today".
- DEFECT-4 A field, a flag, an exit code or a response shape from an external interface, with no evidence in the pull request body. Confirm it against a real response or the installed source.

## Tests

- TEST-1 A handler test uses the real Application and Domain code. It mocks only the Infrastructure ports with NSubstitute.

## Tests that lie

A green check does not prove that the code is right. These tests pass while the code is wrong, so they do not count as the test that fails without a fix.

- LIE-1 Tautological test. The expected value restates the code: a constant asserted equal to the value it holds, or an expected value computed the way the code computes it. Example: `HabitLimits.MaxTitleLength.Should().Be(100)`. Take the expected value from an independent source: the ticket, a worked example, a recorded response. Check: `ORBIT0006`.
- LIE-2 Structure-sensitive test. The test reads a `.cs` source file as text and checks its layout, order or names. It breaks on a safe refactor and passes when the behaviour is wrong. Call the handler, the endpoint or the type instead. A repository-wide guard test (a ban that scans every file) lives in a `Guards` folder of its test project. Check: `ORBIT0005`.
- LIE-3 A test that cannot fail. A substitute replaces the code under test or hides its real failure modes. Substitute only a port that Infrastructure implements: a repository, the unit of work, a clock or date service, and an outside service (OpenAI, Stripe, Firebase, Amazon SES, VAPID). Never substitute a handler, `IMediator`, a domain type or an Application service in a test that a pull request adds or changes. When code is hard to test without that substitute, change its interface.
- LIE-4 A test that passes on the code before the fix does not prove the fix. Run it red before the fix.

## Smells

Each smell is a judgement call. Name it and quote the hunk. A rule above wins over it. Source: the smell baseline of the `/code-review` skill of Matt Pocock (MIT), after Martin Fowler, Refactoring, chapter 3.

- Mysterious Name. A name does not tell what a thing is or does. Fix: rename it to say its purpose.
- Duplicated Code. The same logic appears in more than one place. Fix: extract it once and call it from each place.
- Feature Envy. A method uses the data of another type more than its own data. Fix: move the method to the type that owns the data.
- Data Clumps. The same group of values travels together in fields or parameters. Fix: make one type for the group.
- Primitive Obsession. A raw string or number stands for a domain idea, such as a currency or a range. Fix: make a small type with a guard for the idea.
- Repeated Switches. The same switch or chain of conditions on one value appears in many places. Fix: replace the switch with polymorphism or one lookup.
- Shotgun Surgery. One change forces small edits in many classes. Fix: gather the related code in one place.
- Divergent Change. One class changes for many unrelated reasons. Fix: split the class by reason for change.
- Speculative Generality. The code has a hook, a parameter or an abstraction that no caller needs. Fix: delete it until a real need exists.
- Message Chains. A caller walks a long chain of calls through many objects. Fix: hide the chain behind one method on the first object.
- Middle Man. A class only forwards its calls to another class. Fix: call the other class directly and delete the middle class.
- Refused Bequest. A subclass ignores or rejects what it inherits. Fix: replace the inheritance with composition, or move the unused members down.
