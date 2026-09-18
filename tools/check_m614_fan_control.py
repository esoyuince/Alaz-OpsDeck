
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
"versions": "M5.20-A" in main and "M5.20-A / CLOUD POLISH" in shell and "M6.20-A / Codex Quota Resilience" in form and 'version="M6.20-A"' in app,
"host-request-contract": "mode=(auto|max|manual)" in host and "speed=([0-9]{2,3})" in host and "SetManualAsync" in host,
"helper-manual-action": '[ValidateSet("status","auto","manual","max")]' in helper and 'manual_supported = $manualSupported' in helper,
"wire-control-fields": all(x in models for x in ["fan_control_supported","fan_manual_supported","fan_control_mode","fan_control_speed_pct","fan_control_busy"]),
"panel-atomic-fields": 'fan_control_present!=0&&fan_control_present!=5' in pc_c and "fan_control_speed_pct" in pc_h,
"manual-ui": all(x in screen for x in ['"AUTO"','"MAX"','"-5"','"MAN 70%"','"+5"']),
"manual-handler": "requested_mode=action==1?1:action==2?2:3" in screen and "FAN_CONTROL_REQUEST mode=manual speed=%d request=%d" in screen,
"host-async-queue": "fanControlRequests.Enqueue(request)" in engine and "SetManualAsync" in engine,
"live-evidence": "fan_ctl=%d manual=%d mode=%d speed=%d busy=%d" in main,
}
for k,v in checks.items():
    print(("PASS " if v else "FAIL ")+k)
print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
