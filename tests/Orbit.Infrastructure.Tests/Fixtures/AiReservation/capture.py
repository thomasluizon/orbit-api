"""Refresh provider evidence for captured production requests using OPENAI_API_KEY."""

import gzip
import json
import os
from pathlib import Path
import urllib.request

BASE_URL = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
HEADERS = {
    "Authorization": "Bearer " + os.environ["OPENAI_API_KEY"],
    "Content-Type": "application/json",
}
COUNT_FIELDS = {
    "model", "input", "tools", "text", "reasoning", "instructions",
    "tool_choice", "parallel_tool_calls", "truncation",
}


def post(route, body):
    request = urllib.request.Request(
        BASE_URL + route,
        data=json.dumps(body, ensure_ascii=False).encode(),
        headers=HEADERS,
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


for path in sorted(Path(__file__).parent.glob("*.json.gz")):
    fixture = json.loads(gzip.decompress(path.read_bytes()))
    request = fixture["request"]
    fixture["provider_response"] = post(
        "/responses/input_tokens",
        {key: value for key, value in request.items() if key in COUNT_FIELDS},
    )
    fixture["completion_usage"] = post(
        "/responses", {**request, "max_output_tokens": 128, "store": False}
    )["usage"]
    path.write_bytes(gzip.compress(
        json.dumps(fixture, ensure_ascii=False, separators=(",", ":")).encode(), mtime=0
    ))
    print(path.name, fixture["completion_usage"]["input_tokens"])
