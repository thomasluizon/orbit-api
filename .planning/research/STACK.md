# Stack Research

**Domain:** AI-powered habit tracking backend (milestone extension)
**Researched:** 2026-02-07
**Confidence:** HIGH

## Current Stack (Unchanged)

These technologies are already in the project and remain correct for this milestone.

| Technology | Version | Purpose | Status |
|------------|---------|---------|--------|
| .NET | 10.0 | Runtime & SDK | Keep - LTS release |
| C# | 13 | Language | Keep |
| PostgreSQL | (latest) | Primary database | Keep |
| MediatR | 14.0.0 | CQRS mediator | Keep (see licensing note below) |
| BCrypt.Net-Next | 4.0.3 | Password hashing | Keep - latest stable |

## Recommended Stack Changes

### Packages to Add

| Technology | Version | Purpose | Why Recommended | Confidence |
|------------|---------|---------|-----------------|------------|
| FluentValidation | 12.1.1 | Request validation | Industry standard for .NET validation. Integrates with MediatR pipeline behaviors to validate commands/queries before they hit handlers. Eliminates manual validation in domain entities for input concerns. | HIGH |
| FluentValidation.DependencyInjectionExtensions | 12.1.1 | DI registration for validators | Auto-registers all IValidator<T> implementations from assemblies. Required companion to FluentValidation in DI-based apps. | HIGH |
| Microsoft.AspNetCore.OpenApi | 10.0.2 | OpenAPI document generation | Microsoft's built-in replacement for Swashbuckle. Swashbuckle is deprecated and unmaintained since .NET 8. This is now the official way to generate OpenAPI specs. | HIGH |
| Scalar.AspNetCore | 2.12.36 | API explorer UI | Modern replacement for Swagger UI. The MEMORY.md already references it but the codebase still uses Swashbuckle 7.2.0. Clean, interactive API explorer with built-in code generation. | HIGH |

### Packages to Upgrade

| Technology | Current | Target | Why | Confidence |
|------------|---------|--------|-----|------------|
| Microsoft.EntityFrameworkCore | 10.0.2 | 10.0.2 | Already current. Named query filters (new in EF10) directly useful for soft-delete and per-user filtering patterns. | HIGH |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | 10.0.0 | Already current. Supports EF10 JSON complex type mapping and improved nested scalar collection queries. | HIGH |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.2 | 10.0.2 | Already current. | HIGH |
| Microsoft.EntityFrameworkCore.Design | 10.0.2 | 10.0.2 | Already current. Required for migrations CLI tooling (`dotnet ef migrations add`). | HIGH |

### Packages to Replace

| Current | Replace With | Version | Why | Confidence |
|---------|-------------|---------|-----|------------|
| System.IdentityModel.Tokens.Jwt 8.3.2 | Microsoft.IdentityModel.JsonWebTokens | 8.15.0 | System.IdentityModel.Tokens.Jwt is officially legacy as of IdentityModel 7x. Microsoft recommends Microsoft.IdentityModel.JsonWebTokens as the replacement -- it provides 30% better performance and is actively maintained. The old package will only be supported through .NET 8 LTS lifetime (Nov 2026). | HIGH |
| Swashbuckle.AspNetCore 7.2.0 | Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore | 10.0.2 / 2.12.36 | Swashbuckle was removed from .NET 9 templates because it is unmaintained. Microsoft now provides built-in OpenAPI support via Microsoft.AspNetCore.OpenApi. Scalar provides the interactive UI. | HIGH |

### Packages to Remove

| Package | Why Remove | Confidence |
|---------|-----------|------------|
| Swashbuckle.AspNetCore 7.2.0 | Deprecated, unmaintained, replaced by Microsoft.AspNetCore.OpenApi + Scalar. | HIGH |
| System.IdentityModel.Tokens.Jwt 8.3.2 | Legacy. Replace with Microsoft.IdentityModel.JsonWebTokens. | HIGH |

## No New Libraries Needed For

These features do NOT require additional packages -- they are implemented with EF Core and standard .NET:

| Feature | Implementation Approach | Why No New Package |
|---------|------------------------|--------------------|
| Sub-habits (parent-child hierarchy) | Self-referencing FK on Habit entity (`ParentHabitId`) | Standard EF Core self-referencing relationship pattern. No library needed. |
| Bad habits (negative tracking) | New `IsNegative` bool property on Habit entity | Domain modeling concern, not a library concern. |
| Tags | PostgreSQL `text[]` array column on Habit entity | Npgsql EF Core already supports array columns with full LINQ querying (EF Core 8+). The project already uses this pattern for `Days`. No join table or tag library needed. |
| Progress metrics (streaks, completion rates) | Domain service or Application query handler with LINQ aggregation | Pure computation over HabitLog data. Streak = consecutive date counting. Completion rate = logs / expected. No stats library warranted. |
| User profiles | Extend existing User entity with profile fields | Domain entity change + migration. |
| EF Core migrations | Built-in `dotnet ef` CLI tooling | Microsoft.EntityFrameworkCore.Design is already a dependency. |

