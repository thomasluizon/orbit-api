import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile


root = Path(__file__).resolve().parents[4]
paths = ["infra/onboarding.tf", "infra/cloudflare.tf"]
sources = {path: (root / path).read_text() for path in paths}
policy = sources[paths[0]].split("  onboarding_platform_values =", 1)[0]
domains = re.search(
    r'resource "cloudflare_turnstile_widget" "orbit" \{.*?domains\s*=\s*(\[.*?\])',
    sources[paths[1]],
    re.S,
).group(1)
policy = policy.replace("cloudflare_turnstile_widget.orbit.domains", "local.widget_domains")
with tempfile.TemporaryDirectory() as directory:
    Path(directory, "main.tf").write_text(policy + f"  widget_domains = {domains}\n}}\n")
    result = subprocess.run(
        ["terraform", f"-chdir={directory}", "console"],
        input="jsonencode(local.bot_protection_api_values)\n",
        text=True,
        capture_output=True,
        check=True,
    )
    values = json.loads(json.loads(result.stdout))

fixture = {
    "sourceHashes": {
        path: hashlib.sha256((root / path).read_bytes()).hexdigest().upper()
        for path in paths
    },
    "environments": values,
}
Path(__file__).with_name("TerraformHostnames.json").write_text(json.dumps(fixture, indent=2) + "\n")
