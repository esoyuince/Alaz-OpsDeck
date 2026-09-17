from pathlib import Path
import hashlib
root=Path(__file__).resolve().parents[1]
ui=(root/'firmware/panel/main/opsdeck_ui.c').read_text(encoding='utf-8-sig')
main=(root/'firmware/panel/main/main.c').read_text(encoding='utf-8-sig')
bin_path=root/'artifacts/opsdeck_panel_m513f_cloudflare_ux_20260918.bin'
blob=bin_path.read_bytes()
checks={
 'ui_version':'M5.13-F / CLOUDFLARE UX' in ui,
 'boot_version':'BOOT version=M5.13-F' in main,
 'health_badge':'cloud_health_badge' in ui and 'cloud_effective_state' in ui,
 'health_strip':'cloud_account_health[5]' in ui and 'CLOUDFLARE ACCOUNTS' in ui,
 'metric_age':'STALE | %s' in ui and 'PART %d/%d | %s' in ui,
 'cost_copy':'COST SNAPSHOT / NOT AN INVOICE' in ui,
 'source_window':'Source %s | window %s..%s' in ui,
 'binary_version':b'M5.13-F / CLOUDFLARE UX' in blob,
 'binary_boot':b'BOOT version=M5.13-F' in blob,
 'partition_fit':len(blob)<0x300000,
}
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print('SHA256='+hashlib.sha256(blob).hexdigest().upper())
print(f'BYTES={len(blob)} FREE={0x300000-len(blob)}')
print(f'RESULT passed={sum(checks.values())} failed={sum(not x for x in checks.values())}')
raise SystemExit(0 if all(checks.values()) else 1)
