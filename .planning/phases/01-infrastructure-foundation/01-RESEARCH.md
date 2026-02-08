# Phase 1: Infrastructure Foundation - Research

**Researched:** 2026-02-07
**Domain:** .NET backend modernization -- migrations, validation, API docs, JWT upgrade, code cleanup
**Confidence:** HIGH

## Summary

Phase 1 modernizes the Orbit backend infrastructure across five independent work streams: replacing `EnsureCreated()` with EF Core migrations, adding FluentValidation via MediatR pipeline behavior, replacing Swashbuckle with Microsoft.AspNetCore.OpenApi + Scalar, upgrading JWT token handling from the legacy `System.IdentityModel.Tokens.Jwt` to `Microsoft.IdentityModel.JsonWebTokens`, and removing all task management code. These are well-understood, well-documented changes with mature ecosystem support.

The riskiest item is the EnsureCreated-to-migrations transition, which requires a baseline migration with empty `Up()`/`Down()` methods to avoid recreating tables that already exist. All other changes are straightforward library swaps or additions with clear migration paths. The task removal (CLEAN-01) touches AI code (AiActionType, AiAction, SystemPromptBuilder, ProcessUserChatCommand) in addition to the obvious entity/controller/command files.

**Primary recommendation:** Execute in dependency order -- migrations first (gates everything), then validation + OpenAPI + JWT in parallel, then task removal last (touches AI code that should be stable first).

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.EntityFrameworkCore | 10.0.2 | ORM + migrations | Already in project. Migrations are the standard schema management approach. |
| Microsoft.EntityFrameworkCore.Design | 10.0.2 | `dotnet ef` CLI tooling for migrations | Already in project. Required for `dotnet ef migrations add`. |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | PostgreSQL provider for EF Core | Already in project. |
| FluentValidation | 12.1.1 | Request validation rules | Industry standard .NET validation library. Separates validation from handlers. |
| FluentValidation.DependencyInjectionExtensions | 12.1.1 | Assembly scanning for validator registration | Auto-registers all `IValidator<T>` from assemblies via `AddValidatorsFromAssembly()`. |
| MediatR | 14.0.0 | CQRS mediator + pipeline behaviors | Already in project. `IPipelineBehavior<,>` is the integration point for validation. |
| Microsoft.AspNetCore.OpenApi | 10.0.2 | Built-in OpenAPI document generation | Microsoft's replacement for Swashbuckle. Serves `/openapi/v1.json`. |
| Scalar.AspNetCore | 2.12.36 | API explorer UI | Modern Swagger UI replacement. Reads OpenAPI spec from Microsoft.AspNetCore.OpenApi. |
| Microsoft.IdentityModel.JsonWebTokens | 8.15.0 | JWT token creation and validation | Official replacement for System.IdentityModel.Tokens.Jwt. 30% faster. |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.2 | JWT bearer auth middleware | Already in project. Internally uses JsonWebTokenHandler since ASP.NET Core 8. |

### Packages to Remove

| Library | Version | Why Remove |
|---------|---------|-----------|
| Swashbuckle.AspNetCore | 7.2.0 | Unmaintained. Removed from .NET 9+ templates. Replaced by Microsoft.AspNetCore.OpenApi + Scalar. |
| System.IdentityModel.Tokens.Jwt | 8.3.2 | Legacy. Microsoft recommends Microsoft.IdentityModel.JsonWebTokens. |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| FluentValidation | DataAnnotations | DataAnnotations couple validation to the model. FluentValidation separates concerns and integrates with MediatR pipeline. |
| FluentValidation | Manual validation in handlers | Scatters validation logic, harder to test, no pipeline integration. |
| Scalar.AspNetCore | Swagger UI (manual) | Swagger UI can be used with Microsoft.AspNetCore.OpenApi but Swashbuckle's UI package is unmaintained. Scalar is actively maintained. |
| Exception-based validation | Result-pattern validation | Exception approach is simpler to implement and the dominant pattern. Result-pattern is possible but adds complexity to the pipeline behavior. |

