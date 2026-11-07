#!/usr/bin/env python3
"""Generate original synthetic speech to FILE only; no downloads or playback."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
from datetime import datetime, timezone

BASE = Path(__file__).resolve().parent

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()

def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=BASE.parent.parent / '.local/long-dictation/synthetic-20m')
    parser.add_argument('--voice', default='Samantha')
    parser.add_argument('--minutes', type=float, default=22)
    parser.add_argument('--rate', type=int, default=145)
    parser.add_argument('--plan-only', action='store_true', help='write reference/markers/plan, do not invoke say')
    args = parser.parse_args()
    if not math.isfinite(args.minutes) or not 20 <= args.minutes <= 120 or not 80 <= args.rate <= 250:
        parser.error('minutes must be20..120 and rate80..250')
    output = args.output.expanduser().absolute()
    if output.exists() or output.is_symlink():
        parser.error('output must be new; existing fixtures are never replaced')
    if not args.plan_only:
        listed = subprocess.run(['/usr/bin/say', '-v', '?'], check=True, capture_output=True, text=True).stdout
        names = []
        for line in listed.splitlines():
            match = re.match(r'^(.*?)\s+[a-z]{2,3}_[A-Za-z_]+\s+#', line)
            if match:
                names.append(match.group(1).strip())
        if args.voice not in names:
            parser.error('requested voice is not in installed say voice listing; no download is attempted')
    source = BASE / 'fixtures/passages.json'
    passages = json.loads(source.read_text())
    # Margin accounts for punctuation/voice variability; afinfo enforces real duration.
    target_words = math.ceil(args.minutes * args.rate * 1.10)
    reference_parts, markers = [], []
    marker_list = passages['markers']
    sections = max(len(marker_list), math.ceil(target_words / (len(passages['passage'].split()) + 16)))
    for index in range(sections):
        marker = marker_list[index % len(marker_list)]
        marker_sentence = f'Section {index + 1} begins. The checkpoint phrase is {marker}.'
        reference_parts.append(marker_sentence + '\n' + passages['passage'] + '\n')
        markers.append({'section': index + 1, 'phrase': marker, 'kind': 'section'})
    reference_parts.append(passages['tail'] + '\n')
    markers.append({'section': sections + 1, 'phrase': 'purple compass waits beside the silver lantern', 'kind': 'tail'})
    output.mkdir(parents=True, mode=0o700)
    reference = output / 'reference.txt'
    reference.write_text('\n'.join(reference_parts), encoding='utf-8')
    write_json(output / 'section-markers.json', markers)
    command = ['/usr/bin/say', '-v', args.voice, '-r', str(args.rate), '-f', str(reference),
               '-o', str(output / 'audio.aiff'), '--file-format=AIFF', '--data-format=BEI16@16000', '--channels=1']
    metadata = {'schemaVersion': 1, 'fixtureKind': 'synthetic-say-file-replay',
                'createdUTC': datetime.now(timezone.utc).isoformat(), 'voice': args.voice,
                'rateWordsPerMinute': args.rate, 'requestedMinutes': args.minutes,
                'wordCount': len(reference.read_text().split()), 'sectionCount': sections,
                'referenceSHA256': digest(reference), 'markerSHA256': digest(output / 'section-markers.json'),
                'generatorSHA256': digest(Path(__file__)), 'passagesSHA256': digest(source),
                'sayArguments': command, 'audioFile': 'audio.aiff', 'status': 'planned'}
    write_json(output / 'fixture.json', metadata)
    if args.plan_only:
        print(f'Plan/reference retained at {output}; no speech generated.')
        return 0
    try:
        subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        audio = output / 'audio.aiff'
        info = subprocess.run(['/usr/bin/afinfo', str(audio)], check=True, capture_output=True, text=True).stdout
        (output / 'afinfo.txt').write_text(info)
        match = re.search(r'estimated duration:\s*([0-9.]+)\s*sec', info)
        if not match:
            raise RuntimeError('afinfo duration missing')
        seconds = float(match.group(1))
        metadata.update(audioSHA256=digest(audio), audioBytes=audio.stat().st_size, durationSeconds=seconds)
        if seconds < 1200:
            raise RuntimeError('generated audio shorter than20minutes; retained for diagnosis, choose slower rate/newoutput')
        metadata['status'] = 'ready'
        print(f'Synthetic fixture ready: {seconds:.1f}s, {audio.stat().st_size}bytes, retained at {output}')
        return 0
    except Exception as error:
        metadata['status'] = 'generation-failed'
        metadata['failureType'] = type(error).__name__
        print(f'Generation failed; all reference/audio files retained at {output}', file=sys.stderr)
        return 1
    finally:
        write_json(output / 'fixture.json', metadata)

if __name__ == '__main__':
    sys.exit(main())
