"""Fail-fast, app-only ESP32-S3 flasher for OpsDeck.
Never erases the full chip and never writes bootloader, partition table, NVS, OTA data, or storage.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from serial.tools import list_ports

ROOT=Path(__file__).resolve().parents[1]
OFFSET=0x10000
MAX_APP_END=0x310000
EXPECTED_VID=0x1A86
EXPECTED_PID=0x7522
PINNED_PY=ROOT/'.espressif/python_env/idf5.5_py3.11_env/Scripts/python.exe'
LOCK=Path(os.environ.get('LOCALAPPDATA',str(ROOT/'artifacts')))/'OpsDeck/panel-flash.lock'


def sha256(path:Path)->str:
    h=hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda:f.read(1024*1024),b''):
            h.update(chunk)
    return h.hexdigest().upper()


def host_running()->bool:
    q=subprocess.run(['tasklist','/FI','IMAGENAME eq OpsDeck.Host.exe','/NH'],capture_output=True,text=True,errors='replace')
    return 'OpsDeck.Host.exe' in q.stdout


def esptool_running()->bool:
    ps="$me=$PID; Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $me -and $_.CommandLine -and ($_.CommandLine -match 'python(.exe)? .*?-m esptool' -or $_.Name -match '^esptool(.exe)?$') } | Select-Object -ExpandProperty ProcessId"
    q=subprocess.run(['powershell','-NoProfile','-Command',ps],capture_output=True,text=True,errors='replace')
    return any(x.strip().isdigit() for x in q.stdout.splitlines())


def exact_port(name:str):
    matches=[p for p in list_ports.comports() if p.device.upper()==name.upper()]
    if len(matches)!=1:
        raise RuntimeError(f'{name} must exist exactly once; found {len(matches)}')
    p=matches[0]
    if p.vid!=EXPECTED_VID or p.pid!=EXPECTED_PID:
        raise RuntimeError(f'{name} is not expected CH340K VID:PID 1A86:7522')
    return p


def pid_alive(pid:int)->bool:
    q=subprocess.run(['tasklist','/FI',f'PID eq {pid}','/NH'],capture_output=True,text=True,errors='replace')
    return str(pid) in q.stdout


def acquire_lock():
    LOCK.parent.mkdir(parents=True,exist_ok=True)
    if LOCK.exists():
        try:
            stale=json.loads(LOCK.read_text(encoding='utf-8'))
            if not pid_alive(int(stale.get('pid',-1))):
                LOCK.unlink()
        except Exception:
            pass
    try:
        fd=os.open(LOCK,os.O_CREAT|os.O_EXCL|os.O_WRONLY)
    except FileExistsError as e:
        raise RuntimeError(f'flash lock already exists: {LOCK}') from e
    with os.fdopen(fd,'w',encoding='utf-8') as f:
        json.dump({'pid':os.getpid(),'started':time.time()},f)


def release_lock():
    try: LOCK.unlink()
    except FileNotFoundError: pass


def commands(port:str,image:Path,baud:int):
    base=[str(PINNED_PY),'-m','esptool','--chip','esp32s3','--port',port,'--baud',str(baud),'--before','default_reset','--after','hard_reset']
    write=base+['write_flash','--flash_mode','dio','--flash_freq','80m','--flash_size','16MB',hex(OFFSET),str(image)]
    verify=base+['verify_flash',hex(OFFSET),str(image)]
    return write,verify


def self_test()->int:
    w,v=commands('COM9',Path('panel.bin'),460800)
    joined=' '.join(w+v).lower()
    checks={
        'app-only-offset':hex(OFFSET) in w,
        'write-and-verify':'write_flash' in w and 'verify_flash' in v,
        'no-full-erase':'erase_flash' not in joined and 'erase_region' not in joined,
        'no-bootloader-or-partition':'bootloader' not in joined and 'partition-table' not in joined,
        'no-nvs-storage':'nvs' not in joined and 'storage' not in joined,
    }
    for k,ok in checks.items(): print(('PASS ' if ok else 'FAIL ')+k)
    print(f"RESULT passed={sum(checks.values())} failed={len(checks)-sum(checks.values())}")
    return 0 if all(checks.values()) else 1


def main()->int:
    ap=argparse.ArgumentParser()
    ap.add_argument('--image',type=Path)
    ap.add_argument('--expected-sha256')
    ap.add_argument('--port',default='COM9')
    ap.add_argument('--baud',type=int,default=460800)
    ap.add_argument('--execute',action='store_true')
    ap.add_argument('--self-test',action='store_true')
    a=ap.parse_args()
    if a.self_test: return self_test()
    if not a.image or not a.expected_sha256:
        ap.error('--image and --expected-sha256 are required unless --self-test is used')
    image=a.image.resolve()
    if not image.is_file(): raise RuntimeError(f'image not found: {image}')
    expected=a.expected_sha256.replace(' ','').upper()
    if len(expected)!=64 or any(c not in '0123456789ABCDEF' for c in expected):
        raise RuntimeError('expected SHA256 must be exactly 64 hex characters')
    actual=sha256(image)
    if actual!=expected: raise RuntimeError(f'SHA256 mismatch: expected {expected}, got {actual}')
    data=image.read_bytes()
    if not data or data[0]!=0xE9: raise RuntimeError('image is not an ESP app image')
    end=(OFFSET+len(data)+4095)&~4095
    if end>MAX_APP_END: raise RuntimeError(f'app image exceeds safe app boundary: {hex(end)}')
    if not PINNED_PY.is_file(): raise RuntimeError(f'pinned IDF Python missing: {PINNED_PY}')
    if host_running(): raise RuntimeError('OpsDeck.Host.exe is running; stop the host before flashing')
    if esptool_running(): raise RuntimeError('another esptool process is already running')
    p=exact_port(a.port)
    print(f'PREFLIGHT_OK port={p.device} vid={p.vid:04X} pid={p.pid:04X} bytes={len(data)} sha256={actual}')
    write,verify=commands(a.port,image,a.baud)
    if not a.execute:
        print('DRY_RUN_OK app-only 0x10000; no flash performed')
        return 0
    acquire_lock()
    stamp=time.strftime('%Y%m%d-%H%M%S')
    log_path=ROOT/'artifacts'/f'safe-flash-{stamp}.log'
    try:
        with log_path.open('w',encoding='utf-8') as log:
            log.write('SAFE FLASH APP ONLY\n')
            log.write(f'image={image}\nsha256={actual}\nport={a.port}\noffset={hex(OFFSET)}\n')
            log.flush()
            print('FLASH_BEGIN do-not-unplug')
            r=subprocess.run(write,stdout=log,stderr=subprocess.STDOUT,timeout=240)
            if r.returncode: raise RuntimeError(f'write_flash failed with exit {r.returncode}; see {log_path}')
            print('FLASH_WRITE_OK')
            v=subprocess.run(verify,stdout=log,stderr=subprocess.STDOUT,timeout=120)
            if v.returncode: raise RuntimeError(f'verify_flash failed with exit {v.returncode}; see {log_path}')
        print(f'SAFE_FLASH_OK verified log={log_path}')
        return 0
    except subprocess.TimeoutExpired as e:
        raise RuntimeError(f'esptool timed out during {e.cmd[-3:]}; process was terminated') from e
    finally:
        release_lock()


if __name__=='__main__':
    try: raise SystemExit(main())
    except Exception as e:
        print('SAFE_FLASH_FAILED',type(e).__name__,str(e),file=sys.stderr)
        raise SystemExit(2)