#!/usr/bin/env bash
# build-and-push.sh — Build all 4 CTL images in Azure Cloud Shell and push to ACR.
# Cloud Shell has docker, az, and your identity. Run from the directory that
# contains this script (the unzipped CTL.Deployable root).
set -euo pipefail

ACR_NAME="${ACR_NAME:-ctlagentacr}"
TAG="${TAG:-$(date -u +%Y%m%d%H%M)}"

echo "==> Logging in to ACR: $ACR_NAME"
az acr login --name "$ACR_NAME"

LOGIN_SERVER="$(az acr show -n "$ACR_NAME" --query loginServer -o tsv)"
echo "    Registry: $LOGIN_SERVER"
echo "    Tag:      $TAG"

declare -a IMAGES=(
  "ctl-agent-api|src/Cascade.CTL.Agent.Api/Dockerfile"
  "ctl-mcpserver|src/Cascade.CTL.Agent.McpServer/Dockerfile"
  "ctl-assetservice|src/Cascade.CTL.AssetService/Dockerfile"
  "ctl-rag-indexer|src/Cascade.CTL.RAG.Indexer/Dockerfile"
)

for entry in "${IMAGES[@]}"; do
  IFS='|' read -r name dockerfile <<<"$entry"
  echo ""
  echo "==> Building $name:$TAG  (Dockerfile: $dockerfile)"
  docker build \
    -f "$dockerfile" \
    -t "$LOGIN_SERVER/$name:$TAG" \
    -t "$LOGIN_SERVER/$name:latest" \
    .

  echo "==> Pushing $name:$TAG"
  docker push "$LOGIN_SERVER/$name:$TAG"
  docker push "$LOGIN_SERVER/$name:latest"
done

echo ""
echo "============================================================"
echo " All 4 images pushed to $LOGIN_SERVER with tag: $TAG"
echo "============================================================"
echo ""
echo " Next, on your Windows box, run:"
echo "   cd C:\\CTLDeploy"
echo "   powershell -NoProfile -ExecutionPolicy Bypass -File .\\deploy\\Deploy-CTL-Containers.ps1 -SkipBuild -ImageTag $TAG"
echo ""
