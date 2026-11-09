"""Build 10 s / 60 s / 300 s real-speech fixtures from LibriSpeech test-clean.

Each fixture is consecutive utterances from one speaker's chapter(s), joined with
0.25 s of silence, written as 16 kHz mono 16-bit WAV with a matching .txt reference.
Three different speakers are used so one voice doesn't decide the result.

usage: python -I make_librispeech_fixtures.py <LibriSpeech/test-clean dir> <out dir>
"""
import collections
import pathlib
import sys

import numpy as np
import soundfile as sf

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)

# speaker -> ordered list of (flac path, reference text)
speakers = collections.defaultdict(list)
for trans in sorted(src.glob("*/*/*.trans.txt")):
    for line in trans.read_text().splitlines():
        utt, text = line.split(" ", 1)
        speakers[utt.split("-")[0]].append((trans.parent / f"{utt}.flac", text))

def total_seconds(items):
    return sum(sf.info(p).duration for p, _ in items)

# Speakers with the most audio first; give the longest fixture the speaker with the most.
ranked = sorted(speakers, key=lambda s: -total_seconds(speakers[s]))
gap = np.zeros(4000, dtype=np.float32)

for target, speaker in zip((300, 60, 10), ranked):
    chunks, texts, length = [], [], 0.0
    for path, text in speakers[speaker]:
        audio, rate = sf.read(path, dtype="float32")
        assert rate == 16000 and audio.ndim == 1
        # For the 10 s clip, stop before overshooting badly.
        if target == 10 and length > 0 and length + len(audio) / rate > 13:
            continue
        chunks += [audio, gap]
        texts.append(text)
        length += (len(audio) + len(gap)) / rate
        if length >= target:
            break
    name = out / f"libri-{target:03d}s"
    sf.write(name.with_suffix(".wav"), np.concatenate(chunks), 16000, subtype="PCM_16")
    name.with_suffix(".txt").write_text(" ".join(texts))
    print(f"{name.name}.wav  {length:.1f}s  speaker={speaker}  utterances={len(texts)}")