**Installation:**
```bash
# Add new packages
dotnet add src/Orbit.Application/Orbit.Application.csproj package FluentValidation --version 12.1.1
dotnet add src/Orbit.Application/Orbit.Application.csproj package FluentValidation.DependencyInjectionExtensions --version 12.1.1
dotnet add src/Orbit.Api/Orbit.Api.csproj package Microsoft.AspNetCore.OpenApi --version 10.0.2
dotnet add src/Orbit.Api/Orbit.Api.csproj package Scalar.AspNetCore --version 2.12.36

# Replace JWT package
dotnet remove src/Orbit.Infrastructure/Orbit.Infrastructure.csproj package System.IdentityModel.Tokens.Jwt
dotnet add src/Orbit.Infrastructure/Orbit.Infrastructure.csproj package Microsoft.IdentityModel.JsonWebTokens --version 8.15.0

# Remove Swashbuckle
dotnet remove src/Orbit.Api/Orbit.Api.csproj package Swashbuckle.AspNetCore

# Ensure dotnet ef tool is installed
dotnet tool update --global dotnet-ef
```

## Architecture Patterns

### Recommended Project Structure (additions for Phase 1)

```
src/
├── Orbit.Api/
│   ├── Middleware/
│   │   └── ValidationExceptionHandler.cs     # IExceptionHandler for validation errors
│   ├── OpenApi/
│   │   └── BearerSecuritySchemeTransformer.cs # JWT auth in OpenAPI docs
│   └── Program.cs                            # Updated: OpenApi, Scalar, MigrateAsync
├── Orbit.Application/
│   ├── Behaviors/
│   │   └── ValidationBehavior.cs             # IPipelineBehavior for FluentValidation
│   ├── Habits/
│   │   └── Validators/                       # Validators for habit commands/queries
│   │       ├── CreateHabitCommandValidator.cs
│   │       └── LogHabitCommandValidator.cs
│   └── Auth/
│       └── Validators/                       # Validators for auth commands/queries
│           ├── RegisterCommandValidator.cs
│           └── LoginQueryValidator.cs
├── Orbit.Infrastructure/
│   └── Services/
│       └── JwtTokenService.cs                # Updated: JsonWebTokenHandler
└── Orbit.Domain/
    └── (no changes in Phase 1)
```

### Pattern 1: MediatR Validation Pipeline Behavior

**What:** A generic `IPipelineBehavior<TRequest, TResponse>` that runs all registered `IValidator<TRequest>` validators before the handler executes. If validation fails, it throws a `ValidationException` before the handler is reached.

**When to use:** Every command and query that accepts user input.

**Example:**
```csharp
// Source: Milan Jovanovic's CQRS Validation pattern (verified against FluentValidation docs)
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count != 0)
            throw new ValidationException(failures);

        return await next();
    }
}
```

### Pattern 2: IExceptionHandler for Structured Validation Errors

**What:** ASP.NET Core 8+ `IExceptionHandler` catches `ValidationException` and returns a structured 400 response with field-level errors. This avoids try-catch in controllers.

**When to use:** Register globally. Catches all validation exceptions from the MediatR pipeline.

**Example:**
```csharp
// Source: ASP.NET Core 8+ IExceptionHandler pattern
internal sealed class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not FluentValidation.ValidationException validationException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        await httpContext.Response.WriteAsJsonAsync(new
        {
            type = "ValidationFailure",
            status = 400,
            errors
        }, cancellationToken);

        return true;
    }
}
```

### Pattern 3: OpenAPI + Scalar with JWT Bearer Auth (.NET 10)

**What:** Replace Swashbuckle's `AddSwaggerGen()` + `UseSwaggerUI()` with `AddOpenApi()` + `MapOpenApi()` + `MapScalarApiReference()`. JWT auth is configured via an `IOpenApiDocumentTransformer`.

**When to use:** In Program.cs, replacing all Swashbuckle configuration.

**Example:**
```csharp
// Source: Microsoft Learn - Customize OpenAPI documents (ASP.NET Core 10.0)
// In Program.cs:
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

// In pipeline:
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// BearerSecuritySchemeTransformer.cs:
internal sealed class BearerSecuritySchemeTransformer(
    IAuthenticationSchemeProvider authenticationSchemeProvider) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var authSchemes = await authenticationSchemeProvider.GetAllSchemesAsync();
        if (authSchemes.Any(s => s.Name == "Bearer"))
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, OpenApiSecurityScheme>
            {
                ["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    In = ParameterLocation.Header,
                    BearerFormat = "Json Web Token"
                }
            };

            foreach (var operation in document.Paths.Values.SelectMany(p => p.Operations))
            {
                operation.Value.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Id = "Bearer",
                            Type = ReferenceType.SecurityScheme
                        }
                    }] = Array.Empty<string>()
                });
            }
        }
    }
}
```

