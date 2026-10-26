"""Refresh the captured ranking probes using OPENAI_API_KEY and their recorded requests."""

import gzip
import json
import os
from pathlib import Path
import urllib.request

base_url = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
for path in sorted(Path(__file__).parent.glob("*.json.gz")):
    capture = json.loads(gzip.decompress(path.read_bytes()))
    request = urllib.request.Request(
        base_url + "/responses",
        data=json.dumps(capture["request"], ensure_ascii=False).encode(),
        headers={
            "Authorization": "Bearer " + os.environ["OPENAI_API_KEY"],
            "Content-Type": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        result = json.load(response)
    capture["response"] = {key: result[key] for key in capture["response"]}
    path.write_bytes(gzip.compress(
        json.dumps(capture, ensure_ascii=False, separators=(",", ":")).encode(), mtime=0
    ))
    print(path.name, capture["response"]["id"])
