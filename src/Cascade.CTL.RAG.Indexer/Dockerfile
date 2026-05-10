# syntax=docker/dockerfile:1.7
# --- Build stage --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./

COPY src/Cascade.CTL.Agent.Domain/Cascade.CTL.Agent.Domain.csproj                 src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/Cascade.CTL.Agent.Infrastructure.csproj src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.RAG.Indexer/Cascade.CTL.RAG.Indexer.csproj                   src/Cascade.CTL.RAG.Indexer/
RUN dotnet restore src/Cascade.CTL.RAG.Indexer/Cascade.CTL.RAG.Indexer.csproj

COPY src/Cascade.CTL.Agent.Domain/         src/Cascade.CTL.Agent.Domain/
COPY src/Cascade.CTL.Agent.Infrastructure/ src/Cascade.CTL.Agent.Infrastructure/
COPY src/Cascade.CTL.RAG.Indexer/          src/Cascade.CTL.RAG.Indexer/

# Bake policy knowledge into the image — ACA Job consumes it from /app/rag-knowledge.
# Re-running the indexer after policy changes = rebuild image + restart job.
COPY config/rag-knowledge/  src/Cascade.CTL.RAG.Indexer/rag-knowledge/
COPY config/appsettings.json src/Cascade.CTL.RAG.Indexer/appsettings.json

RUN dotnet publish src/Cascade.CTL.RAG.Indexer/Cascade.CTL.RAG.Indexer.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# --- Runtime stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENV DOTNET_RUNNING_IN_CONTAINER=true

# Default args point the indexer at the baked-in knowledge directory and ask it
# to recreate the index on each run for deterministic, idempotent ingestion.
ENTRYPOINT ["dotnet", "Cascade.CTL.RAG.Indexer.dll"]
CMD ["--knowledge-path", "/app/rag-knowledge", "--recreate-index"]
