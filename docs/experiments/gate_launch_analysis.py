#!/usr/bin/env python3
"""Attack-launch classification for the base-sighting-cap pre-registration.

Stdlib only.  Usage:
  python3 docs/experiments/gate_launch_analysis.py DECISIONS_DIR [DECISIONS_DIR ...]
          [--include-llm] [--results GLOB]

Part A (decision logs).  Each DECISIONS_DIR holds `<arm>_<opp>_<map>_<seed>.ndjson` + `.match.json`
(extract the *.tar.xz archives under docs/results/ first; nothing extracted is committed).  Per
operations.plan tick the squad note is one of
  "squads: staging at rN (army A/B, conditions ..., force ratio R/Q)"
  "squads: attacking rN with army value V"
A launch is an `attacking` tick whose previous squad tick was not `attacking`.  The launch note carries no
ratio, so the gate outcome at launch is not observable; the class is decided from the previous tick:
  jump    previous tick staged with ratio R < requirement Q (the gate opened between the two ticks, by a
          ratio rise, a requirement drop, or both)
  steady  previous tick staged with R >= Q (the ratio already cleared; the launch waited for something else)
  other   previous squad tick was not a staging tick (e.g. defending), so no ratio to compare
Arms counted as "selector": the `selector` arm, plus `llm-shadow-t3` (its Primary strategist is the selector;
the LLM only shadows).  Duplicate (opponent, map, seed) cells across archives are counted once, preferring the
pure `selector` arm.  `llm-t1` (LLM primary, same operations layer) is only tabulated with --include-llm.

Part B (results.json.xz).  Big-n proxy without ratios: `selector` arm first-attack time vs win, per file.
"""
import glob
import json
import lzma
import os
import re
import statistics
import sys
from collections import defaultdict

FPS = 15
STAGE = re.compile(r"squads: staging at (\S+) \(army (\d+)/(\d+), conditions (hold|not met), force ratio ([\d.]+)/([\d.]+)\)")
ATTACK = re.compile(r"squads: attacking (\S+) with army value (\d+)")
FACTION = re.compile(r"queued (GA|NA)[A-Z]+")


def load_match(base):
    with open(base + ".match.json") as f:
        m = json.load(f)
    faction = None
    ticks = []  # (frame, state, info)
    with open(base + ".ndjson") as f:
        for line in f:
            r = json.loads(line)
            if r["kind"] != "operations.plan":
                continue
            notes = r["data"].get("notes", [])
            for n in notes:
                if faction is None:
                    fm = FACTION.search(n)
                    if fm:
                        faction = "Allied" if fm.group(1) == "GA" else "Soviet"
                sm = STAGE.search(n)
                if sm:
                    ticks.append((r["frame"], "staging", (float(sm.group(5)), float(sm.group(6)), sm.group(4))))
                    break
                am = ATTACK.search(n)
                if am:
                    ticks.append((r["frame"], "attacking", int(am.group(2))))
                    break
                if n.startswith("squads: defending"):
                    ticks.append((r["frame"], "defending", None))
                    break
    return m, faction or "?", ticks


def launches(ticks):
    out = []
    for i, (fr, st, info) in enumerate(ticks):
        if st != "attacking" or i == 0 or ticks[i - 1][1] == "attacking":
            continue
        pf, pst, pinfo = ticks[i - 1]
        if pst == "staging":
            ratio, req, _ = pinfo
            cls = "jump" if ratio < req else "steady"
        else:
            ratio = req = None
            cls = "other"
        # requirement one tick earlier than the previous, to show a drop already under way
        pp = ticks[i - 2] if i >= 2 and ticks[i - 2][1] == "staging" else None
        out.append(dict(frame=fr, sec=fr / FPS, cls=cls, ratio=ratio, req=req, army=info,
                        prev_req=(pp[2][1] if pp else None), prev_ratio=(pp[2][0] if pp else None)))
    return out


def med(xs):
    return statistics.median(xs) if xs else float("nan")


