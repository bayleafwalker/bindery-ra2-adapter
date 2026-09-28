#!/usr/bin/env python3
"""Paired analysis for the SeenAttackForceRatio pre-registration (2026-09-29).

usage: seen_ratio_analysis.py <run-0.8 dir> <run-1.0 dir> [--min-seed N]
Each dir holds the arena's results.json (a list of matches with arm, opponent, map, seed, winner -- 0 means the arm
won -- and players.arm.faction). Prints wins per arm and faction for both runs and, per arm, the paired comparison of
1.0 against 0.8 with an exact two-sided sign test on discordant pairs.
"""
import json
import math
import sys
from collections import Counter


def load(directory, min_seed):
    rows = json.load(open(f"{directory}/results.json"))
    return {(m["arm"], m["opponent"], m["map"], m["seed"]): (m["winner"] == 0, m["players"]["arm"]["faction"])
            for m in rows if m["seed"] >= min_seed}


def sign_p(better, worse):
    n = better + worse
    if n == 0:
        return 1.0
    k = min(better, worse)
    tail = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def main():
    args = sys.argv[1:]
    min_seed = 1
    if "--min-seed" in args:
        i = args.index("--min-seed")
        min_seed = int(args[i + 1])
        del args[i:i + 2]
    low, high = load(args[0], min_seed), load(args[1], min_seed)
    if set(low) != set(high):
        sys.exit("the two runs do not cover the same matches")
    for label, runs in (("0.8", low), ("1.0", high)):
        wins, games = Counter(), Counter()
        for (arm, *_), (won, faction) in runs.items():
            games[arm, faction] += 1
            wins[arm, faction] += won
        for arm in sorted({a for a, _ in games}):
            parts = [f"{f} {wins[arm, f]}/{games[arm, f]}" for f in ("Allied", "Soviet")]
            total = sum(wins[arm, f] for f in ("Allied", "Soviet"))
            n = sum(games[arm, f] for f in ("Allied", "Soviet"))
            print(f"{label} {arm}: {total}/{n} ({', '.join(parts)})")
    for arm in sorted({k[0] for k in low}):
        for faction in ("all", "Allied", "Soviet"):
            better = worse = 0
            for key, (won08, fac) in low.items():
                if key[0] != arm or (faction != "all" and fac != faction):
                    continue
                won10 = high[key][0]
                better += won10 and not won08
                worse += won08 and not won10
            print(f"paired {arm} {faction}: 1.0 better {better}, worse {worse}, sign p {sign_p(better, worse):.4g}")


if __name__ == "__main__":
    main()
