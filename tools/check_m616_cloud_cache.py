from pathlib import Path
root=Path(r"C:\Antigravity\OpsDeck")
app=(root/"host/OpsDeck.Core/AppEngine.cs").read_text(encoding="utf-8")
summary=(root/"host/OpsDeck.Core/CloudPanelSummary.cs").read_text(encoding="utf-8")
multi=(root/"host/OpsDeck.Core/MultiAccount.cs").read_text(encoding="utf-8")
cloud_h=(root/"firmware/panel/main/opsdeck_cloud.h").read_text(encoding="utf-8")
cloud_c=(root/"firmware/panel/main/opsdeck_cloud.c").read_text(encoding="utf-8")
ui=(root/"firmware/panel/main/opsdeck_ui.c").read_text(encoding="utf-8")
main=(root/"firmware/panel/main/main.c").read_text(encoding="utf-8")
form=(root/"host/OpsDeck.Host/MainForm.cs").read_text(encoding="utf-8")
checks={
"versions":"M6.16-A / Cloud Cache" in form and 'version="M6.16-A"' in app and "BOOT version=M5.16-A" in main and "M5.16-A / CLOUD CACHE" in ui,
"optional-wire":"CloudPanelSummary? summary=null" in multi and "summary=summary?.Wire()" in multi,
"cache-only-host":"queueCache" in summary and "analyticsHistoryCache" in summary and "Inventory.Sets" in summary and "CloudflareClient" not in summary and "ReadAiHistory(" not in summary,
"serial-uses-summary":"CloudWire(profiles[i]" in app and "CloudSummary(account.Profile,now)" in summary,
"summary-struct":"summary_present" in cloud_h and "inventory_state" in cloud_h and "gateway_cached" in cloud_h,
"atomic-parser":'const cJSON *summary=cJSON_GetObjectItemCaseSensitive(o,"summary")' in cloud_c and 's.summary_present=true' in cloud_c and 'queue_observed>s.queue_count' in cloud_c,
"unknown-sentinel":"summary_number" in cloud_c and 'v->valuedouble!=-1' in cloud_c,
"resource-ui":"READ-ONLY CACHE COVERAGE" in ui and "cloud_resource_summary" in ui and "Resources %d | W:%d D1:%d R2:%d P:%d" in ui,
"queue-ui":"cloud_queue_summary" in ui and "metrics %d/%d" in ui,
"ai-ui":"cloud_ai_summary" in ui and "Workers AI %s | req %s | in %s | out %s" in ui,
"gateway-ui":"cloud_gateway_summary" in ui and "AI Gateway %s | req %s | err %s | cache %s" in ui,
"all-not-falsely-combined":"account cache is not combined" in ui,
}
for k,v in checks.items(): print(("PASS " if v else "FAIL ")+k)
print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
