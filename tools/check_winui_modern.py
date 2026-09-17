from pathlib import Path
root=Path(__file__).resolve().parents[1]
main=(root/'host/OpsDeck.Host/MainForm.cs').read_text(encoding='utf-8-sig')
theme=(root/'host/OpsDeck.Host/OpsDeckTheme.cs').read_text(encoding='utf-8-sig')
settings=(root/'host/OpsDeck.Host/SettingsForm.cs').read_text(encoding='utf-8-sig')
overview=(root/'host/OpsDeck.Host/M5OverviewForm.cs').read_text(encoding='utf-8-sig')
program=(root/'host/OpsDeck.Host/Program.cs').read_text(encoding='utf-8-sig')
checks={
 'theme-palette':'Color.FromArgb(10,14,24)' in theme and 'Color.FromArgb(59,198,255)' in theme,
 'theme-grid':'StyleGrid(DataGridView g)' in theme and 'AlternatingRowsDefaultCellStyle' in theme,
 'theme-buttons':'StyleButton(Button b' in theme and 'FlatStyle=FlatStyle.Flat' in theme,
 'main-sidebar':'ColumnStyle(SizeType.Absolute,240)' in main,
 'main-header':'ALAZ OPSDECK' in main and 'SECURE TELEMETRY' in main,
 'main-nav-sections':'Section("Monitoring")' in main and 'Section("Agents & Analysis")' in main,
 'main-system-footer':'systemBar=new TableLayoutPanel' in main and 'Text="Ayarlar"' in main and 'Text="Çıkış"' in main,
 'main-no-native-nav-scroll':'AutoScroll=false' in main,
 'main-state-badge':'headerStatus.Text=' in main and 'SYSTEM OK' in main,
 'main-state-tint':'TintStateCell' in main,
 'native-dark-mode':'Application.SetColorMode(SystemColorMode.Dark)' in program,
 'settings-themed':'OpsDeckTheme.Apply(this)' in settings,
 'overview-themed':'OpsDeckTheme.Apply(this)' in overview and 'StyleGrid(summary)' in overview,
}
for name,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+name)
print(f"RESULT passed={sum(checks.values())} failed={sum(not v for v in checks.values())}")
raise SystemExit(0 if all(checks.values()) else 1)
