#!/usr/bin/env python3
"""Controlled A00 tag pull, rejecting manifest drift before any evaluation."""
import datetime
import hashlib
import json
from pathlib import Path
import shutil
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
PLAN = ROOT / 'validation/provider-baseline/qwen-install-plan.json'
OUT = ROOT / 'validation/results/2026-10-19-a00'
BASE = 'http://127.0.0.1:11434'
EXPECTED = 'd8b0f5e9760cd1682034f292d7ef72ec46f432149be0df7574bf2d6e92e38c04'
MODEL = 'qwen3.5:4b'


def request_json(path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(BASE + path, data=data, headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req, timeout=60) as response:
        return json.load(response)


def remote(plan):
    with urllib.request.urlopen(plan['manifest_url'], timeout=60) as response:
        raw = response.read()
    actual = hashlib.sha256(raw).hexdigest()
    if actual != EXPECTED:
        raise RuntimeError(f'Remote manifest drift: {actual}; expected {EXPECTED}')
    return json.loads(raw), actual


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    result = {'started_at': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'model':MODEL, 'expected_manifest_sha256':EXPECTED,
              'scope':'Installation only; no evaluation, generation, unloading or settings changes',
              'pinning_limitation':'Documented tag pull with before/after digest verification; not an immutable pull operation'}
    log_path = OUT / 'qwen-install.log'
    def log(message):
        print(message, flush=True)
        with log_path.open('a') as output: output.write(message + '\n')
    try:
        plan = json.loads(PLAN.read_text())
        assert plan['manifest_sha256'] == EXPECTED
        manifest, digest = remote(plan)
        result['remote_before_sha256'] = digest
        result['version'] = request_json('/api/version')
        result['tags_before'] = request_json('/api/tags')
        store = Path.home() / '.ollama/models'
        if store.is_symlink() or not store.is_dir():
            raise RuntimeError('Expected default model store unavailable; inspect actual server store before continuing')
        missing = []
        for entry in [manifest['config'], *manifest['layers']]:
            try:
                with urllib.request.urlopen(urllib.request.Request(BASE+'/api/blobs/'+entry['digest'],method='HEAD'),timeout=30) as response:
                    status=response.status
            except urllib.error.HTTPError as error: status=error.code
            if status==404: missing.append(entry)
            elif status!=200: raise RuntimeError(f'Unexpected local blob status {status}')
        free = shutil.disk_usage(store).free
        needed = sum(entry['size'] for entry in missing)
        result['store_path'] = str(store)
        result['free_bytes_before'] = free
        result['missing_blob_bytes_before'] = needed
        result['manifest'] = manifest
        if free < needed + 10 * 1024**3:
            raise RuntimeError('Insufficient model-store free space for pull plus 10GiB working reserve')
        log(f'Preflight passed: remote {digest}; missing {needed} bytes; free {free} bytes; Ollama {result["version"]["version"]}')
        req=urllib.request.Request(BASE+'/api/pull',data=json.dumps({'model':MODEL,'stream':True}).encode(),headers={'Content-Type':'application/json'})
        started=time.monotonic()
        last_status=None
        last_report=started
        events=0
        log('Starting authorized documented tag pull; no inference requested.')
        success=False
        with urllib.request.urlopen(req,timeout=60) as response:
            for raw in response:
                event=json.loads(raw)
                events+=1
                if 'error' in event: raise RuntimeError(event['error'])
                status=event.get('status','')
                if status!=last_status or time.monotonic()-last_report>=30:
                    log(f'{status}: {event.get("completed",0)}/{event.get("total",0)} bytes')
                    last_status=status; last_report=time.monotonic()
                success=success or status=='success'
        result['pull_seconds']=round(time.monotonic()-started,3)
        result['pull_progress_events']=events
        if not success: raise RuntimeError('Pull stream ended without success')
        tags=request_json('/api/tags')
        result['tags_after']=tags
        local=next((item for item in tags['models'] if item['name']==MODEL),None)
        if local is None or local['digest']!=EXPECTED:
            raise RuntimeError('Installed model missing or local manifest digest drifted; do not evaluate')
        _, after=remote(plan)
        result['remote_after_sha256']=after
        result['installed_model']=local
        show=request_json('/api/show',{'model':MODEL})
        (OUT/'qwen-install-show.json').write_text(json.dumps(show,indent=2)+'\n')
        result['capabilities']=show.get('capabilities')
        result['thinking']=show.get('thinking')
        result['details']=show.get('details')
        result['license_prefix']=show.get('license','')[:180]
        verified=[]
        for entry in [manifest['config'],*manifest['layers']]:
            path=store/'blobs'/entry['digest'].replace(':','-')
            if path.is_symlink() or not path.is_file():
                raise RuntimeError(f'Expected local blob unavailable: {path.name}')
            sha=hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda:stream.read(4*1024*1024),b''): sha.update(chunk)
            if path.stat().st_size!=entry['size'] or sha.hexdigest()!=entry['digest'].split(':')[1]:
                raise RuntimeError(f'Installed blob integrity mismatch: {path.name}')
            verified.append({'digest':entry['digest'],'size':entry['size'],'sha256_verified':True})
        result['verified_blobs']=verified
        result['free_bytes_after']=shutil.disk_usage(store).free
        result['status']='installed and verified; evaluation not run'
        log(f'Complete: local and remote digest match; {len(verified)} local blobs SHA256 verified; capabilities={result["capabilities"]}; thinking={result["thinking"]}')
    except Exception as error:
        result['status']='aborted; do not evaluate'
        result['error']=str(error)
        log(f'ABORT: {error}')
        raise
    finally:
        result['finished_at']=datetime.datetime.now(datetime.timezone.utc).isoformat()
        (OUT/'qwen-install.json').write_text(json.dumps(result,indent=2)+'\n')


if __name__=='__main__': main()
