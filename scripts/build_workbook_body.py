"""Build the workbook PUT body file."""
import json, sys, pathlib

root = pathlib.Path(__file__).parent
serialized_path = root / "workbook-ctl-audit.json"
out_path = root / "workbook-body.json"

if len(sys.argv) < 2:
    print("Usage: build_workbook_body.py <appInsightsResourceId> [location] [displayName]")
    sys.exit(1)

ai_id = sys.argv[1]
location = sys.argv[2] if len(sys.argv) > 2 else "eastus2"
display = sys.argv[3] if len(sys.argv) > 3 else "CTL Agent - Verdict Evidence and Audit Trail"

serialized = serialized_path.read_text(encoding="utf-8")

body = {
    "location": location,
    "kind": "shared",
    "properties": {
        "displayName": display,
        "category": "workbook",
        "serializedData": serialized,
        "version": "1.0",
        "sourceId": ai_id,
    },
}

out_path.write_text(json.dumps(body, ensure_ascii=False), encoding="utf-8")
print(f"Wrote {out_path} ({out_path.stat().st_size} bytes)")
