#!/usr/bin/env node
// PreToolUse(Bash) hook: block git invocations that violate Orbit's git workflow.
// Enforces the CLAUDE.md "Git workflow" rules deterministically instead of relying
// on prose the model can drift past:
//   - "Branch protection on main. No direct pushes. Squash-merge only."
//   - "Never --no-verify, --no-gpg-sign, or force-push to main."
// Also covers the bypass aliases: `git commit -n` (= --no-verify) and a bare
// `git push` issued while HEAD is on main/master. Feature-branch pushes, new
// commits, and PRs are untouched. reset --hard and checkout -- are intentionally
// NOT blocked: CLAUDE.md allows them with judgment. Exits 0 silent (allow) or 2
// with stderr feedback (block). Any error exits 0 so the hook never wedges Bash.

import { readFileSync } from "node:fs"
import { execFileSync } from "node:child_process"

try {
  let input
  try {
    input = JSON.parse(readFileSync(0, "utf8"))
  } catch {
    process.exit(0)
  }

  const command = input?.tool_input?.command
  if (typeof command !== "string" || !/\bgit\b/.test(command)) process.exit(0)

  const rules = [
    {
      test: /(?<![\w-])--no-verify\b/,
      why: "--no-verify skips the pre-commit/pre-push hooks. Fix what the hook flags, then commit normally.",
    },
    {
      test: /(?<![\w-])--no-gpg-sign\b|commit\.gpgsign\s*=\s*false/,
      why: "This bypasses commit signing. Let the commit sign as configured.",
    },
    {
      test: /\bgit\s+(?:-C\s+\S+\s+|-c\s+\S+\s+|-\S+\s+)*push\b[^&|;\n]*[\s:/](?:main|master)(?=$|[\s:])/,
      why: "Direct or force push to main is forbidden (branch protection, squash-merge only). Open a PR.",
    },
    {
      test: /\bgit\s+(?:-C\s+\S+\s+|-c\s+\S+\s+)*commit\s+(?:-[A-Za-z]\s+)*-n(?=\s|$)/,
      why: "`-n` is git's short alias for --no-verify on commit; it skips the pre-commit hooks. Commit normally.",
    },
  ]

  const hit = rules.find((rule) => rule.test.test(command))
  if (hit) {
    process.stderr.write(
      `BLOCKED git command (Orbit git workflow):\n  ${command}\n\n${hit.why}\n` +
        `The user has prevented this. See the "Git workflow" section of CLAUDE.md.\n`,
    )
    process.exit(2)
  }

  // A bare push (no explicit main/master ref, so rule 3 did not fire) issued while
  // HEAD is on the protected branch still lands on main. For pushes with no explicit
  // refspec (0-1 positional args after `push` = pushes HEAD), resolve the target repo's
  // branch (an explicit `-C <path>`, else the hook cwd) and block if it is main/master.
  const segment = command.split(/[&|;\n]/).find((s) => /\bgit\b[\s\S]*\bpush\b/.test(s))
  if (segment) {
    const afterPush = segment.slice(segment.search(/\bpush\b/) + 4)
    const positional = afterPush.split(/\s+/).filter((token) => token && !token.startsWith("-"))
    if (positional.length <= 1) {
      const cMatch = /-C\s+("[^"]+"|'[^']+'|[^\s"']+)/.exec(segment)
      const dir = cMatch ? cMatch[1].replace(/^["']|["']$/g, "") : input?.cwd || process.cwd()
      try {
        const branch = execFileSync("git", ["-C", dir, "rev-parse", "--abbrev-ref", "HEAD"], {
          encoding: "utf8",
          stdio: ["ignore", "pipe", "ignore"],
        }).trim()
        if (/^(?:main|master)$/.test(branch)) {
          process.stderr.write(
            `BLOCKED git command (Orbit git workflow):\n  ${command}\n\n` +
              `HEAD is on '${branch}'. Pushing from the protected branch is forbidden — switch to a feature branch and open a PR.\n`,
          )
          process.exit(2)
        }
      } catch {
        // Can't determine the branch (no repo at that path, git error) — fail open.
      }
    }
  }

  process.exit(0)
} catch {
  process.exit(0)
}
