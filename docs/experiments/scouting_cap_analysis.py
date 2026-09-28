#!/usr/bin/env python3
"""Paired analysis for the oracle scouting-cap pre-registration (2026-09-29).

usage: scouting_cap_analysis.py <baseline run dir> <capped run dir> [--min-seed N]
Each dir holds the arena's results.json (arm, opponent, map, seed, winner -- 0 means the arm won -- and
players.arm.faction). Pairs selector-oracle matches of the two runs by (opponent, map, seed) and prints wins per faction
for each run, the selector's wins in the baseline run for reference, and the paired comparison of capped against
baseline with an exact two-sided sign test on discordant pairs, overall and per faction.
"""
import json
import math
import sys
from collections import Counter

ARM = "selector-oracle"


def load(directory, min_seed, arm):
    rows = json.load(open(f"{directory}/results.json"))
    return {(m["opponent"], m["map"], m["seed"]): (m["winner"] == 0, m["players"]["arm"]["faction"])
            for m in rows if m["arm"] == arm and m["seed"] >= min_seed}


def sign_p(better, worse):
    n = better + worse
    if n == 0:
        return 1.0
    k = min(better, worse)
    return min(1.0, 2 * sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n)


def wins(runs):
    won, games = Counter(), Counter()
    for won_match, faction in runs.values():
        games[faction] += 1
        won[faction] += won_match
    return ", ".join(f"{f} {won[f]}/{games[f]}" for f in ("Allied", "Soviet"))


def main():
    args = sys.argv[1:]
    min_seed = 1
    if "--min-seed" in args:
        i = args.index("--min-seed")
        min_seed = int(args[i + 1])
        del args[i:i + 2]
    base, capped = load(args[0], min_seed, ARM), load(args[1], min_seed, ARM)
    if set(base) != set(capped) or not base:
        sys.exit("the two runs do not cover the same selector-oracle matches")
    print(f"baseline {ARM}: {wins(base)}")
    print(f"capped {ARM}: {wins(capped)}")
    selector = load(args[0], min_seed, "selector")
    if selector:
        print(f"baseline selector (reference): {wins(selector)}")
    for faction in ("all", "Allied", "Soviet"):
        better = worse = 0
        for key, (won_base, fac) in base.items():
            if faction != "all" and fac != faction:
                continue
            won_capped = capped[key][0]
            better += won_capped and not won_base
            worse += won_base and not won_capped
        print(f"paired {ARM} {faction}: capped better {better}, worse {worse}, sign p {sign_p(better, worse):.4g}")


if __name__ == "__main__":
    main()
