#!/usr/bin/env python3
"""Traced divergence of selector and selector-oracle as Allied (pre-registration 2026-09-29).

usage: oracle_divergence.py <run dir> <trace dir>
<run dir> holds the arena's results.json (arm, opponent, map, seed, winner -- 0 means the arm won -- and
players.arm.faction); <trace dir> holds the arena's --trace files, <arm>_<opponent with ':' as '-'>_<map>_<seed>.txt.

Pairs the two arms by (opponent, map, seed). Gate: the Allied paired split (selector-only wins against oracle-only
wins) with an exact two-sided sign test; the classification below runs only when p < 0.05. For each Allied
discordant pair it finds the first game second at which the two arms' decisions differ in one category and names
that category the pair's divergence. Categories, compared as decisions and targets (not numbers, which differ between
the two views from the first second), and their precedence when several first differ in the same second:
  intent       playbook of strategy.intent_activated
  composition  operations.plan notes "composition: ..." and "queued <TYPE>" / "placement: <TYPE>"
  scouting     operations.plan notes "scout: heading to <region>"
  defend       operations.plan notes "squads: defending <region>"
  attack-gate  the squad state per plan: attacking <region> / staging with conditions hold / not met
  none         no category differs before the earlier of the two match ends
Primary cause: a category that is the divergence of >= 50% of selector-only pairs and whose share there is >= 2x its
share among oracle-only pairs (with no oracle-only pairs, the 50% condition alone, reported as such).
"""
import json
import math
import re
import sys
from collections import Counter

CATEGORIES = ("intent", "composition", "scouting", "defend", "attack-gate")
QUEUED = re.compile(r"queued ([A-Z0-9]+)")
PLACED = re.compile(r"^placement: ([A-Z0-9]+)")
SCOUT = re.compile(r"^scout: heading to (\S+)")
DEFEND = re.compile(r"^squads: defending (\S+)")
ATTACK = re.compile(r"^squads: attacking (\S+)")
STAGING = re.compile(r"^squads: staging at (\S+) \(army [^,]*, conditions (hold|not met)")


def sign_p(a, b):
    n = a + b
    if n == 0:
        return 1.0
    k = min(a, b)
    return min(1.0, 2 * sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n)


def decisions(path):
    """{second: {category: tuple of decisions}} and the last second, from one trace."""
    by_second, last = {}, 0
    for line in open(path):
        if line.startswith("result "):
            break
        parts = line.split(" ", 3)
        if parts[0] != "log" or len(parts) < 4:
            continue
        second, kind, data = int(parts[1]), parts[2], json.loads(parts[3])
        last = max(last, second)
        slot = by_second.setdefault(second, {c: [] for c in CATEGORIES})
        if kind == "strategy.intent_activated":
            slot["intent"].append(str(data.get("playbookId") or data.get("intent", {}).get("playbookId")))
        elif kind == "operations.plan":
            for note in data.get("notes", []):
                if note.startswith("composition:"):
                    slot["composition"].append(note)
                for pattern in (QUEUED, PLACED):
                    for m in pattern.finditer(note) if pattern is QUEUED else [pattern.match(note)]:
                        if m:
                            slot["composition"].append(m.group(1))
                for pattern, category in ((SCOUT, "scouting"), (DEFEND, "defend")):
                    if m := pattern.match(note):
                        slot[category].append(m.group(1))
                if m := ATTACK.match(note):
                    slot["attack-gate"].append("attack " + m.group(1))
                elif m := STAGING.match(note):
                    slot["attack-gate"].append(f"stage {m.group(1)} {m.group(2)}")
    return {s: {c: tuple(v) for c, v in cats.items()} for s, cats in by_second.items()}, last


def divergence(a_path, b_path):
    a, a_end = decisions(a_path)
    b, b_end = decisions(b_path)
    empty = {c: () for c in CATEGORIES}
    for second in sorted(set(a) | set(b)):
        if second > min(a_end, b_end):
            break
        da, db = a.get(second, empty), b.get(second, empty)
        for category in CATEGORIES:
            if da[category] != db[category]:
                return category, second
    return "none", None


def main():
    run_dir, trace_dir = sys.argv[1], sys.argv[2]
    rows = json.load(open(f"{run_dir}/results.json"))
    result = {(m["arm"], m["opponent"], m["map"], m["seed"]): (m["winner"] == 0, m["players"]["arm"]["faction"]) for m in rows}
    pairs = {}
    for (arm, opponent, map_id, seed), (won, faction) in result.items():
        pairs.setdefault((opponent, map_id, seed), {})[arm] = (won, faction)
    selector_only, oracle_only = [], []
    for key, arms in sorted(pairs.items()):
        if set(arms) != {"selector", "selector-oracle"}:
            sys.exit(f"pair {key} lacks an arm: {sorted(arms)}")
        (s_won, s_fac), (o_won, o_fac) = arms["selector"], arms["selector-oracle"]
        if s_fac != o_fac:
            sys.exit(f"pair {key} has different factions")
        if s_fac != "Allied":
            continue
        if s_won and not o_won:
            selector_only.append(key)
        elif o_won and not s_won:
            oracle_only.append(key)
    p = sign_p(len(selector_only), len(oracle_only))
    allied = sum(1 for arms in pairs.values() if arms["selector"][1] == "Allied")
    print(f"Allied pairs {allied}: selector-only {len(selector_only)}, oracle-only {len(oracle_only)}, sign p {p:.4g}")
    if p >= 0.05:
        print("gate: not significant -- stop, no classification")
        return

    def trace(arm, key):
        opponent, map_id, seed = key
        return f"{trace_dir}/{arm}_{opponent.replace(':', '-')}_{map_id}_{seed}.txt"

    counts = {}
    for label, keys in (("selector-only", selector_only), ("oracle-only", oracle_only)):
        counts[label] = Counter()
        for key in keys:
            category, second = divergence(trace("selector", key), trace("selector-oracle", key))
            counts[label][category] += 1
            print(f"{label} {key[0]} {key[1]} {key[2]}: {category} at {second}")
    n_sel, n_orc = len(selector_only), len(oracle_only)
    for category in CATEGORIES + ("none",):
        share_sel = counts["selector-only"][category] / n_sel
        share_orc = counts["oracle-only"][category] / n_orc if n_orc else None
        orc = f"{counts['oracle-only'][category]}/{n_orc} ({share_orc:.2f})" if n_orc else "0/0"
        primary = share_sel >= 0.5 and (share_orc is None or share_sel >= 2 * share_orc)
        note = " PRIMARY" + (" (no oracle-only pairs: 50% condition alone)" if primary and share_orc is None else "") if primary else ""
        print(f"{category}: selector-only {counts['selector-only'][category]}/{n_sel} ({share_sel:.2f}), oracle-only {orc}{note}")


if __name__ == "__main__":
    main()
