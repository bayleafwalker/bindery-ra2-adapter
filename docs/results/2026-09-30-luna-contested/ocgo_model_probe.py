import json,sys,time,urllib.request
MODELS=sys.argv[1:]
schema={"type":"object","additionalProperties":False,"required":["playbookId","posture","confidence","rationale"],
 "properties":{"playbookId":{"type":"string","enum":["allied-grizzly-timing","allied-boom","allied-prism-turtle","allied-ifv-mix","allied-harass","generic-expand"]},
 "posture":{"type":"string","enum":["Pressure","Defend","Expand"]},"confidence":{"type":"number"},"rationale":{"type":"string"}}}
sysmsg="You are the strategist of a Red Alert 2 bot playing Allied. Choose one playbook for the next minute. Answer only with JSON matching the schema."
user="Game time 240 s. Own army value 2100 (6 Grizzly, 4 IFV), credits 3400, 2 refineries. Enemy seen: 5 Rhino tanks, 2 Flak tracks near our expansion; enemy base scouted with a war factory and a battle lab. Enemy army estimate 2600."
for m in MODELS:
    for i in range(2):
        body={"model":m,"messages":[{"role":"system","content":sysmsg},{"role":"user","content":user}],
              "response_format":{"type":"json_schema","json_schema":{"name":"intent","strict":True,"schema":schema}},"max_tokens":2000}
        t=time.time();ok=False;err=""
        try:
            r=urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:8032/v1/chat/completions",json.dumps(body).encode(),{"Content-Type":"application/json"}),timeout=120)
            d=json.load(r);c=d["choices"][0]["message"]["content"]
            j=json.loads(c.strip().removeprefix("```json").removesuffix("```").strip());ok=j.get("playbookId") in schema["properties"]["playbookId"]["enum"];err=j.get("playbookId")
        except Exception as e: err=type(e).__name__+":"+str(e)[:80]
        print(f"{m:16} try{i} {time.time()-t:6.1f}s valid={ok} {err}",flush=True)
