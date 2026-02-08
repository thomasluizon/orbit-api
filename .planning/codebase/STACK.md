# Technology Stack

**Analysis Date:** 2026-02-07

## Languages

**Primary:**
- C# 13 - All backend code across domain, application, infrastructure, and API layers

## Runtime

**Environment:**
- .NET 10.0 - Target framework for all projects (SDK projects with implicit usings and nullable enabled)

**Package Manager:**
- NuGet - Implicit package restoration via .NET tooling
- Lockfile: Project.assets.json (generated per project)

## Frameworks

**Web:**
- ASP.NET Core 10.0.2 - HTTP API, routing, middleware pipeline, DI container
  - Controllers: `src/Orbit.Api/Controllers/` - MVC-style endpoint handling
  - Configuration: `src/Orbit.Api/Program.cs` - Startup and service registration

**CQRS:**
- MediatR 14.0.0 - Command/Query pattern implementation
  - Queries: `src/Orbit.Application/Auth/Queries/`, `src/Orbit.Application/Habits/Queries/`, `src/Orbit.Application/Tasks/Queries/`
  - Commands: `src/Orbit.Application/Auth/Commands/`, `src/Orbit.Application/Chat/Commands/`, `src/Orbit.Application/Habits/Commands/`, `src/Orbit.Application/Tasks/Commands/`
  - Pipeline configured in `src/Orbit.Api/Program.cs` line 81-82

**ORM:**
- Entity Framework Core 10.0.2 - Database mapping and LINQ queries
  - DbContext: `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs`
  - Repository pattern: `src/Orbit.Infrastructure/Persistence/GenericRepository.cs`
  - Unit of Work: `src/Orbit.Infrastructure/Persistence/UnitOfWork.cs`

**Authentication:**
- JWT Bearer Authentication (ASP.NET Core 10.0.2) - Token-based API security
  - Configuration: `src/Orbit.Api/Program.cs` lines 32-51
  - Implementation: `src/Orbit.Infrastructure/Services/JwtTokenService.cs`
  - Settings: `src/Orbit.Infrastructure/Configuration/JwtSettings.cs`

**Password Hashing:**
- BCrypt.Net-Next 4.0.3 - Secure password hashing with salt
  - Implementation: `src/Orbit.Infrastructure/Services/PasswordHasher.cs`

**API Documentation:**
- Swashbuckle.AspNetCore 7.2.0 - Swagger/OpenAPI UI generation
  - Configured in `src/Orbit.Api/Program.cs` lines 93-130
  - Accessible at root path in development

## Key Dependencies

**Database:**
- Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 - PostgreSQL integration with EF Core
  - Connection string: `appsettings.json` - `ConnectionStrings.DefaultConnection`
  - Configured in `src/Orbit.Api/Program.cs` line 15
- Npgsql 10.0.1 - Low-level PostgreSQL ADO.NET provider

**JWT & Security:**
- System.IdentityModel.Tokens.Jwt 8.3.2 - JWT token creation and validation
  - Used by `src/Orbit.Infrastructure/Services/JwtTokenService.cs`
- Microsoft.AspNetCore.Authentication.JwtBearer 10.0.2 - Bearer authentication scheme

**Testing:**
- xunit 2.9.3 - Test framework and runner
  - Used in `tests/Orbit.IntegrationTests/`
- xunit.runner.visualstudio 3.1.4 - VS Test Explorer integration
- Microsoft.AspNetCore.Mvc.Testing 10.0.2 - WebApplicationFactory for integration tests
- Microsoft.NET.Test.Sdk 17.14.1 - Test execution engine
- FluentAssertions 8.8.0 - Fluent assertion library
- coverlet.collector 6.0.4 - Code coverage instrumentation

**Utilities:**
- Microsoft.EntityFrameworkCore.Design 10.0.2 - EF CLI tools for migrations (development only)

## Configuration

**Environment:**
- ASPNETCORE_ENVIRONMENT - Controls development vs production behavior
  - Launchsettings: `src/Orbit.Api/Properties/launchSettings.json` sets to "Development"
  - Configured for http (port 5000) and https (port 5001) profiles

**Configuration Files:**
- `src/Orbit.Api/appsettings.json` - Base configuration (committed)
  - Logging levels
  - Database connection string (placeholder)
  - Ollama provider settings (default)
  - JWT settings
- `src/Orbit.Api/appsettings.Development.json` - Development overrides (gitignored)
  - Contains Gemini API key and JWT secret
  - Contains PostgreSQL credentials

**Configuration Sections:**
- ConnectionStrings.DefaultConnection - PostgreSQL connection
- Ollama - Local LLM provider (BaseUrl, Model)
- Gemini - Google Gemini API provider (ApiKey, Model, BaseUrl)
- Jwt - JWT token configuration (SecretKey, Issuer, Audience, ExpiryHours)
- Logging - Log level configuration
- AiProvider - String switch between "Gemini" or "Ollama"

## Platform Requirements

**Development:**
- .NET 10.0 SDK installed
- PostgreSQL 12+ (connection string in appsettings.json)
  - Alternative: Ollama local LLM server (must be running on localhost:11434)
- Visual Studio 2024 or Visual Studio Code with C# extension (optional)

**Production:**
- ASP.NET Core 10.0 runtime
- PostgreSQL 12+ with persistent database
- API key for selected provider:
  - Gemini: Google Cloud API key
  - Ollama: No API key required (local deployment)

**Port Bindings:**
- HTTP: Port 5000
- HTTPS: Port 5001

---

*Stack analysis: 2026-02-07*
