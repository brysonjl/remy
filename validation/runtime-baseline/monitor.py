#!/usr/bin/env python3
"""Sample the Remy Dev XCTest host RSS; excludes Ollama and helper processes."""
import argparse
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--timeout', type=int, default=660)
args = parser.parse_args()
samples = []
seen = False
started = time.monotonic()
while time.monotonic() - started < args.timeout:
    lines = subprocess.check_output(['ps', '-axo', 'pid=,rss=,%cpu=,comm='], text=True).splitlines()
    processes = [s.split(None, 3) for s in lines if '/Build/Products/Debug/Remy Dev.app/' in s and s.endswith('/Remy Dev')]
    if processes:
        seen = True
        for pid, rss, cpu, _ in processes:
            samples.append({'elapsedSeconds': round(time.monotonic() - started, 3),
                            'pid': int(pid), 'rssKiB': int(rss), 'cpuPercent': float(cpu)})
    elif seen:
        break
    time.sleep(1)
report = {'scope': 'XCTest host process RSS sampled every second, including app/test overhead; not sole-ASR memory. Ollama/helper memory excluded.',
          'samples': samples, 'peakRssKiB': max((s['rssKiB'] for s in samples), default=0),
          'observedHost': seen}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({'samples': len(samples), 'peakRssKiB': report['peakRssKiB']}))
