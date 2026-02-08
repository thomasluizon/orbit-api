---
phase: 01-infrastructure-foundation
plan: 01
subsystem: infra
tags: [ef-core, migrations, openapi, scalar, jwt, jsonwebtokens]

# Dependency graph
requires: []
provides:
  - EF Core migration infrastructure (baseline migration + MigrateAsync in startup)
  - OpenAPI document generation with JWT Bearer security scheme
  - Scalar API explorer UI at /scalar/v1
  - Modern JWT token generation via JsonWebTokenHandler
affects: [01-infrastructure-foundation, 02-habit-depth, 03-intelligence-layer]

# Tech tracking
tech-stack:
  added: [Microsoft.IdentityModel.JsonWebTokens 8.15.0, Microsoft.AspNetCore.OpenApi 10.0.2, Scalar.AspNetCore 2.12.36]
  removed: [System.IdentityModel.Tokens.Jwt 8.3.2, Swashbuckle.AspNetCore 7.2.0]
  patterns: [IOpenApiDocumentTransformer for security schemes, SecurityTokenDescriptor for JWT creation, baseline migration pattern]

key-files:
  created:
    - src/Orbit.Api/OpenApi/BearerSecuritySchemeTransformer.cs
    - src/Orbit.Infrastructure/Migrations/20260208013453_BaselineMigration.cs
    - src/Orbit.Infrastructure/Migrations/20260208013453_BaselineMigration.Designer.cs
    - src/Orbit.Infrastructure/Migrations/OrbitDbContextModelSnapshot.cs
  modified:
    - src/Orbit.Api/Program.cs
    - src/Orbit.Api/Orbit.Api.csproj
    - src/Orbit.Infrastructure/Orbit.Infrastructure.csproj
    - src/Orbit.Infrastructure/Services/JwtTokenService.cs

key-decisions:
  - "Used OpenAPI.NET 2.0 API with OpenApiSecuritySchemeReference (not v1 OpenApiReference pattern) for .NET 10 compatibility"
  - "SecurityTokenDescriptor.Subject (ClaimsIdentity) used for JWT claims -- no deprecation warning on 8.15.0"

patterns-established:
  - "IOpenApiDocumentTransformer: use for customizing OpenAPI documents (security schemes, metadata)"
  - "Baseline migration: empty Up/Down bodies to transition from EnsureCreated without recreating tables"
  - "JsonWebTokenHandler.CreateToken(SecurityTokenDescriptor): returns string directly, no WriteToken needed"

# Metrics
duration: 8min
completed: 2026-02-08
---

# Phase 1 Plan 1: Infrastructure Modernization Summary

**EF Core migrations (baseline), OpenAPI + Scalar API explorer, and JsonWebTokenHandler replacing three deprecated infrastructure components**

## Performance

- **Duration:** 8 min
- **Started:** 2026-02-08T01:32:37Z
- **Completed:** 2026-02-08T01:40:35Z
- **Tasks:** 2
- **Files modified:** 7 (4 modified, 3 created as migration files, 1 new transformer)

## Accomplishments
- Replaced EnsureCreated with MigrateAsync and generated empty baseline migration preserving existing database
- Replaced Swashbuckle with Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore for API documentation
- Replaced System.IdentityModel.Tokens.Jwt with Microsoft.IdentityModel.JsonWebTokens for JWT token generation
- Application starts successfully with migration infrastructure, Scalar UI, and JWT auth all working

## Task Commits

Each task was committed atomically:

1. **Task 1: EF Core baseline migration + JWT upgrade + package changes** - `8911d49` (feat)
2. **Task 2: OpenAPI/Scalar setup in Program.cs + BearerSecuritySchemeTransformer** - `5d147bc` (feat)

