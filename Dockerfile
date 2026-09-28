FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for layer caching
COPY src/Orbit.Domain/Orbit.Domain.csproj src/Orbit.Domain/
COPY src/Orbit.Application/Orbit.Application.csproj src/Orbit.Application/
COPY src/Orbit.Infrastructure/Orbit.Infrastructure.csproj src/Orbit.Infrastructure/
COPY src/Orbit.Api/Orbit.Api.csproj src/Orbit.Api/

RUN dotnet restore src/Orbit.Api/Orbit.Api.csproj

# Copy everything and publish
COPY src/ src/
RUN dotnet publish src/Orbit.Api/Orbit.Api.csproj -c Release -o /app --no-restore
RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.12
RUN OpenApiGenerateDocumentsOnBuild=false /tools/dotnet-ef migrations bundle \
    --project src/Orbit.Infrastructure/Orbit.Infrastructure.csproj \
    --startup-project src/Orbit.Api/Orbit.Api.csproj \
    --context OrbitDbContext \
    --configuration Release \
    --target-runtime linux-x64 \
    --self-contained \
    --output /app/efbundle

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/efbundle

RUN apt-get update \
    && apt-get install -y --no-install-recommends wget libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --no-create-home appuser
USER appuser

COPY --chown=appuser:appuser --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Orbit.Api.dll"]
