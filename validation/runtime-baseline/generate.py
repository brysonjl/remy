#!/usr/bin/env python3
"""Generate public synthetic fixtures using an already installed macOS voice."""
import argparse
import hashlib
import json
import pathlib
import platform
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--voice', default='Samantha', help='Exact already-installed English say voice; never installs voices')
a = p.parse_args()
voices = subprocess.check_output(['/usr/bin/say', '-v', '?'], text=True)
if not any(line.split('#')[0].strip().startswith(a.voice + ' ') and 'en_' in line.split('#')[0] for line in voices.splitlines()):
    p.error('Choose an already-listed English voice from: say -v "?"')
fixtures = json.loads((pathlib.Path(__file__).with_name('fixtures.json')).read_text())
out = ROOT / '.local/runtime-baseline'
out.mkdir(parents=True, exist_ok=True)
for fixture in fixtures:
    path = out / (fixture['id'] + '.wav')
    subprocess.run(['/usr/bin/say', '-v', a.voice, '-r', '165', '-o', str(path), '--file-format=WAVE', '--data-format=LEI16@16000', fixture['reference']], check=True)
    fixture['audioFile'] = path.name
    fixture['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
manifest = {'corpusVersion': 'remy-public-synthetic-v1', 'voice': a.voice, 'rate': 165,
            'generator': 'macOS say; mono 16 kHz PCM; no microphone', 'os': platform.platform(), 'fixtures': fixtures}
(out / 'fixtures-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(f'Generated {len(fixtures)} public synthetic fixtures in {out}')
