# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy build configuration and project files first for layer caching
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Hive.Contracts/Hive.Contracts.csproj src/Hive.Contracts/
COPY src/Hive.Domain/Hive.Domain.csproj src/Hive.Domain/
COPY src/Hive.Notifications/Hive.Notifications.csproj src/Hive.Notifications/
COPY src/Hive.Infrastructure/Hive.Infrastructure.csproj src/Hive.Infrastructure/
COPY src/Hive.Server/Hive.Server.csproj src/Hive.Server/

RUN dotnet restore src/Hive.Server/Hive.Server.csproj

# Copy full server-side source and publish framework-dependent Linux binary
COPY src/Hive.Contracts/ src/Hive.Contracts/
COPY src/Hive.Domain/ src/Hive.Domain/
COPY src/Hive.Notifications/ src/Hive.Notifications/
COPY src/Hive.Infrastructure/ src/Hive.Infrastructure/
COPY src/Hive.Server/ src/Hive.Server/

RUN dotnet publish src/Hive.Server/Hive.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Internal CA certificate trust and health check tooling (zero AD/Kerberos requirement for LocalJwt mode)
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Non-root execution (standard ASP.NET Core 10 app UID 1654)
USER $APP_UID

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=https://+:8443;http://+:8080 \
    Authentication__UserMode=LocalJwt \
    DOTNET_EnableDiagnostics=0

EXPOSE 8443 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Hive.Server.dll"]
