from pathlib import Path
root=Path(r"C:\Antigravity\OpsDeck")
details=(root/"host/OpsDeck.Core/PanelDetails.cs").read_text(encoding="utf-8")
engine=(root/"host/OpsDeck.Core/PanelDetailsEngine.cs").read_text(encoding="utf-8")
inventory=(root/"host/OpsDeck.Core/InventoryPaging.cs").read_text(encoding="utf-8")
inv_h=(root/"firmware/panel/main/opsdeck_inventory.h").read_text(encoding="utf-8")
inv_c=(root/"firmware/panel/main/opsdeck_inventory.c").read_text(encoding="utf-8")
inv_ui=(root/"firmware/panel/main/opsdeck_inventory_ui.c").read_text(encoding="utf-8")
det_h=(root/"firmware/panel/main/opsdeck_details.h").read_text(encoding="utf-8")
det_c=(root/"firmware/panel/main/opsdeck_details.c").read_text(encoding="utf-8")
det_ui=(root/"firmware/panel/main/opsdeck_details_ui.c").read_text(encoding="utf-8")
ui=(root/"firmware/panel/main/opsdeck_ui.c").read_text(encoding="utf-8")
app=(root/"host/OpsDeck.Core/AppEngine.cs").read_text(encoding="utf-8")
form=(root/"host/OpsDeck.Host/MainForm.cs").read_text(encoding="utf-8")
main=(root/"firmware/panel/main/main.c").read_text(encoding="utf-8")
checks={
"versions":"M6.17-B / Project Details" in form and 'version="M6.17-B"' in app and "BOOT version=M5.17-B" in main and "M5.17-B / PROJECT DETAILS" in ui,
"legacy-request":'group=m.Groups[4].Success?m.Groups[4].Value:"all"' in details,
"filtered-request":"group=(all|[a-f0-9]{16})" in details and 'Group!="all"&&Kind>2' in details,
"group-echo":"group=Request.Group" in details,
"pure-filter":"FilterProject(" in engine and "InventoryPaging.GroupKey(project)==group" in engine,
"filter-cross-account":"Same(x.Key.AccountId,account)" in engine,
"account-wide-cleared":"Queues=[],QueueHistory=[],Ai=[]" in engine,
"no-filter-network":"CloudflareClient" not in engine and "ReadAiHistory(" not in engine,
"inventory-kind-wire":"int? Kind=null" in inventory and "(int)row.Resource.Key.Kind" in inventory,
"panel-kind-optional":"kind_present" in inv_h and 'const cJSON *kind=cJSON_GetObjectItemCaseSensitive(row,"kind")' in inv_c,
"project-row-kind-rejected":"s.view==0&&s.rows[i].kind_present" in inv_c,
"only-supported-clickable":"s.rows[i].kind<=2" in inv_ui and "opsdeck_ui_open_project_detail" in inv_ui,
"project-bridge":"project_detail_pending=true" in ui and "PROJECT_DETAIL_OPEN" in ui and "opsdeck_details_select_project(project_detail_slot,project_detail_kind,0,project_detail_group,project_detail_name)" in ui,
"project-request-wire":'group=%s request=%d' in det_c and "opsdeck_details_select_project" in det_h,
"response-match-group":"!strcmp(s.group,query.group)" in det_c,
"filtered-category-cycle":"(q.kind+1)%3" in det_ui,
"account-change-clears":"if(action<2)opsdeck_details_select((int)action,q.kind,0)" in det_ui,
"project-label":"Project: %s | %s" in det_ui,
"pages-no-fake-detail":"kind>=0&&s.rows[row].kind<=2" in inv_ui,
"details-create-passive":"opsdeck_details_copy(NULL,&q);opsdeck_details_select" not in det_ui,
"project-single-request":"project_detail_pending=true" in ui and "show_page(5);opsdeck_details_select_project" not in ui,
"pending-consumed-in-page":"if(project_detail_pending)" in ui and "project_detail_pending=false" in ui,
"inventory-active-gate":"static bool active;" in inv_c and "enabled=active" in inv_c and "if(enabled)ESP_LOGI" in inv_c,
"inventory-deactivate":"opsdeck_inventory_deactivate()" in inv_ui,
}
for k,v in checks.items(): print(("PASS " if v else "FAIL ")+k)
print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
