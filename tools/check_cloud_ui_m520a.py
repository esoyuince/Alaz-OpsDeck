from pathlib import Path
import hashlib
root=Path(__file__).resolve().parents[1]
ui=(root/'firmware/panel/main/opsdeck_ui.c').read_text(encoding='utf-8-sig')
main=(root/'firmware/panel/main/main.c').read_text(encoding='utf-8-sig')
bin_path=root/'firmware/panel/build/opsdeck_panel.bin'
blob=bin_path.read_bytes()
checks={
 'ui_version':'M5.20-A / CLOUD POLISH' in ui,
 'boot_version':'BOOT version=M5.20-A' in main,
 'health_badge':'cloud_health_badge' in ui and 'cloud_effective_state' in ui,
 'health_strip':'cloud_account_health[5]' in ui and 'CLOUDFLARE ACCOUNTS' in ui,
 'account_costs':'ALL\\n%.3s %s' in ui and '%s\\n%.3s %s' in ui,
 'cost_heading':'COST / MONTHLY' in ui,
 'usage_monthly':'Usage %s + monthly %s %.3s' in ui and 'no monthly fee' in ui,
 'period_from':'period from %s' in ui,
 'provider_age':'Updated %d s ago | source dates above' in ui,
 'binary_version':b'M5.20-A / CLOUD POLISH' in blob,
 'binary_boot':b'BOOT version=M5.20-A' in blob,
 'partition_fit':len(blob)<0x300000,
}
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print('SHA256='+hashlib.sha256(blob).hexdigest().upper())
print(f'BYTES={len(blob)} FREE={0x300000-len(blob)}')
print(f'RESULT passed={sum(checks.values())} failed={sum(not x for x in checks.values())}')
raise SystemExit(0 if all(checks.values()) else 1)
