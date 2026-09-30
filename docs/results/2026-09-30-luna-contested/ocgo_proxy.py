#!/usr/bin/env python3
"""Local OpenAI-compatible forwarder to OpenCode Go for the bindery arena.

Adds the opencode-go key (read from opencode's auth.json, never logged), a User-Agent and the
x-opencode-session header OpenCode Go requires, so the arena points --llm-endpoint at
http://127.0.0.1:<port>/v1 and never holds the key. Each request's model, latency, status and
usage are appended to LOG (ndjson), no prompts or keys.
Models named gpt-* only speak the Responses API on OpenCode Go, so a chat/completions request for them is
translated to /responses (system -> instructions, response_format json_schema -> text.format) and the reply
back to a chat.completion.
Usage: ocgo_proxy.py <port> <log path> [session id]
"""
import json, os, sys, threading, time, urllib.error, urllib.request, uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT, LOG = int(sys.argv[1]), sys.argv[2]
SESSION = sys.argv[3] if len(sys.argv) > 3 else "bindery-arena-" + uuid.uuid4().hex[:12]
UPSTREAM = "https://opencode.ai/zen/go/v1"
KEY = json.load(open(os.path.expanduser("~/.local/share/opencode/auth.json")))["opencode-go"]["key"]
LOCK = threading.Lock()
NO_RESPONSE_FORMAT = {"deepseek-v4-pro"}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    @staticmethod
    def _to_responses(req):
        msgs = req.get("messages", [])
        out = {"model": req["model"],
               "instructions": "\n".join(m["content"] for m in msgs if m["role"] == "system"),
               "input": [{"role": m["role"], "content": m["content"]} for m in msgs if m["role"] != "system"]}
        if req.get("max_tokens"):
            out["max_output_tokens"] = max(int(req["max_tokens"]), 16)
        rf = req.get("response_format") or {}
        if rf.get("type") == "json_schema":
            js = rf["json_schema"]
            out["text"] = {"format": {"type": "json_schema", "name": js.get("name", "out"),
                                      "strict": js.get("strict", True), "schema": js["schema"]}}
        elif rf.get("type") == "json_object":
            out["text"] = {"format": {"type": "json_object"}}
        return out

    @staticmethod
    def _from_responses(d, model):
        text = "".join(c.get("text", "") for o in d.get("output", []) if o.get("type") == "message"
                       for c in o.get("content", []) if c.get("type") == "output_text")
        u = d.get("usage") or {}
        return {"id": d.get("id", ""), "object": "chat.completion", "model": model,
                "choices": [{"index": 0, "finish_reason": "stop" if d.get("status") == "completed" else "length",
                             "message": {"role": "assistant", "content": text}}],
                "usage": {"prompt_tokens": u.get("input_tokens", 0), "completion_tokens": u.get("output_tokens", 0),
                          "prompt_tokens_details": u.get("input_tokens_details"),
                          "completion_tokens_details": u.get("output_tokens_details")}}

    def _forward(self, method, body=None):
        responses = False
        if body is not None and self.path.endswith("/chat/completions"):
            req = json.loads(body)
            if str(req.get("model", "")).startswith("gpt-"):
                responses, body = True, json.dumps(self._to_responses(req)).encode()
            elif req.get("model") in NO_RESPONSE_FORMAT and "response_format" in req:
                # These answer 400 to response_format; they follow the prompt's JSON instruction instead.
                req.pop("response_format")
                body = json.dumps(req).encode()
        headers = {"Authorization": "Bearer " + KEY, "User-Agent": "bindery-ra2-bot-strategist/1.0",
                   "x-opencode-session": SESSION, "Content-Type": "application/json"}
        path = "/responses" if responses else self.path.removeprefix("/v1")
        req = urllib.request.Request(UPSTREAM + path, body, headers, method=method)
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                code, data = r.status, r.read()
        except urllib.error.HTTPError as e:
            code, data = e.code, e.read()
        except Exception as e:  # network failure: the arena records a failed proposal
            code, data = 502, json.dumps({"error": {"message": str(e)[:300]}}).encode()
        if responses and code == 200:
            data = json.dumps(self._from_responses(json.loads(data), json.loads(body)["model"])).encode()
        if body is not None:
            try:
                usage = json.loads(data).get("usage")
            except Exception:
                usage = None
            with LOCK, open(LOG, "a") as f:
                f.write(json.dumps({"t": t0, "sec": round(time.time() - t0, 2), "status": code,
                                    "model": json.loads(body).get("model"), "usage": usage,
                                    "error": None if code == 200 else data[:300].decode("utf-8", "replace")}) + "\n")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._forward("GET")

    def do_POST(self):
        self._forward("POST", self.rfile.read(int(self.headers["Content-Length"])))


print(f"ocgo_proxy on 127.0.0.1:{PORT} session {SESSION}", flush=True)
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