**IMPORTANT .NET 10 NOTE:** OpenAPI.NET 2.x (used by .NET 10) changed how `Reference` works on `OpenApiSecurityScheme`. The code above follows the official Microsoft Learn documentation for ASP.NET Core 10.0. If compilation errors occur around `Reference`, check the Microsoft.OpenApi v2 API surface -- the `Reference` property may need to be set differently or the `OpenApiSecurityScheme` for the requirement may need to reference by `Id` only.

### Pattern 4: JWT Token Generation with JsonWebTokenHandler

**What:** Replace `JwtSecurityToken` + `JwtSecurityTokenHandler.WriteToken()` with `JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor)`.

**When to use:** In `JwtTokenService.GenerateToken()`.

**Example:**
```csharp
// Source: Microsoft.IdentityModel.JsonWebTokens API docs
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

public string GenerateToken(Guid userId, string email)
{
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));

    var descriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        ]),
        Expires = DateTime.UtcNow.AddHours(_settings.ExpiryHours),
        Issuer = _settings.Issuer,
        Audience = _settings.Audience,
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
    };

    var handler = new JsonWebTokenHandler();
    return handler.CreateToken(descriptor);
}
```

**Key difference:** `JsonWebTokenHandler.CreateToken()` returns a `string` directly (the compact JWT). No need for the two-step `CreateToken()` + `WriteToken()` dance.

### Pattern 5: EF Core Baseline Migration

**What:** Create an initial migration that captures the current schema but has empty `Up()`/`Down()` methods, so EF Core records it in `__EFMigrationsHistory` without trying to recreate existing tables.

**When to use:** One-time transition from `EnsureCreated()` to migrations.

**Example:**
```bash
# Step 1: Generate the migration (from solution root)
dotnet ef migrations add BaselineMigration --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Step 2: Open the generated migration file and EMPTY the Up() and Down() methods:
# protected override void Up(MigrationBuilder migrationBuilder) { }
# protected override void Down(MigrationBuilder migrationBuilder) { }
# DO NOT modify the ModelSnapshot file -- it must reflect the current model

# Step 3: Apply the migration (creates __EFMigrationsHistory and records baseline)
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Step 4: Replace EnsureCreatedAsync() with MigrateAsync() in Program.cs
```

### Anti-Patterns to Avoid

- **FluentValidation.AspNetCore package:** Deprecated by the FluentValidation maintainers. Do NOT use it. Use FluentValidation + MediatR `IPipelineBehavior` instead.
- **Validation in controllers:** Do not add manual validation in controller actions. All validation flows through the MediatR pipeline.
- **Validation in domain entities for input concerns:** Domain entity `Create()` methods validate domain invariants (e.g., "name must not be empty"). FluentValidation handles input validation (e.g., "name must be 1-100 characters, no special characters"). These are complementary, not competing.
- **Keeping EnsureCreated alongside MigrateAsync:** They are mutually exclusive. Remove `EnsureCreatedAsync()` entirely after the baseline migration.
- **Modifying the ModelSnapshot during baseline migration:** Only empty the `Up()`/`Down()` methods in the migration file. The snapshot must reflect the current model for future migrations to work.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Request validation | Manual `if` checks in handlers | FluentValidation + MediatR pipeline | Centralized, testable, declarative rules. 50+ edge cases in string/number validation. |
| OpenAPI spec generation | Manual JSON/YAML OpenAPI files | Microsoft.AspNetCore.OpenApi | Auto-generated from controllers. Always in sync with code. |
| API explorer UI | Custom HTML pages for API docs | Scalar.AspNetCore | Full-featured API explorer with code generation, auth support. |
| JWT token creation | Manual base64 encoding of JWT parts | JsonWebTokenHandler.CreateToken | Security-critical code. Handles encoding, signing, claim serialization correctly. |
| Schema migrations | Raw SQL scripts | EF Core Migrations | Tracks migration history, supports rollback, generates SQL from model changes. |
| Validation error formatting | Custom error response middleware | IExceptionHandler + FluentValidation | Structured, consistent error responses. Standard ASP.NET Core 8+ pattern. |

