#!/usr/bin/env python3
"""Minimal OpenAI-compatible /v1/chat/completions shim over `claude -p` (subscription).

Each request becomes one headless Claude Code call with the request's system prompt
replacing Claude Code's, no tools/MCP/settings, and the request's JSON schema as
structured output. Every request/response pair is appended to LOG (ndjson).
Usage: claude_proxy.py <port> <claude model alias> <log path> [max concurrent]
"""
import json, subprocess, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT, MODEL, LOG = int(sys.argv[1]), sys.argv[2], sys.argv[3]
SLOTS = threading.Semaphore(int(sys.argv[4]) if len(sys.argv) > 4 else 4)
LOCK = threading.Lock()


def ask(system, user, schema):
    cmd = ["claude", "-p", "--model", MODEL, "--system-prompt", system, "--tools", "",
           "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence",
           "--disable-slash-commands", "--output-format", "json"]
    if schema is not None:
        cmd += ["--json-schema", json.dumps(schema)]
    with SLOTS:
        p = subprocess.run(cmd, input=user, capture_output=True, text=True, timeout=300)
    d = json.loads(p.stdout)
    if d.get("is_error"):
        raise RuntimeError(str(d.get("result"))[:300])
    out = d.get("structured_output")
    content = json.dumps(out) if out is not None else d.get("result", "")
    u = d.get("usage", {})
    tin = u.get("input_tokens", 0) + u.get("cache_creation_input_tokens", 0) + u.get("cache_read_input_tokens", 0)
    return content, tin, u.get("output_tokens", 0), d


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        self._send(200, {"data": [{"id": "claude-" + MODEL, "object": "model"}]})

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        msgs = req.get("messages", [])
        system = "\n".join(m["content"] for m in msgs if m["role"] == "system")
        user = "\n".join(m["content"] for m in msgs if m["role"] == "user")
        rf = req.get("response_format") or {}
        schema = (rf.get("json_schema") or {}).get("schema")
        t0 = time.time()
        try:
            content, tin, tout, raw = ask(system, user, schema)
            resp = {"id": raw.get("uuid", ""), "object": "chat.completion", "model": "claude-" + MODEL,
                    "choices": [{"index": 0, "finish_reason": "stop",
                                 "message": {"role": "assistant", "content": content}}],
                    "usage": {"prompt_tokens": tin, "completion_tokens": tout}}
            code, err = 200, None
        except Exception as e:  # surface as an upstream error; the arena records a failed proposal
            resp, code, err = {"error": {"message": str(e)[:300]}}, 502, str(e)[:300]
            content, raw = None, {}
        with LOCK, open(LOG, "a") as f:
            f.write(json.dumps({"t": t0, "sec": round(time.time() - t0, 2), "user": user, "reply": content,
                                "error": err, "cost_usd": raw.get("total_cost_usd")}) + "\n")
        self._send(code, resp)


ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