def part_a(dirs, include_llm):
    cells = {}
    for d in dirs:
        for nd in sorted(glob.glob(os.path.join(d, "**", "*.ndjson"), recursive=True)):
            base = nd[:-len(".ndjson")]
            if not os.path.exists(base + ".match.json"):
                continue
            arm = os.path.basename(base).split("_")[0]
            if arm == "selector" or arm == "llm-shadow-t3":
                group = "selector"
            elif arm == "llm-t1" and include_llm:
                group = "llm-t1"
            else:
                continue
            m, fac, ticks = load_match(base)
            key = (group, m["opponent"], m["map"], m["seed"], m["split"])
            if key in cells and not (arm == "selector" and cells[key]["arm"] != "selector"):
                continue
            cells[key] = dict(arm=arm, group=group, m=m, faction=fac, la=launches(ticks),
                              name=os.path.basename(base), dir=os.path.basename(os.path.dirname(base)))
    for group in sorted({k[0] for k in cells}):
        print(f"\n=== Part A: {group} decision logs ===")
        rows = defaultdict(list)
        for k, c in cells.items():
            if k[0] == group:
                rows[(c["faction"], c["m"]["split"])].append(c)
        hdr = ("faction", "split", "matches", "w/launch", "launches", "jump", "jump%", "first=jump W/N",
               "first=steady|other W/N", "med launch s")
        print(" | ".join(hdr))
        for (fac, sp), cs in sorted(rows.items()):
            allL = [l for c in cs for l in c["la"]]
            fj = [c for c in cs if c["la"] and c["la"][0]["cls"] == "jump"]
            fs = [c for c in cs if c["la"] and c["la"][0]["cls"] != "jump"]
            wj = sum(c["m"]["winner"] == 0 for c in fj)
            ws = sum(c["m"]["winner"] == 0 for c in fs)
            nj = sum(l["cls"] == "jump" for l in allL)
            print(f"{fac} | {sp} | {len(cs)} | {sum(1 for c in cs if c['la'])} | {len(allL)} | {nj} | "
                  f"{(100 * nj / len(allL)) if allL else float('nan'):.0f}% | {wj}/{len(fj)} | {ws}/{len(fs)} | "
                  f"{med([l['sec'] for l in allL]):.0f}")
            for label, sel in (("first launch = jump", fj), ("first launch = steady/other", fs)):
                for c in sel:
                    l = c["la"][0]
                    rq = f"prev {l['ratio']:.2f}/{l['req']:.2f}" if l["ratio"] is not None else "prev n/a"
                    print(f"    {label}: {c['name']} [{c['dir']}] winner={c['m']['winner']} "
                          f"launch@{l['sec']:.0f}s army={l['army']} {rq} (tick before: "
                          f"{l['prev_ratio']}/{l['prev_req']}) launches={len(c['la'])}")
            nol = [c for c in cs if not c["la"]]
            for c in nol:
                print(f"    no launch: {c['name']} winner={c['m']['winner']}")
        allc = [c for k, c in cells.items() if k[0] == group]
        print(f"matches counted: {len(allc)}; distinct archives: {sorted({c['dir'] for c in allc})}")


def part_b(pattern):
    files = sorted(glob.glob(pattern, recursive=True))
    print("\n=== Part B: selector first-attack time vs win (results.json.xz, no ratios available) ===")
    if not files:
        print("no results files matched", pattern)
        return
    buckets = [(0, 150), (150, 200), (200, 250), (250, 400), (400, 10 ** 6)]
    for f in files:
        with lzma.open(f, "rt") as fh:
            res = json.load(fh)
        sel = [r for r in res if r["arm"] == "selector"]
        print(f"\n{f}: {len(sel)} selector matches")
        by = defaultdict(lambda: defaultdict(lambda: [0, 0]))
        for r in sel:
            fa = r["players"]["arm"].get("firstAttackSeconds")
            fac = r["players"]["arm"]["faction"]
            if fa is None or fa <= 0:
                b = "never"
            else:
                b = next(f"{lo}-{hi if hi < 10 ** 6 else 'inf'}s" for lo, hi in buckets if lo <= fa < hi)
            for key in ((fac, r["split"], b), (fac, r["split"], "all")):
                by[key[:2]][key[2]][1] += 1
                by[key[:2]][key[2]][0] += r["winner"] == 0
        for (fac, sp), bd in sorted(by.items()):
            cells = ", ".join(f"{b}: {w}/{n} ({100 * w / n:.0f}%)" for b, (w, n) in
                              sorted(bd.items(), key=lambda kv: (kv[0] == "all", kv[0] == "never", kv[0])))
            print(f"  {fac} {sp}: {cells}")
        # hard-tier ai-* opponents by map: the cell class named in the hypothesis
        hard = defaultdict(lambda: defaultdict(lambda: [0, 0]))
        for r in sel:
            if ":hard" in r["opponent"]:
                a = r["players"]["arm"]
                k = (a["faction"], r["map"])
                fa = a.get("firstAttackSeconds") or 0
                hard[k][fa][1] += 1
                hard[k][fa][0] += r["winner"] == 0
        for (fac, mp), d in sorted(hard.items()):
            cells = ", ".join(f"first attack {fa}s: {w}/{n}" for fa, (w, n) in sorted(d.items()))
            print(f"  hard ai-* {fac} {mp}: {cells}")


def main(argv):
    include_llm = "--include-llm" in argv
    pattern = "docs/results/2026-09-29-seen-ratio/*-0.8/results.json.xz"
    args = []
    it = iter(argv)
    for a in it:
        if a == "--results":
            pattern = next(it)
        elif a != "--include-llm":
            args.append(a)
    if args:
        part_a(args, include_llm)
    part_b(pattern)


if __name__ == "__main__":
    main(sys.argv[1:])