**Key insight:** Every item in this phase replaces a hand-rolled or deprecated approach with the current ecosystem standard. The migration paths are well-documented because thousands of .NET projects have made these exact transitions.

## Common Pitfalls

### Pitfall 1: Baseline Migration Contains CreateTable Calls

**What goes wrong:** Running `dotnet ef migrations add BaselineMigration` generates a migration with `CreateTable` calls for all existing tables. If applied to the existing database, it fails with "relation already exists."
**Why it happens:** `EnsureCreated()` never populates `__EFMigrationsHistory`, so EF Core thinks no schema exists.
**How to avoid:** After generating the baseline migration, open the `.cs` file and empty the `Up()` and `Down()` method bodies completely. Leave the `ModelSnapshot` file untouched. Then run `dotnet ef database update` -- this creates `__EFMigrationsHistory` and records the baseline without running any SQL.
**Warning signs:** The generated migration file has `CreateTable`, `CreateIndex` calls in `Up()`. The `Designer.cs` file is normal -- only the main migration file needs emptying.

### Pitfall 2: OpenAPI.NET v2 Breaking Changes in .NET 10

**What goes wrong:** Code copied from .NET 9 examples using `OpenApiSecurityScheme.Reference` fails to compile or behaves differently because Microsoft.OpenApi 2.x changed the Reference handling.
**Why it happens:** .NET 10 ships with Microsoft.OpenApi 2.x which has breaking changes from 1.x. Many online examples still show the 1.x API.
**How to avoid:** Follow the official Microsoft Learn ASP.NET Core 10.0 documentation for OpenAPI customization. Use `IOpenApiDocumentTransformer` with `document.Components.SecuritySchemes` dictionary approach. Test that the `/scalar/v1` page shows the "Authorize" button.
**Warning signs:** Compilation errors mentioning `OpenApiReference`, `ReferenceType`, or `OpenApiSecurityScheme` properties. The Scalar UI loads but has no authorization option.

### Pitfall 3: JwtRegisteredClaimNames Namespace Confusion

**What goes wrong:** After replacing `System.IdentityModel.Tokens.Jwt` with `Microsoft.IdentityModel.JsonWebTokens`, code using `JwtRegisteredClaimNames` (e.g., `JwtRegisteredClaimNames.Jti`) may reference the wrong namespace or fail to resolve.
**Why it happens:** `JwtRegisteredClaimNames` exists in BOTH `System.IdentityModel.Tokens.Jwt` and `Microsoft.IdentityModel.JsonWebTokens`. After removing the old package, ensure the `using` directive points to the new namespace.
**How to avoid:** Replace `using System.IdentityModel.Tokens.Jwt;` with `using Microsoft.IdentityModel.JsonWebTokens;`. The `JwtRegisteredClaimNames` class has the same constants in both.
**Warning signs:** Build error: "The type or namespace 'JwtRegisteredClaimNames' could not be found."

### Pitfall 4: Validation Pipeline Runs for Queries Without Validators

**What goes wrong:** If `ValidationBehavior` is registered for ALL requests but some queries have no validators, the behavior either throws or does unnecessary work.
**Why it happens:** `IEnumerable<IValidator<TRequest>>` will be empty for requests without validators, not null.
**How to avoid:** In the `ValidationBehavior`, check `if (!validators.Any()) return await next();` at the start. This is a performance optimization and prevents edge cases.
**Warning signs:** Queries that had no validation suddenly slow down or throw unexpected exceptions.

### Pitfall 5: Task Removal Leaves Orphaned AI References

