#!/usr/bin/env python3
"""Classify one lab run's launches (pre-registration 2026-09-29-defer-service-start-ab).

usage: classify.py <run dir> <arm> <index>  -> one TSV line per side: index arm run side class exit_code complete
crash = an NTSTATUS exit code (C + 7 hex digits) in evidence/syringe-client-<side>.log; ok = any other exit code;
unknown = none recorded. complete = control_plane_lifecycle_complete from the run's live-acceptance evidence.
"""
import glob
import json
import os
import re
import sys

run, arm, index = sys.argv[1], sys.argv[2], sys.argv[3]
complete = "unknown"
for path in glob.glob(os.path.join(run, "evidence", "**", "*live-acceptance-evidence.json"), recursive=True):
    try:
        complete = str(json.load(open(path))["qualification"]["control_plane_lifecycle_complete"]).lower()
    except (OSError, ValueError, KeyError):
        pass
for side in "ab":
    log = os.path.join(run, "evidence", f"syringe-client-{side}.log")
    text = open(log, errors="replace").read() if os.path.exists(log) else ""
    codes = re.findall(r"exit code ([0-9A-Fa-fx]+)", text)
    ntstatus = [c for c in codes if re.fullmatch(r"[Cc][0-9A-Fa-f]{7}", c)]
    klass = "crash" if ntstatus else "ok" if codes else "unknown"
    code = (ntstatus or codes or ["-"])[-1]
    print("\t".join([index, arm, os.path.basename(run.rstrip("/")), side, klass, code, complete]))