## Files Created/Modified
- `src/Orbit.Infrastructure/Orbit.Infrastructure.csproj` - Swapped System.IdentityModel.Tokens.Jwt for Microsoft.IdentityModel.JsonWebTokens
- `src/Orbit.Api/Orbit.Api.csproj` - Swapped Swashbuckle for Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore
- `src/Orbit.Infrastructure/Services/JwtTokenService.cs` - Rewritten to use JsonWebTokenHandler + SecurityTokenDescriptor
- `src/Orbit.Infrastructure/Migrations/20260208013453_BaselineMigration.cs` - Empty Up/Down baseline migration
- `src/Orbit.Infrastructure/Migrations/20260208013453_BaselineMigration.Designer.cs` - Auto-generated migration designer
- `src/Orbit.Infrastructure/Migrations/OrbitDbContextModelSnapshot.cs` - EF Core model snapshot (4 entities)
- `src/Orbit.Api/OpenApi/BearerSecuritySchemeTransformer.cs` - IOpenApiDocumentTransformer for JWT Bearer auth in OpenAPI docs
- `src/Orbit.Api/Program.cs` - Updated: MigrateAsync, AddOpenApi, MapOpenApi, MapScalarApiReference

## Decisions Made
- Used OpenAPI.NET 2.0 API surface (types in `Microsoft.OpenApi` namespace, `OpenApiSecuritySchemeReference` class) instead of v1 `OpenApiReference` pattern -- required for .NET 10 compatibility
- Used `SecurityTokenDescriptor.Subject` (ClaimsIdentity) for JWT claims rather than the `Claims` dictionary -- no deprecation warning on version 8.15.0
- Applied baseline migration to database during execution (DB was available)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] OpenAPI.NET 2.0 breaking changes required rewrite of BearerSecuritySchemeTransformer**
- **Found during:** Task 2 (BearerSecuritySchemeTransformer creation)
- **Issue:** Research code used `Microsoft.OpenApi.Models` namespace and v1 `OpenApiReference` pattern. .NET 10 ships OpenAPI.NET 2.0 where all types are in `Microsoft.OpenApi` namespace, `SecuritySchemes` is `IDictionary<string, IOpenApiSecurityScheme>`, and `OpenApiSecurityRequirement` keys are `OpenApiSecuritySchemeReference`
- **Fix:** Investigated OpenAPI.NET 2.0 API surface via reflection, rewrote transformer using correct v2 types: `IOpenApiSecurityScheme` for dictionary values, `OpenApiSecuritySchemeReference("Bearer", document)` for security requirements
- **Files modified:** src/Orbit.Api/OpenApi/BearerSecuritySchemeTransformer.cs
- **Verification:** Build succeeds with zero errors, app starts and serves API
- **Committed in:** 5d147bc (Task 2 commit)

**2. [Rule 1 - Bug] Fixed nullable reference warnings in BearerSecuritySchemeTransformer**
- **Found during:** Task 2 (BearerSecuritySchemeTransformer compilation)
- **Issue:** `document.Paths` and `p.Operations` could be null in OpenAPI.NET 2.0 types, producing CS8602/CS8603 warnings
- **Fix:** Added null guard for `document.Paths` (early return) and null-coalescing for `p.Operations ?? []`; also initialized `operation.Value.Security ??= []`
- **Files modified:** src/Orbit.Api/OpenApi/BearerSecuritySchemeTransformer.cs
- **Verification:** Build with zero warnings from project code
- **Committed in:** 5d147bc (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 bug)
**Impact on plan:** Both auto-fixes necessary due to OpenAPI.NET 2.0 breaking changes anticipated in research but not fully resolved until build-time validation. No scope creep.

## Issues Encountered
- Program.cs could not compile for migration generation until Swashbuckle references were removed (expected dependency between Task 1 and Task 2 -- resolved by removing Swashbuckle code in Task 1 with placeholder comments, then completing OpenAPI setup in Task 2)

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Migration infrastructure is ready for schema changes in plans 01-02 (validation) and 01-03 (task removal)
- Scalar UI is functional for API testing during development
- JWT auth continues to work with the new library (same claims, same validation parameters)
- Pre-existing MSB3277 warning in IntegrationTests project (EF Core Relational version conflict) is unrelated to this plan

## Self-Check: PASSED

- All 7 claimed files exist
- Both commit hashes (8911d49, 5d147bc) verified in git log
- Build succeeds with 0 errors
- Application starts and connects to database successfully

---
*Phase: 01-infrastructure-foundation*
*Completed: 2026-02-08*
