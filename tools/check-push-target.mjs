#!/usr/bin/env node
/**
 * Pre-push guard: reject a push whose remote ref is a protected branch.
 *
 * Reads git's pre-push stdin format, one line per ref being pushed:
 *   <local ref> <local sha> <remote ref> <remote sha>
 *
 * Lives in a script rather than an inline lefthook `run:` block because a
 * multi-line shell script in that field fails to parse on Windows
 * ("syntax error: unexpected end of file"), which made the guard reject
 * every push instead of only the protected ones.
 */

const PROTECTED = new Set(['refs/heads/main', 'refs/heads/master']);

const stdin = await new Promise((resolve) => {
  let buffer = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => (buffer += chunk));
  process.stdin.on('end', () => resolve(buffer));
});

const blocked = stdin
  .split('\n')
  .map((line) => line.trim().split(/\s+/)[2])
  .filter((remoteRef) => remoteRef && PROTECTED.has(remoteRef));

if (blocked.length > 0) {
  console.error(
    `BLOCKED: pushing to ${blocked.join(', ')} is forbidden (branch protection, squash-merge only). Open a PR.`,
  );
  process.exit(1);
}