**What goes wrong:** Removing `TaskItem`, `TasksController`, and task commands/queries is straightforward. But the AI system has deep references to tasks: `AiActionType.CreateTask`, `AiActionType.UpdateTask`, `AiAction.TaskId`, `AiAction.NewStatus`, `SystemPromptBuilder` mentions tasks throughout, `ProcessUserChatCommand` has task execution methods, and `IAiIntentService.InterpretAsync` takes `IReadOnlyList<TaskItem>` as a parameter.
**Why it happens:** The AI was designed to handle both habits and tasks. Removing tasks requires updating the entire AI pipeline.
**How to avoid:** Track ALL task references across the codebase. The full list of files that reference task code:
  - `Orbit.Domain/Entities/TaskItem.cs` -- delete
  - `Orbit.Domain/Enums/TaskItemStatus.cs` -- delete
  - `Orbit.Domain/Enums/AiActionType.cs` -- remove `CreateTask`, `UpdateTask`
  - `Orbit.Domain/Models/AiAction.cs` -- remove `TaskId`, `NewStatus` properties
  - `Orbit.Domain/Interfaces/IAiIntentService.cs` -- remove `IReadOnlyList<TaskItem>` parameter
  - `Orbit.Application/Tasks/` -- delete entire folder (3 commands + 1 query)
  - `Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` -- remove task loading, task switch cases, task execution methods
  - `Orbit.Infrastructure/Persistence/OrbitDbContext.cs` -- remove `DbSet<TaskItem> Tasks` and TaskItem model config
  - `Orbit.Infrastructure/Services/SystemPromptBuilder.cs` -- remove task parameter, task context section, task examples, task action types
  - `Orbit.Infrastructure/Services/GeminiIntentService.cs` -- may reference TaskItem
  - `Orbit.Infrastructure/Services/AiIntentService.cs` -- may reference TaskItem
  - `Orbit.Api/Controllers/TasksController.cs` -- delete
  - `tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs` -- remove task-related test cases
**Warning signs:** Build errors after deletion (expected). Missing: runtime errors if any string-based reference to "CreateTask" remains in the system prompt but the handler no longer processes it.

### Pitfall 6: Integration Tests Break After Task Removal

**What goes wrong:** The 31 AI chat integration tests include task-related scenarios. After removing task code, these tests fail because the AI prompt still mentions tasks or the test expects task-related responses.
**Why it happens:** Tests send messages like "remind me to buy groceries" expecting a `CreateTask` action.
**How to avoid:** Identify which of the 31 tests are task-related and remove them. Update remaining tests to verify the AI correctly rejects task-like requests (or redirects them to habit creation).
**Warning signs:** Test compilation failures (references to deleted types) and test logic failures (AI returns unexpected responses after prompt changes).

## Code Examples

Verified patterns from official sources:

### FluentValidation Validator for Existing Commands

```csharp
// Example: CreateHabitCommand validator
// Source: FluentValidation docs pattern applied to Orbit commands
public sealed class CreateHabitCommandValidator : AbstractValidator<CreateHabitCommand>
{
    public CreateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("User ID is required.");

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Habit name is required.")
            .MaximumLength(200)
            .WithMessage("Habit name must be 200 characters or less.");

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity.HasValue)
            .WithMessage("Frequency quantity must be positive.");
    }
}
```

### DI Registration in Program.cs

```csharp
// Source: FluentValidation.DependencyInjectionExtensions + MediatR pipeline
builder.Services.AddValidatorsFromAssemblyContaining<CreateHabitCommandValidator>();

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(
        typeof(Orbit.Application.Chat.Commands.ProcessUserChatCommand).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddProblemDetails();
```

### Program.cs Migration Replacement

```csharp
// BEFORE:
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// AFTER:
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
    await db.Database.MigrateAsync();
}
```

### Scalar UI at /scalar/v1

