#!/usr/bin/env node

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
