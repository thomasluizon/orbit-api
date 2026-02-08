# Coding Conventions

**Analysis Date:** 2026-02-07

## Naming Patterns

**Files:**
- PascalCase for class files: `User.cs`, `CreateHabitCommand.cs`, `HabitsController.cs`
- DTOs and request/response records inline within controllers: `RegisterRequest`, `LoginRequest`
- Enum files match enum name: `HabitType.cs`, `TaskItemStatus.cs`
- Handler suffixes: `CommandHandler`, `QueryHandler` (e.g., `CreateHabitCommandHandler`)

**Functions/Methods:**
- PascalCase for all public methods: `GetUserId()`, `Create()`, `Handle()`, `ExecuteLogHabitAsync()`
- Async methods append `Async` suffix: `GetByIdAsync()`, `FindAsync()`, `InitializeAsync()`
- Private methods also PascalCase: `ValidatePassword()`, `ExecuteCreateHabitAsync()`
- Predicate/filter methods use clear intent: `FindAsync(h => h.UserId == request.UserId)`

**Variables:**
- camelCase for local variables: `testUserId`, `registerResponse`, `habitResult`, `authToken`
- camelCase for parameters: `userId`, `request`, `cancellationToken`
- Constants in UPPER_SNAKE_CASE: `TestUserPassword`, `DefaultConnection`
- Private fields with underscore prefix: `_testUserId`, `_client`, `_factory`, `_logs`

**Types:**
- PascalCase for all types (classes, records, enums, interfaces)
- Interface names prefixed with `I`: `IGenericRepository<T>`, `IAiIntentService`, `IUnitOfWork`
- Record types for DTOs and command/query objects: `LoginRequest`, `CreateHabitCommand`, `ChatResponse`
- Nullable reference types enabled: `string?`, `Guid?`, `IReadOnlyList<T>?`

## Code Style

