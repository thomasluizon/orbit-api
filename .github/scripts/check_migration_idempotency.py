#!/usr/bin/env python3
"""Fail CI on non-idempotent raw index SQL in EF migrations.

EF applies migrations at startup on Render; a raw CREATE INDEX for an index that
already exists throws Postgres 42P07 and fails the deploy. Every raw CREATE INDEX
inside migrationBuilder.Sql(...) must carry IF NOT EXISTS, and every raw DROP
INDEX must carry IF EXISTS. The fluent migrationBuilder.CreateIndex(...) /
DropIndex(...) API is already idempotent-safe and is not flagged (its method
names carry no whitespace, so they never match the raw-SQL patterns).

Mirrors checkEfMigrationRawIndex in the orbit-ui-mobile consumer repo
(.claude/hooks/_lib/rules-source.mjs): only the balanced-paren body of each
migrationBuilder.Sql(...) call is scanned, split into ;-separated statements so
one statement's IF [NOT] EXISTS clause cannot mask a sibling that lacks its own.

    check_migration_idempotency.py <migration.cs> [<migration.cs> ...]
"""
import os
import re
import sys

SQL_CALL = re.compile(r"migrationBuilder\.Sql\s*\(")
CREATE_INDEX = re.compile(r"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b", re.IGNORECASE)
IF_NOT_EXISTS = re.compile(r"\bIF\s+NOT\s+EXISTS\b", re.IGNORECASE)
DROP_INDEX = re.compile(r"\bDROP\s+INDEX\b", re.IGNORECASE)
IF_EXISTS = re.compile(r"\bIF\s+EXISTS\b", re.IGNORECASE)


def find_violations(path: str, contents: str) -> list[str]:
    """Return one message per non-idempotent raw index statement in the file."""
    findings: list[str] = []
    for call in SQL_CALL.finditer(contents):
        index = call.end()
        depth = 1
        start = index
        while index < len(contents) and depth > 0:
            char = contents[index]
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
            index += 1
        sql = contents[start : index - 1]
        line_number = contents.count("\n", 0, call.start()) + 1
        for statement in sql.split(";"):
            if CREATE_INDEX.search(statement) and not IF_NOT_EXISTS.search(statement):
                findings.append(f"{path}:{line_number} raw CREATE INDEX without IF NOT EXISTS")
            if DROP_INDEX.search(statement) and not IF_EXISTS.search(statement):
                findings.append(f"{path}:{line_number} raw DROP INDEX without IF EXISTS")
    return findings


def _confine(candidate: str, base: str) -> str:
    """Resolve candidate and confirm it stays within base, rejecting path traversal."""
    resolved = os.path.realpath(candidate)
    if resolved != base and not resolved.startswith(base + os.sep):
        raise ValueError(f"Refusing path outside {base}: {candidate}")
    return resolved


def main() -> int:
    base = os.path.realpath(os.getcwd())
    violations: list[str] = []
    for path in sys.argv[1:]:
        try:
            safe_path = _confine(path, base)
        except ValueError as error:
            print(error, file=sys.stderr)
            continue
        try:
            with open(safe_path, encoding="utf-8") as handle:
                contents = handle.read()
        except (OSError, UnicodeDecodeError) as error:
            print(f"Skipping unreadable file {path}: {error}", file=sys.stderr)
            continue
        violations.extend(find_violations(path, contents))

    if violations:
        print("::error::Non-idempotent raw index SQL in EF migration(s). Use CREATE INDEX IF NOT EXISTS / DROP INDEX IF EXISTS so a startup re-apply cannot throw Postgres 42P07 and fail the Render deploy.")
        for violation in violations:
            print(violation)
        return 1

    print("OK: no non-idempotent raw index SQL in the checked migrations.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
