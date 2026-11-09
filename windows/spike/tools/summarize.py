"""Turn .cache/runs/*.jsonl into Markdown tables for RESULTS.md.

usage: python -I summarize.py <runs dir>
"""
import collections
import json
import pathlib
import statistics
import sys

runs = pathlib.Path(sys.argv[1])
loads, decodes, summaries, errors = {}, collections.defaultdict(list), {}, []
for f in sorted(runs.glob("*.jsonl")):
    for line in f.read_text(encoding="utf-8").splitlines():
        if not line.startswith("{"):
            continue
        o = json.loads(line)
        if o["kind"] == "load":
            loads[o["label"]] = o
        elif o["kind"] == "decode":
            decodes[(o["label"], o["fixture"])].append(o)
        elif o["kind"] == "summary":
            summaries[o["label"]] = o
        elif o["kind"] == "error":
            errors.append(o)

print("| Config | Load (s) | Peak RAM (GB) |")
print("|---|---|---|")
for label, o in loads.items():
    s = summaries.get(label, {})
    print(f"| {label} | {o['load_ms']/1000:.1f} | {s.get('peak_working_set_mb', 0)/1024:.1f} |")

print()
print("| Config | Fixture | Audio (s) | First decode (s) | Warm median (s) | Warm RTF | WER |")
print("|---|---|---|---|---|---|---|")
for (label, fixture), ds in decodes.items():
    if label.startswith("full-"):
        continue
    warm = [d["decode_ms"] / 1000 for d in ds[1:]] or [ds[0]["decode_ms"] / 1000]
    med = statistics.median(warm)
    print(f"| {label} | {fixture} | {ds[0]['audio_s']:.1f} | {ds[0]['decode_ms']/1000:.2f} | {med:.2f} | "
          f"{med/ds[0]['audio_s']:.3f} | {ds[0]['wer']*100:.1f}% |")

full = collections.defaultdict(lambda: [0, 0, 0.0, 0.0])
for (label, _), ds in decodes.items():
    if label.startswith("full-"):
        a = full[label]
        a[0] += ds[0]["word_errors"]; a[1] += ds[0]["ref_words"]
        a[2] += ds[0]["audio_s"]; a[3] += ds[0]["decode_ms"] / 1000
if full:
    print()
    print("| Config | Utterances | Audio (h) | Decode (min) | RTF | Corpus WER |")
    print("|---|---|---|---|---|---|")
    for label, (e, w, au, de) in full.items():
        n = sum(1 for k in decodes if k[0] == label)
        print(f"| {label} | {n} | {au/3600:.2f} | {de/60:.1f} | {de/au:.4f} | {e/w*100:.2f}% |")

for e in errors:
    print(f"\nERROR {e}")