**Formatting:**
- No .editorconfig or Prettier configuration detected
- 4-space indentation (standard C# convention)
- Braces on same line (ASP.NET Core convention): `public class User : Entity { ... }`
- Single-line properties: `public string Name { get; private set; }`

**Linting:**
- No explicit linting configuration (no .editorconfig or Roslyn analyzer config)
- Follow C# 13 / .NET 10.0 idioms
- Nullable reference types enabled: `<Nullable>enable</Nullable>` in .csproj

**Key Language Features Used:**
- Records for immutable DTOs: `public record CreateHabitCommand(...)`
- Primary constructors: `public class CreateHabitCommandHandler(IGenericRepository<Habit> habitRepository, ...)`
- Target-typed new expressions: `new User { ... }` instead of explicit type
- Implicit usings: `<ImplicitUsings>enable</ImplicitUsings>` in .csproj
- File-scoped namespaces: `namespace Orbit.Domain.Entities;`

## Import Organization

**Order:**
1. System namespaces: `using System;`, `using System.Text.RegularExpressions;`
2. Standard library/Microsoft: `using Microsoft.EntityFrameworkCore;`, `using Microsoft.AspNetCore.Mvc;`
3. NuGet packages: `using MediatR;`, `using FluentAssertions;`
4. Local project namespaces: `using Orbit.Domain.Common;`, `using Orbit.Api.Extensions;`

**Path Aliases:**
- No path aliases detected (no `using X = Y;`)
- Fully qualified namespaces used throughout

**Namespace Structure:**
- File-scoped namespaces: `namespace Orbit.Domain.Entities;` (no closing brace)
- Follows folder structure directly: Folder `Orbit.Application/Habits/Commands/` → namespace `Orbit.Application.Habits.Commands`

## Error Handling

**Patterns:**
- Result pattern for domain/application operations: `Result<T>` and `Result`
- Methods return `Result<T>` with either `.Value` or `.Error`
- Check with `if (result.IsFailure)` before accessing `.Value`
- Controllers return appropriate HTTP status codes based on result: `BadRequest()`, `Ok()`, `Unauthorized()`
- Exceptions thrown only for truly exceptional cases (e.g., claim parsing failures in `HttpContextExtensions`)

**Result Pattern Usage:**
```csharp
// Domain entity creation - returns Result<T>
var userResult = User.Create(request.Name, request.Email, request.Password);
if (userResult.IsFailure)
    return Result.Failure<Guid>(userResult.Error);

var user = userResult.Value;  // Safe access after IsFailure check

// Handler methods propagate failures
if (habitResult.IsFailure)
    return Result.Failure<Guid>(habitResult.Error);
```

**Validation:**
- Domain entities perform validation in static `Create()` factory methods
- Validation failures return `Result.Failure<T>()` with descriptive error messages
- Example from `User.cs`: Email regex validation, password requirements (8+ chars, uppercase, lowercase, digit)
- Example from `Habit.cs`: Frequency quantity > 0, days only when frequency quantity == 1

## Logging

**Framework:** `Microsoft.Extensions.Logging.ILogger<T>`

**Patterns:**
- Injected via constructor: `ILogger<ProcessUserChatCommandHandler> logger`
- Performance monitoring with `Stopwatch`: Multiple timers track DB, AI service, and actions execution
- Structured logging with string interpolation: `logger.LogInformation("Message with {Variable}", variable)`
- Emoji prefixes for visual scanning in logs:
  - `🚀` for starting operations
  - `🔵` for substeps
  - `✅` for successes
  - `❌` for errors
  - `🎯` for final summary
- Log levels used:
  - `LogInformation()` for major operations and metrics
  - `LogError()` for action failures
  - No Debug/Trace logging observed

**Example from ProcessUserChatCommand:**
```csharp
logger.LogInformation("🚀 Processing chat message: '{Message}'", request.Message);
logger.LogInformation("✅ Database queries completed in {ElapsedMs}ms (Habits: {HabitCount}, Tasks: {TaskCount})",
    dbStopwatch.ElapsedMilliseconds, activeHabits.Count, pendingTasks.Count);
```

## Comments

**When to Comment:**
- Minimal comments in production code
- Comments explain *why*, not *what*: See `RegisterCommandHandler.cs` line 18: `// Check if email already exists`
- Infrastructure concern clarity: `// Hash password (infrastructure concern)`
- Complex logic requiring domain knowledge gets brief explanation

**JSDoc/XMLDoc:**
- No observed usage of XML documentation comments (`/// <summary>`)
- Only test files have summary comments: `/// <summary>` in `AiChatIntegrationTests.cs`

## Function Design

**Size:**
- Handler methods: 20-40 lines typical
- Private helper methods: 10-20 lines
- Complex handlers like `ProcessUserChatCommandHandler.Handle()` reaches ~90 lines but stays focused on orchestration
- Methods follow single responsibility principle strictly

**Parameters:**
- Prefer dependency injection in constructors over method parameters
- Method parameters: command/query object + CancellationToken
- No excessive parameters (max 2-3 including CT)
- Example: `public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)`

**Return Values:**
- Async methods return `Task<T>` where T is Result<> or concrete type
- Queries return bare types (e.g., `IReadOnlyList<Habit>`)
- Commands/mutations return `Result<T>`
- Use `Result.Success()` and `Result.Failure()` factory methods consistently

**CancellationToken:**
- Always included as final parameter in async methods
- Passed down the call chain: handler → repository → EF Core
- Example: `habitRepository.FindAsync(..., cancellationToken)`

## Module Design

**Exports:**
- Handlers are public classes alongside command/query records
- Namespace structure enforces modularity: `Orbit.Application.Habits.Commands`, `Orbit.Application.Auth.Queries`
- Controllers are endpoints; MediatR delegates to handlers

**Barrel Files:**
- No barrel files observed (no index.ts equivalent)
- Direct imports using full namespace paths

**Access Modifiers:**
- Domain entities use `private set` for properties: `public string Name { get; private set; }`
- Private fields for collections: `private readonly List<HabitLog> _logs = [];`
- All static `Create()` factory methods are public
- Behavior methods (Log, MarkCompleted, etc.) are public
- Constructors are private on entities to force use of factory methods

**Example from Habit.cs:**
```csharp
public class Habit : Entity
{
    public string Title { get; private set; }  // Public getter, private setter
    public bool IsActive { get; private set; } = true;

    private readonly List<HabitLog> _logs = [];  // Private backing field
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();  // Public read-only collection

    private Habit() { }  // Force factory method

    public static Result<Habit> Create(...) { ... }  // Factory with validation
}
```

## Clean Architecture Layers

**Import Direction (Inward):**
- API → Application
- Application → Domain
- Infrastructure → Domain (interfaces only, no reverse dependencies)

**Example from HabitsController:**
- Imports from `Orbit.Application.Habits.Commands` (handler, mediator)
- Does not import from Infrastructure
- Uses extension method `HttpContext.GetUserId()` from `Orbit.Api.Extensions`

---

*Convention analysis: 2026-02-07*
