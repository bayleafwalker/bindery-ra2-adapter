#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Non-strength metrics for an LLM shadow arm, from an arena run's decisions/ directory.

Pre-registered in 2026-09-27-llm-shadow-full-vocabulary-preregistration.md. Reads every
<arm>_*.ndjson decision log (the run must not use --no-decisions) and prints one JSON object.

Shadow proposals are the `strategy.shadow` records; the reference is the selector's own primary
proposals (`strategy.proposal`, role Primary) in the same matches.
"""
import glob
import json
import math
import os
import re
import sys
from collections import Counter, defaultdict

# Fields each tier adds over the one below (VocabularyTiers.cs); FULL_TIER is what the Full tier alone adds.
TIER_FIELDS = ("playbookParameters", "objectives", "regionsOfInterest")
FULL_TIER = ("posture", "budget", "composition", "attackConditions", "abortTriggers", "replanTriggers")
NUMBER = re.compile(r"\d")


def entropy(counter):
    total = sum(counter.values())
    return -sum(c / total * math.log2(c / total) for c in counter.values() if c) if total else 0.0


def normalised_entropy(by_faction):
    """Mean over factions of playbook entropy / log2(distinct playbooks the faction was seen to use or has)."""
    values = []
    for faction, counter in sorted(by_faction.items()):
        n = max(len(PLAYBOOKS.get(faction, ())), len(counter))
        if n > 1:
            values.append(entropy(counter) / math.log2(n))
    return sum(values) / len(values) if values else 0.0


def percentile(xs, p):
    if not xs:
        return None
    xs = sorted(xs)
    return xs[min(len(xs) - 1, math.ceil(p * len(xs)) - 1)]


PLAYBOOKS = {}


def main(run_dir, arm):
    shadow_by_faction, selector_by_faction = defaultdict(Counter), defaultdict(Counter)
    total = accepted = fog = grounded = same_playbook = authority_used = 0
    field_differs = Counter()
    latencies, matches = [], 0
    for path in sorted(glob.glob(os.path.join(run_dir, "decisions", f"{arm}_*.ndjson"))):
        matches += 1
        records = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
        # The arm's faction is fixed for a match; proposals do not carry it, activations and shadow records do.
        faction = next((r["data"]["faction"] for r in records if r["data"].get("faction")), "?")
        last_primary = None
        for r in records:
            d = r["data"]
            if r["kind"] == "strategy.proposal" and d.get("role") == "Primary" and d.get("intent"):
                last_primary = d["intent"]
                PLAYBOOKS.setdefault(faction, set()).add(last_primary["playbookId"])
                selector_by_faction[faction][last_primary["playbookId"]] += 1
            elif r["kind"] == "strategy.shadow":
                total += 1
                latencies.append(d["cost"]["latencySeconds"])
                if any("fog" in json.dumps(i).lower() for i in d.get("issues", [])):
                    fog += 1
                intent = d.get("intent")
                if not d.get("accepted") or intent is None:
                    continue
                accepted += 1
                PLAYBOOKS.setdefault(d["faction"], set()).add(intent["playbookId"])
                shadow_by_faction[d["faction"]][intent["playbookId"]] += 1
                rationale = (intent.get("rationale") or "").strip()
                if rationale and len(rationale) <= 400 and NUMBER.search(rationale):
                    grounded += 1
                if last_primary is not None and last_primary["playbookId"] == intent["playbookId"]:
                    same_playbook += 1
                    differs = [f for f in TIER_FIELDS + FULL_TIER
                               if json.dumps(intent.get(f), sort_keys=True) != json.dumps(last_primary.get(f), sort_keys=True)]
                    field_differs.update(differs)
                    if any(f in FULL_TIER for f in differs):
                        authority_used += 1
    out = {
        "arm": arm,
        "matches": matches,
        "shadowProposals": total,
        "validity": accepted / total if total else None,
        "fogRejections": fog,
        "latencyP50Seconds": percentile(latencies, 0.5),
        "latencyP95Seconds": percentile(latencies, 0.95),
        "variety": {
            "shadowPlaybookEntropy": round(normalised_entropy(shadow_by_faction), 3),
            "selectorPlaybookEntropy": round(normalised_entropy(selector_by_faction), 3),
            "shadowPlaybooks": {f: dict(c) for f, c in sorted(shadow_by_faction.items())},
        },
        "authorityUse": {"samePlaybookPairs": same_playbook, "fullTierDiffers": authority_used,
                         "rate": authority_used / same_playbook if same_playbook else None,
                         "byField": {f: field_differs[f] for f in TIER_FIELDS + FULL_TIER}},
        "explainability": {"groundedRationales": grounded, "rate": grounded / accepted if accepted else None},
    }
    json.dump(out, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: shadow_metrics.py <run-dir> <arm>")
    main(sys.argv[1], sys.argv[2])
