# syntax=docker/dockerfile:1.7
# --- Build stage ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution-level files needed for central package management
COPY Directory.Build.props Directory.Packages.props ./

# Copy project files for a cacheable restore layer
COPY src/Cascade.CTL.Agent.Domain/Cascade.CTL.Agent.Domain.csproj src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.AssetService/Cascade.CTL.AssetService.csproj src/Cascade.CTL.AssetService/
RUN dotnet restore src/Cascade.CTL.AssetService/Cascade.CTL.AssetService.csproj

# Copy remaining sources and publish
COPY src/Cascade.CTL.Agent.Domain/ src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.AssetService/ src/Cascade.CTL.AssetService/
RUN dotnet publish src/Cascade.CTL.AssetService/Cascade.CTL.AssetService.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# --- Runtime stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Run as non-root user (already provided by base image)
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O - http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Cascade.CTL.AssetService.dll"]
