
from pathlib import Path
root=Path(r"C:\Antigravity\OpsDeck")
app=(root/"host/OpsDeck.Core/AppEngine.cs").read_text(encoding="utf-8")
pacing=(root/"host/OpsDeck.Core/PanelSerialPacing.cs").read_text(encoding="utf-8")
ui=(root/"firmware/panel/main/opsdeck_ui.c").read_text(encoding="utf-8")
main=(root/"firmware/panel/main/main.c").read_text(encoding="utf-8")
form=(root/"host/OpsDeck.Host/MainForm.cs").read_text(encoding="utf-8")
checks={
"versions":"M6.19-A / Fan Manual" in form and 'version="M6.19-A"' in app and "BOOT version=M5.19-A" in main and "M5.19-A / FAN MANUAL" in ui,
"no-fixed-serial-sleeps":all(x not in app for x in ["Task.Delay(160,stop.Token)","Task.Delay(150,stop.Token)","Task.Delay(120,stop.Token)","Task.Delay(70,stop.Token)"]),
"rx-loop-fast":"Task.Delay(PanelSerialPacing.LoopDelayMs,stop.Token)" in app and "LoopDelayMs=5" in pacing and "Baud=460800" in pacing,
"wire-aware-gap":"GapMs(frame)" in app and "bytes*10_000L" in pacing and "MaxGapMs=300" in pacing,
"request-immediate":all(x in app for x in ["nextInventory=timer.ElapsedMilliseconds","nextDetails=timer.ElapsedMilliseconds","nextOpsView=timer.ElapsedMilliseconds"]),
"single-frame-scheduler":"statusFrames=new Queue<string>()" in app and "if(now>=nextTx)" in app and "if(tx!=null)SendFrame(tx)" in app,
"screen-log-parser":"SCREEN_OPEN" in app,
"refresh-100ms":"lv_timer_create(refresh,100,NULL)" in ui and "lv_timer_create(refresh,250,NULL)" not in ui,
"change-driven-main":"rendered_pc_sequence" in ui and "rendered_second" in ui and "rendered_page" in ui,
"change-driven-provider":"rendered_sequence" in ui and "rendered_cloud" in ui,
"page-build-metric":"PAGE index=%d" in ui and "build_ms=%" in ui,
"screen-build-metric":"SCREEN_OPEN target=%d" in ui and "build_ms=%" in ui,
"agent-change-driven":"last_render_sequence=UINT32_MAX" in (root/"firmware/panel/main/opsdeck_agent_ui.c").read_text(encoding="utf-8") and "selected_workspace==last_render_workspace" in (root/"firmware/panel/main/opsdeck_agent_ui.c").read_text(encoding="utf-8"),
"inventory-change-driven":"q.request_id==last_render_request" in (root/"firmware/panel/main/opsdeck_inventory_ui.c").read_text(encoding="utf-8"),
"screen-live-change-driven":"last_live_pc_sequence" in (root/"firmware/panel/main/opsdeck_screen_ui.c").read_text(encoding="utf-8") and "last_wifi_sequence" in (root/"firmware/panel/main/opsdeck_screen_ui.c").read_text(encoding="utf-8"),
"home-shared-styles":"lv_obj_add_style(o,&style_card,0)" in ui and "lv_obj_add_style(b,&style_button,0)" in ui,
"home-chart-deferred":"screen_shortcut(overview_net_chart,OPS_SCREEN_NETWORK,CYAN);draw_network();" not in ui and "draw_network();\n    } else if(page==2)" not in ui,
"home-cache":"home_cached" in ui and "restore_home_refs()" in ui and "cached=1" in ui,
"home-dynamic-root":"dynamic_root" in ui and "lv_obj_add_flag(home_root,LV_OBJ_FLAG_HIDDEN)" in ui,
}
for k,v in checks.items(): print(("PASS " if v else "FAIL ")+k)
print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
