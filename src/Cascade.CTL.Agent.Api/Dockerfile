# syntax=docker/dockerfile:1.7
# --- Build stage --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Central package management files
COPY Directory.Build.props Directory.Packages.props ./

# Project files needed for restore (cached layer)
COPY src/Cascade.CTL.Agent.Domain/Cascade.CTL.Agent.Domain.csproj                 src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/Cascade.CTL.Agent.Infrastructure.csproj src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.Agent.Guardrails/Cascade.CTL.Agent.Guardrails.csproj         src/Cascade.CTL.Agent.Guardrails/
COPY src/Cascade.CTL.Agent.Application/Cascade.CTL.Agent.Application.csproj       src/Cascade.CTL.Agent.Application/
COPY src/Cascade.CTL.Agent.Api/Cascade.CTL.Agent.Api.csproj                       src/Cascade.CTL.Agent.Api/
RUN dotnet restore src/Cascade.CTL.Agent.Api/Cascade.CTL.Agent.Api.csproj

# Sources
COPY src/Cascade.CTL.Agent.Domain/         src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/ src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.Agent.Guardrails/     src/Cascade.CTL.Agent.Guardrails/
COPY src/Cascade.CTL.Agent.Application/    src/Cascade.CTL.Agent.Application/
COPY src/Cascade.CTL.Agent.Api/            src/Cascade.CTL.Agent.Api/

# Baseline config (ACA env vars override secrets at runtime)
COPY config/appsettings.json                src/Cascade.CTL.Agent.Api/appsettings.json
COPY config/rag-knowledge/                  src/Cascade.CTL.Agent.Api/rag-knowledge/

RUN dotnet publish src/Cascade.CTL.Agent.Api/Cascade.CTL.Agent.Api.csproj \
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
  CMD wget -q -O - http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Cascade.CTL.Agent.Api.dll"]
