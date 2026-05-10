# Execution Steps

1. **Verify Azure login**: Run `az account show` — if it shows subscription, I'm logged in. If it errors or shows the wrong account, run `az login --use-device-code` — this gives me a code + URL to enter in any browser where I can sign in with any email (bypasses cached accounts). `az logout` 

2. **Build the solution**: `dotnet build` from the repo root (`Cascade.CTL.AgentSolution/`)

3. **Start the Asset Domain Service** (separate terminal): `cd src/Cascade.CTL.AssetService && dotnet run` or `dotnet run --project src/Cascade.CTL.AssetService`
   - Verify I can see `Now listening on: http://localhost:64019`
   - Health check: `curl http://localhost:64019/health` should return `Healthy`
   - **Docker alternative** (when Docker is available): `docker build -f src/Cascade.CTL.AssetService/Dockerfile -t ctl-asset-service . && docker run -d -p 64019:8080 --name ctl-asset-service ctl-asset-service`
   - **Stop Docker**: `docker stop ctl-asset-service && docker rm ctl-asset-service`

4. **Start the MCP Server** (separate terminal): `cd src/Cascade.CTL.Agent.McpServer && dotnet run` or `dotnet run --project src/Cascade.CTL.Agent.McpServer`

    Is running? `Test-NetConnection -ComputerName localhost -Port 5100 -InformationLevel Quiet`

5. **Run a CTL evaluation** (separate terminal): `cd src/Cascade.CTL.Agent.Host && dotnet run -- --asset-id ASSET-TX-001` or  `dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-TX-001`

6. **Verify inline output**: Confirm the console shows the CTL verdict, confidence score, evidence trail, reflection log, and the full color-coded audit trail (8 checkpoints) — plus the persisted JSONL file path printed at the end

7. **Replay the full audit trail**: `dotnet run -- --audit-view <session-id>` — copy the session ID from the cyan box printed at the end of each evaluation run. Verify all 8 audit checkpoints appear: EvaluationStarted → PlanGenerated → InvestigationFindings (×3) → ReflectionCompleted → QualityGateEvaluated → HumanReviewCompleted → EvaluationCompleted, each with timestamps, token counts, durations, and payload previews

8. **List all past sessions** (optional): `dotnet run -- --audit-history` — shows session ID, timestamp, asset ID, step count, and verdict summary for each past run

9. **Inspect the raw JSONL file**: Open `audit-logs/<session-id>.jsonl` — verify each line is a complete JSON object containing SessionId, AssetId, AgentName, StepType, Description, OutputPayload (full reasoning/reflection/evidence), TokensUsed, and Duration

10. **Stop services**: Ctrl+C in each terminal (AssetService, MCP Server)

11. **Azure Search** (fusion ANN + BM25 K) -  using 1536-dimension embedding
try `*` , or sample queries:
    - `Texas foreclosure timeline`
    - `HOA delinquency verification requirements`
    - `California REO property disposition`
    - `CWCOT conveyance condition standards`
    - `FHA first legal action deadline`
    - `BPO valuation staleness threshold`

12. **429s** - Search for `AgentExhaustedRetries` → point to the description that  reads HTTP 429 (Azure OpenAI rate limit ...) 