## MediatR Licensing Note

MediatR 14.0.0 uses a dual-license model (RPL-1.5 + commercial) since July 2025. A free Community license is available for companies under $5M annual revenue and under $10M total outside capital. For a personal/small project like Orbit, the Community license applies at no cost, but you must register for a license key (no runtime enforcement). Previous versions (pre-13.0) remain MIT-licensed.

**Recommendation:** Continue using MediatR 14.0.0. The Community license covers this project. If licensing becomes a concern, the only viable alternative is building a simple in-process mediator (not worth the effort for this scope).

## EF Core 10 Features to Leverage

These EF Core 10 features are directly relevant to this milestone:

| Feature | Use Case in Orbit | Notes |
|---------|-------------------|-------|
| Named Query Filters | Soft-delete filtering (`IsActive`) + per-user data isolation (`UserId == currentUser`) as separate, independently toggleable filters | Replaces single `.HasQueryFilter()` with named filters that can be selectively disabled. |
| LeftJoin LINQ operator | Progress metrics queries joining Habits with HabitLogs | Cleaner syntax than manual GroupJoin + SelectMany. |
| Complex Types (optional) | Could model user profile details as a complex type on User | Value semantics, maps to same table columns. Optional -- regular properties also work fine. |

## Installation

```bash
# Add new packages (from Orbit.Application project)
dotnet add src/Orbit.Application/Orbit.Application.csproj package FluentValidation 12.1.1
dotnet add src/Orbit.Application/Orbit.Application.csproj package FluentValidation.DependencyInjectionExtensions --version 12.1.1

# Replace JWT package (in Orbit.Infrastructure project)
dotnet remove src/Orbit.Infrastructure/Orbit.Infrastructure.csproj package System.IdentityModel.Tokens.Jwt
dotnet add src/Orbit.Infrastructure/Orbit.Infrastructure.csproj package Microsoft.IdentityModel.JsonWebTokens --version 8.15.0

# Replace Swashbuckle with OpenAPI + Scalar (in Orbit.Api project)
dotnet remove src/Orbit.Api/Orbit.Api.csproj package Swashbuckle.AspNetCore
dotnet add src/Orbit.Api/Orbit.Api.csproj package Microsoft.AspNetCore.OpenApi --version 10.0.2
dotnet add src/Orbit.Api/Orbit.Api.csproj package Scalar.AspNetCore --version 2.12.36

# Verify EF tools are installed globally
dotnet tool update --global dotnet-ef
```

## Alternatives Considered

| Recommended | Alternative | Why Not |
|-------------|-------------|---------|
| FluentValidation 12.1.1 | Manual validation in handlers | Manual validation scatters validation logic, makes it harder to test, and misses the MediatR pipeline integration pattern. FluentValidation is the de facto standard. |
| FluentValidation 12.1.1 | DataAnnotations | DataAnnotations couple validation to the model. FluentValidation separates concerns and supports complex validation rules (cross-property, async, conditional). |
| PostgreSQL `text[]` for tags | Many-to-many join table (Tag entity) | Tags are simple strings with no metadata. Array columns are native to PostgreSQL, require no join table, and Npgsql supports full LINQ querying (`Contains`, `Any`, etc.). The project already uses arrays for `Days`. A join table adds unnecessary complexity for a flat string list. |
| PostgreSQL `text[]` for tags | JSON column for tags | Array columns use PostgreSQL's native binary encoding (more efficient than JSON). LINQ support is identical. Arrays are the idiomatic PostgreSQL approach for primitive collections. |
| Self-referencing FK for sub-habits | Separate SubHabit entity | A separate entity duplicates the Habit schema. Self-referencing keeps one entity type with a nullable `ParentHabitId`. Standard EF Core pattern with well-understood loading strategies. |
| Scalar.AspNetCore | Swagger UI (manual integration) | Swagger UI can still be used manually, but Swashbuckle is unmaintained. Scalar provides a superior developer experience with built-in code generation and modern design. |
| Microsoft.IdentityModel.JsonWebTokens | Keep System.IdentityModel.Tokens.Jwt | The old package is officially legacy. Microsoft explicitly recommends the replacement. 30% performance gain. Will lose support after Nov 2026. |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| Swashbuckle.AspNetCore | Unmaintained since .NET 8. Removed from .NET 9+ templates. Active security and compatibility risks. | Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore |
| System.IdentityModel.Tokens.Jwt | Officially legacy. Microsoft recommends replacement. Support ends Nov 2026. | Microsoft.IdentityModel.JsonWebTokens |
| AutoMapper | Now commercial (same licensing as MediatR). For this project's scope, manual mapping is clearer and avoids the dependency. The project already uses manual mapping. | Manual mapping (record DTOs, factory methods) |
| FluentValidation.AspNetCore | Deprecated by FluentValidation maintainers. Automatic validation in the MVC pipeline is an anti-pattern for CQRS -- use MediatR pipeline behaviors instead. | FluentValidation + MediatR IPipelineBehavior<,> |
| EF Core `EnsureCreated()` | Cannot be used alongside migrations. Creates schema without migration history. Must be replaced before adding migrations. | `context.Database.MigrateAsync()` in startup |
| A dedicated stats/metrics library (e.g., MathNet.Numerics) | Overkill. Habit metrics are simple aggregations (streak counting, completion percentage, totals). LINQ + DateOnly arithmetic covers all needs. | LINQ queries in Application layer query handlers |
| HangFire / Quartz.NET (background jobs) | Not needed for this milestone. Metrics are computed on-demand, not scheduled. Add background jobs only if/when push notifications or daily summaries are introduced. | On-demand computation in query handlers |

