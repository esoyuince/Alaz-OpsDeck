from pathlib import Path

root=Path(__file__).resolve().parents[1]
ui=(root/"firmware/panel/main/opsdeck_inventory_ui.c").read_text(encoding="utf-8-sig")
inv=(root/"firmware/panel/main/opsdeck_inventory.c").read_text(encoding="utf-8-sig")
hdr=(root/"firmware/panel/main/opsdeck_inventory.h").read_text(encoding="utf-8-sig")
main=(root/"firmware/panel/main/main.c").read_text(encoding="utf-8-sig")
shell=(root/"firmware/panel/main/opsdeck_ui.c").read_text(encoding="utf-8-sig")
health=(root/"host/OpsDeck.Core/ProjectHealth.cs").read_text(encoding="utf-8-sig")
paging=(root/"host/OpsDeck.Core/InventoryPaging.cs").read_text(encoding="utf-8-sig")

checks={
    "version_boot":'BOOT version=M5.13-G' in main,
    "version_ui":'M5.13-G / HEALTH UX' in shell,
    "wrap_forward":'s.page+1>=s.total_pages?0:s.page+1' in ui,
    "wrap_backward":'s.page<=0?s.total_pages-1:s.page-1' in ui,
    "arrows_live_at_edges":'s.total_pages<=0' in ui and 's.page<=0)lv_obj_add_state(prev' not in ui,
    "project_label_wire":'project_health_label' in paging,
    "project_label_parse":'project_health_label' in inv and 'project_health_label[21]' in hdr,
    "reason_labels":all(x in health for x in ['"PENDING"','"NO DATA"','"STALE"','"ATTENTION"','"ERROR"','"NO HEALTH DATA"']),
    "pages_excluded":'SupportsResourceHealth' in health and 'ResourceKind.Pages' not in health.split('SupportsResourceHealth',1)[1].split(';',1)[0],
    "footer_explains":'Pages = NO HEALTH DATA' in ui,
}
passed=sum(checks.values())
for name,ok in checks.items(): print(("PASS " if ok else "FAIL ")+name)
print(f"RESULT passed={passed} failed={len(checks)-passed}")
raise SystemExit(0 if passed==len(checks) else 1)
