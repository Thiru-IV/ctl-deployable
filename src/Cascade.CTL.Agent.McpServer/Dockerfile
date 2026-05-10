# syntax=docker/dockerfile:1.7
# --- Build stage --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./

COPY src/Cascade.CTL.Agent.Domain/Cascade.CTL.Agent.Domain.csproj                 src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/Cascade.CTL.Agent.Infrastructure.csproj src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.Agent.McpServer/Cascade.CTL.Agent.McpServer.csproj           src/Cascade.CTL.Agent.McpServer/
RUN dotnet restore src/Cascade.CTL.Agent.McpServer/Cascade.CTL.Agent.McpServer.csproj

COPY src/Cascade.CTL.Agent.Domain/         src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/ src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.Agent.McpServer/      src/Cascade.CTL.Agent.McpServer/

# Baseline config — McpServer expects ../../config/appsettings.json relative to
# its source dir at dev time. In container we just place a sibling appsettings.json
# in the content root; env vars override secrets.
COPY config/appsettings.json src/Cascade.CTL.Agent.McpServer/appsettings.json

RUN dotnet publish src/Cascade.CTL.Agent.McpServer/Cascade.CTL.Agent.McpServer.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# --- Runtime stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Writable directory for AuditFileStore (CWD/audit-logs).
RUN mkdir -p /app/audit-logs && chmod 777 /app/audit-logs

USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O - http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "Cascade.CTL.Agent.McpServer.dll"]
