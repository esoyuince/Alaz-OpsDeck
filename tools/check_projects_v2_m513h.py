from pathlib import Path

root=Path(__file__).resolve().parents[1]
def read(rel): return (root/rel).read_text(encoding="utf-8-sig")

paging=read("host/OpsDeck.Core/InventoryPaging.cs")
health=read("host/OpsDeck.Core/ProjectHealth.cs")
tests=read("host/OpsDeck.Tests/M59ProjectHealthTests.cs")
parser=read("firmware/panel/main/opsdeck_inventory.c")
header=read("firmware/panel/main/opsdeck_inventory.h")
ui=read("firmware/panel/main/opsdeck_inventory_ui.c")
main=read("firmware/panel/main/main.c")
shell=read("firmware/panel/main/opsdeck_ui.c")
host=read("host/OpsDeck.Host/MainForm.cs")
engine=read("host/OpsDeck.Core/AppEngine.cs")

fields=["project_workers","project_d1","project_r2","project_pages",
        "project_ok","project_attention","project_degraded","project_unknown","project_health_age_s"]
checks={
    "panel_version": "BOOT version=M5.13-H" in main and "M5.13-H / PROJECTS V2" in shell,
    "host_version": "M6.13-C / Projects V2" in host and 'version="M6.13-C"' in engine,
    "wire_summary_fields": all(x in paging for x in fields),
    "wire_count_guard": "ProjectWorkers+ProjectD1+ProjectR2+ProjectPages!=TotalRows" in paging,
    "health_age_helper": "OldestEvidenceAgeSeconds" in health,
    "parser_optional_backcompat": "present_summary!=0&&present_summary!=9" in parser,
    "parser_summary_fields": all(x in parser for x in fields),
    "parser_state": "project_summary_present" in header and "project_summary_present" in parser,
    "ui_resource_mix": '"W:%d D1:%d R2:%d P:%d | OK:%d ATT:%d DEG:%d UNK:%d"' in ui,
    "ui_age_line": "Health oldest %s | inventory age %s | Pages = NO HEALTH DATA" in ui,
    "tests_summary": "selected-summary-counts" in tests and "wire-project-summary" in tests and "wire-project-summary-omitted" in tests,
}
passed=sum(checks.values())
for name,ok in checks.items(): print(("PASS " if ok else "FAIL ")+name)
print(f"RESULT passed={passed} failed={len(checks)-passed}")
raise SystemExit(0 if passed==len(checks) else 1)