```csharp
// BEFORE (Swashbuckle):
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => { /* ... JWT config ... */ });
// ...
app.UseSwagger();
app.UseSwaggerUI(options => { /* ... */ });

// AFTER (OpenAPI + Scalar):
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});
// ...
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();  // Available at /scalar/v1
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `EnsureCreated()` for DB setup | EF Core Migrations with `MigrateAsync()` | Always recommended; EnsureCreated was a dev shortcut | Schema changes are tracked, versioned, reversible |
| Swashbuckle.AspNetCore | Microsoft.AspNetCore.OpenApi + Scalar | .NET 9 (Nov 2024) removed from templates | First-party OpenAPI generation, modern UI |
| System.IdentityModel.Tokens.Jwt | Microsoft.IdentityModel.JsonWebTokens | IdentityModel 7.x (2023) declared legacy | 30% performance gain, actively maintained |
| FluentValidation.AspNetCore (auto MVC) | FluentValidation + MediatR IPipelineBehavior | FluentValidation maintainers deprecated AspNetCore package | Explicit validation in CQRS pipeline, not implicit MVC binding |
| Manual validation in handlers | FluentValidation pipeline behavior | Industry standard since MediatR + FluentValidation pairing | Centralized, testable, declarative |

**Deprecated/outdated:**
- `Swashbuckle.AspNetCore`: Unmaintained since 2023. Removed from .NET 9+ templates. Active compatibility issues with OpenAPI.NET 2.x.
- `System.IdentityModel.Tokens.Jwt`: Officially legacy. Microsoft explicitly recommends JsonWebTokens. Support ends Nov 2026 with .NET 8 LTS.
- `FluentValidation.AspNetCore`: Deprecated by FluentValidation maintainers. Anti-pattern for CQRS architectures.

## Open Questions

1. **OpenAPI.NET v2 Reference Property**
   - What we know: Microsoft Learn docs for ASP.NET Core 10.0 show `OpenApiSecurityScheme.Reference` in the document transformer. Community reports say OpenAPI.NET 2.x changed this.
   - What's unclear: Whether the exact code from Microsoft Learn compiles against the actual OpenAPI.NET 2.x packages shipped with .NET 10.
   - Recommendation: Implement per Microsoft Learn docs. If compilation fails, check the `Microsoft.OpenApi.Models` API for the current way to reference security schemes. This is LOW risk -- it only affects the Scalar authorize button, not auth functionality.

2. **Validation Behavior Constraint: Commands Only or All Requests?**
   - What we know: The common pattern constrains `ValidationBehavior` to command types only (via `where TRequest : ICommandBase`). This prevents validators running on queries.
   - What's unclear: Whether Orbit should validate queries too (e.g., validating `GetHabitsQuery.UserId` is not empty).
   - Recommendation: Apply validation to ALL request types (no constraint). Queries without validators will short-circuit via the `!validators.Any()` check. This is simpler and allows adding query validators later without changing the behavior registration.

3. **SecurityTokenDescriptor.Subject vs Claims Dictionary**
   - What we know: Newer versions of `SecurityTokenDescriptor` have a `Claims` dictionary property, and the old `Subject` (ClaimsIdentity) is marked obsolete in some versions.
   - What's unclear: Whether `Subject` is obsolete in the version (8.15.0) used by this project.
   - Recommendation: Use `Subject = new ClaimsIdentity(...)` for now, as it is well-documented and widely tested. If a deprecation warning appears during build, switch to the `Claims` dictionary approach.

## Task Removal Scope (CLEAN-01)

Full inventory of files to delete or modify:

### Files to DELETE

| File | Layer |
|------|-------|
| `src/Orbit.Domain/Entities/TaskItem.cs` | Domain |
| `src/Orbit.Domain/Enums/TaskItemStatus.cs` | Domain |
| `src/Orbit.Application/Tasks/Commands/CreateTaskCommand.cs` | Application |
| `src/Orbit.Application/Tasks/Commands/UpdateTaskCommand.cs` | Application |
| `src/Orbit.Application/Tasks/Commands/DeleteTaskCommand.cs` | Application |
| `src/Orbit.Application/Tasks/Queries/GetTasksQuery.cs` | Application |
| `src/Orbit.Api/Controllers/TasksController.cs` | Api |

### Files to MODIFY

| File | What to Change |
|------|----------------|
| `src/Orbit.Domain/Enums/AiActionType.cs` | Remove `CreateTask`, `UpdateTask` enum values |
| `src/Orbit.Domain/Models/AiAction.cs` | Remove `TaskId`, `NewStatus`, `DueDate` properties |
| `src/Orbit.Domain/Interfaces/IAiIntentService.cs` | Remove `IReadOnlyList<TaskItem> pendingTasks` parameter from `InterpretAsync` |
| `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs` | Remove `DbSet<TaskItem> Tasks` and TaskItem model configuration |
| `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` | Remove task parameter, task context section, task examples, task action type docs |
| `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` | Remove task parameter from InterpretAsync call |
| `src/Orbit.Infrastructure/Services/AiIntentService.cs` | Remove task parameter from InterpretAsync call |
| `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` | Remove task repository injection, task loading, `CreateTask`/`UpdateTask` switch cases, `ExecuteCreateTaskAsync`/`ExecuteUpdateTaskAsync` methods |
| `tests/Orbit.IntegrationTests/AiChatIntegrationTests.cs` | Remove task-related test scenarios |

### Migration for Task Table

After removing `DbSet<TaskItem> Tasks` and its model config from `OrbitDbContext`, create a migration:
```bash
dotnet ef migrations add RemoveTaskItems --project src/Orbit.Infrastructure --startup-project src/Orbit.Api
```
This will generate a migration that drops the `Tasks` table. This is expected and correct.

## Sources

### Primary (HIGH confidence)
- [Microsoft Learn: EnsureCreated vs Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created) -- official warning about EnsureCreated + Migrations incompatibility
- [Microsoft Learn: Migrations Overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) -- baseline migration approach
- [Microsoft Learn: Customize OpenAPI documents (ASP.NET Core 10.0)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi?view=aspnetcore-10.0) -- BearerSecuritySchemeTransformer code
- [Microsoft Learn: Using OpenAPI documents (ASP.NET Core 10.0)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/using-openapi-documents?view=aspnetcore-10.0) -- AddOpenApi + MapOpenApi + Scalar setup
- [Microsoft Learn: Security token events breaking change (.NET 8)](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/securitytoken-events) -- JwtSecurityToken to JsonWebToken migration
- [Microsoft Learn: JsonWebTokenHandler.CreateToken API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler.createtoken?view=msal-web-dotnet-latest) -- SecurityTokenDescriptor approach
- [NuGet: FluentValidation 12.1.1](https://www.nuget.org/packages/fluentvalidation/) -- version verified
- [NuGet: Microsoft.IdentityModel.JsonWebTokens 8.15.0](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens) -- version verified
- [NuGet: Scalar.AspNetCore 2.12.36](https://www.nuget.org/packages/Scalar.AspNetCore) -- version verified
- [NuGet: Microsoft.AspNetCore.OpenApi 10.0.2](https://www.nuget.org/packages/Microsoft.AspNetCore.OpenApi) -- version verified
- Orbit codebase analysis (direct code inspection) -- task removal scope

### Secondary (MEDIUM confidence)
- [Milan Jovanovic: CQRS Validation with MediatR Pipeline](https://www.milanjovanovic.tech/blog/cqrs-validation-with-mediatr-pipeline-and-fluentvalidation) -- ValidationBehavior pattern
- [EF Core migrations with existing database](https://cmatskas.com/ef-core-migrations-with-existing-database-schema-and-data/) -- baseline migration (empty Up/Down) approach
- [GitHub: dotnet/aspnetcore#54599](https://github.com/dotnet/aspnetcore/issues/54599) -- Swashbuckle removal announcement
- [GitHub: AzureAD/identitymodel#2006](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/2006) -- JWT library replacement recommendation
- [GitHub: dotnet/aspnetcore#61123](https://github.com/dotnet/aspnetcore/issues/61123) -- Microsoft.OpenApi 2.0 breaking changes

### Tertiary (LOW confidence)
- [Medium: Fixing OpenAPI Transform for Scalar in .NET 10](https://yogeshhadiya33.medium.com/fixing-openapi-transform-for-scalar-to-add-a-global-auth-token-in-net-10-5678f838cbec) -- .NET 10 OpenAPI security scheme workarounds (could not fetch, marked for validation)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all versions verified on NuGet, migration paths documented by Microsoft
- Architecture: HIGH -- patterns are well-established in .NET ecosystem (MediatR pipeline, IExceptionHandler, OpenAPI transformers)
- Pitfalls: HIGH -- EnsureCreated-to-migrations trap documented by Microsoft; task removal scope verified by codebase grep
- Code examples: HIGH for migrations, validation, JWT; MEDIUM for Scalar security (OpenAPI.NET v2 surface may differ)

**Research date:** 2026-02-07
**Valid until:** 2026-03-09 (30 days -- stable libraries, no fast-moving dependencies)