## Version Compatibility

| Package | Compatible With | Notes |
|---------|-----------------|-------|
| FluentValidation 12.1.1 | .NET 10.0, MediatR 14.0.0 | Works with any MediatR version via IPipelineBehavior. |
| Microsoft.IdentityModel.JsonWebTokens 8.15.0 | .NET 10.0, Microsoft.AspNetCore.Authentication.JwtBearer 10.0.2 | JwtBearer internally uses JsonWebTokenHandler since ASP.NET Core 8.0. Drop-in compatible. |
| Scalar.AspNetCore 2.12.36 | .NET 10.0, Microsoft.AspNetCore.OpenApi 10.0.2 | Scalar reads the OpenAPI document generated by Microsoft.AspNetCore.OpenApi. |
| Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 | Microsoft.EntityFrameworkCore 10.0.2 | Provider version tracks EF Core major version. 10.0.0 is the stable release for EF10. |

## Sources

- [NuGet - FluentValidation 12.1.1](https://www.nuget.org/packages/fluentvalidation/) -- Version verified
- [NuGet - FluentValidation.DependencyInjectionExtensions 12.1.1](https://www.nuget.org/packages/fluentvalidation.dependencyinjectionextensions/) -- Version verified
- [NuGet - Microsoft.AspNetCore.OpenApi 10.0.2](https://www.nuget.org/packages/Microsoft.AspNetCore.OpenApi) -- Version verified
- [NuGet - Scalar.AspNetCore 2.12.36](https://www.nuget.org/packages/Scalar.AspNetCore) -- Version verified
- [NuGet - Microsoft.IdentityModel.JsonWebTokens 8.15.0](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens) -- Version verified via System.IdentityModel.Tokens.Jwt NuGet page recommendation
- [NuGet - System.IdentityModel.Tokens.Jwt 8.15.0](https://www.nuget.org/packages/system.identitymodel.tokens.jwt/) -- Legacy status confirmed
- [What's New in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew) -- Named query filters, complex types, LeftJoin
- [Breaking Changes in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes) -- Migration transaction changes
- [Npgsql EF Core 10.0 Release Notes](https://www.npgsql.org/efcore/release-notes/10.0.html) -- JSON complex type, array column improvements
- [MediatR Licensing](https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/) -- Community license details, $5M threshold
- [Swashbuckle Removal Announcement](https://github.com/dotnet/aspnetcore/issues/54599) -- Official .NET 9 deprecation
- [Npgsql Array Type Mapping](https://www.npgsql.org/efcore/mapping/array.html) -- PostgreSQL array column LINQ support
- [EF Core Self-Referencing Entities](https://medium.com/@dmitry.pavlov/tree-structure-in-ef-core-how-to-configure-a-self-referencing-table-and-use-it-53effad60bf) -- Parent-child pattern
- [MediatR Pipeline + FluentValidation](https://www.milanjovanovic.tech/blog/cqrs-validation-with-mediatr-pipeline-and-fluentvalidation) -- Validation behavior pattern
- [EF Core Migrations vs EnsureCreated](https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created) -- Transition strategy

---
*Stack research for: Orbit AI habit tracker - backend extension milestone*
*Researched: 2026-02-07*
