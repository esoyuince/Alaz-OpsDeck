
from pathlib import Path
root=Path(r"C:\Antigravity\OpsDeck")
host=(root/"host/OpsDeck.Core/OmenFanControl.cs").read_text(encoding="utf-8")
models=(root/"host/OpsDeck.Core/Models.cs").read_text(encoding="utf-8")
engine=(root/"host/OpsDeck.Core/AppEngine.FanControl.cs").read_text(encoding="utf-8")
helper=(root/"host/OpsDeck.Core/Assets/omen_fan_control.ps1").read_text(encoding="utf-8")
pc_h=(root/"firmware/panel/main/opsdeck_pc.h").read_text(encoding="utf-8")
pc_c=(root/"firmware/panel/main/opsdeck_pc.c").read_text(encoding="utf-8")
screen=(root/"firmware/panel/main/opsdeck_screen_ui.c").read_text(encoding="utf-8")
main=(root/"firmware/panel/main/main.c").read_text(encoding="utf-8")
shell=(root/"firmware/panel/main/opsdeck_ui.c").read_text(encoding="utf-8")
app=(root/"host/OpsDeck.Core/AppEngine.cs").read_text(encoding="utf-8")
form=(root/"host/OpsDeck.Host/MainForm.cs").read_text(encoding="utf-8")

checks={
"versions": "M5.14-A" in main and "M5.14-A / FAN CONTROL" in shell and "M6.14-A / Fan Control" in form and 'version="M6.14-A"' in app,
"host-request-contract": "mode=(auto|max)" in host and "request=([1-9][0-9]{0,9})" in host and "request-manual-rejected" not in host,
"helper-no-manual-action": '[ValidateSet("status","auto","max")]' in helper and '[ValidateSet("status","auto","manual","max")]' not in helper,
"wire-control-fields": all(x in models for x in ["fan_control_supported","fan_manual_supported","fan_control_mode","fan_control_busy"]),
"panel-atomic-fields": 'fan_control_present!=0&&fan_control_present!=4' in pc_c and "PC_FAN_CONTROL" in pc_h,
"manual-ui-locked": 'button(root,616,101,92,32,"MANUAL"' in screen and 'lv_obj_add_state(fan_control_btn[2],LV_STATE_DISABLED)' in screen,
"manual-handler-rejected": "requested<1||requested>2" in screen and 'requested==1?"auto":"max"' in screen,
"panel-request-log": 'FAN_CONTROL_REQUEST mode=%s request=%d' in screen,
"host-async-queue": "fanControlRequests.Enqueue(request)" in engine and "FanControlLoop" in engine,
"live-evidence": "fan_ctl=%d mode=%d busy=%d" in main,
}
for k,v in checks.items():
    print(("PASS " if v else "FAIL ")+k)
print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
