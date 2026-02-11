FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for layer caching
COPY Orbit.slnx ./
COPY src/Orbit.Domain/Orbit.Domain.csproj src/Orbit.Domain/
COPY src/Orbit.Application/Orbit.Application.csproj src/Orbit.Application/
COPY src/Orbit.Infrastructure/Orbit.Infrastructure.csproj src/Orbit.Infrastructure/
COPY src/Orbit.Api/Orbit.Api.csproj src/Orbit.Api/

RUN dotnet restore Orbit.slnx

# Copy everything and publish
COPY src/ src/
RUN dotnet publish src/Orbit.Api/Orbit.Api.csproj -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*

# Non-root user for security
RUN useradd --no-create-home appuser
USER appuser

COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Orbit.Api.dll"]
