"""Convert every LibriSpeech test-clean utterance to 16 kHz WAV + .txt for a corpus-level WER pass.

usage: python -I make_librispeech_full.py <LibriSpeech/test-clean dir> <out dir>
"""
import pathlib
import sys

import soundfile as sf

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
n = 0
for trans in sorted(src.glob("*/*/*.trans.txt")):
    for line in trans.read_text().splitlines():
        utt, text = line.split(" ", 1)
        audio, rate = sf.read(trans.parent / f"{utt}.flac", dtype="int16")
        sf.write(out / f"{utt}.wav", audio, rate, subtype="PCM_16")
        (out / f"{utt}.txt").write_text(text)
        n += 1
print(f"{n} utterances")
