import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys

CATALOG = 'src/Orbit.Api/OpenApi/artifacts.json'
IDENTITY = ('id', 'httpVersion', 'revision', 'documentName', 'path')
KEYS = set(IDENTITY) | {'deprecated', 'replacement'}
LEGACY = [
    dict(id=f'orbit-http-v{v}-r1', httpVersion=f'{v}.0', revision='r1',
         documentName=f'v{v}', path='src/Orbit.Api/openapi' + ('' if v == 1 else '_v2') + '.json',
         deprecated=False, replacement=None)
    for v in (1, 2)
]


def git(*args):
    return subprocess.check_output(['git', *args])


def catalog(raw):
    rows = json.loads(raw)
    if not isinstance(rows, list) or not rows:
        raise ValueError('The artifact catalog must be a nonempty array')
    for row in rows:
        if not isinstance(row, dict) or set(row) != KEYS:
            raise ValueError('Artifact keys must match the catalog interface')
        if any(not isinstance(row[k], str) for k in IDENTITY):
            raise ValueError('Artifact identities must be strings')
        match = re.fullmatch(r'orbit-http-v([12])-r([1-9][0-9]*)', row['id'])
        if not match:
            raise ValueError('Unknown artifact identity')
        version, revision = match.groups()
        name = 'v' + version + ('' if revision == '1' else '-r' + revision)
        path = 'src/Orbit.Api/openapi' + ('' if name == 'v1' else '_' + name) + '.json'
        expected = (f'{version}.0', 'r' + revision, name, path)
        if tuple(row[k] for k in IDENTITY[1:]) != expected:
            raise ValueError('Artifact version, revision, document name and path must agree')
        if type(row['deprecated']) is not bool or (row['replacement'] is not None and not isinstance(row['replacement'], str)):
            raise ValueError('Invalid artifact lifecycle metadata')
    for key in ('id', 'documentName', 'path'):
        if len({row[key] for row in rows}) != len(rows):
            raise ValueError('Duplicate artifact ' + key)
    indexed = {row['id']: row for row in rows}
    for row in rows:
        if row['replacement'] is not None:
            other = indexed.get(row['replacement'])
            if not row['deprecated'] or other is None or other['httpVersion'] != row['httpVersion'] or other['id'] == row['id'] or other['deprecated']:
                raise ValueError('A deprecated artifact must name a current replacement for the same HTTP version')
    for legacy in LEGACY:
        if legacy['id'] not in indexed:
            raise ValueError('Published legacy streams cannot be removed')
    return rows


def artifact_paths(paths):
    return {p for p in paths if re.fullmatch(r'src/Orbit.Api/openapi[^/]*\.json', p)}


def check_document(row, raw):
    doc = json.loads(raw)
    if doc['openapi'] != '3.1.1' or doc['info']['version'] != row['httpVersion']:
        raise ValueError('Wrong OpenAPI or HTTP version: ' + row['id'])
    if doc['info']['title'] != 'Orbit.Api | ' + row['documentName'] or not doc['paths']:
        raise ValueError('Wrong or empty document: ' + row['id'])
    if row['revision'] != 'r1' and not doc['info'].get('description', '').startswith('Documentation revision ' + row['id'] + ' '):
        raise ValueError('Wrong document revision: ' + row['id'])


def freshness():
    rows = catalog(pathlib.Path(CATALOG).read_bytes())
    tracked = set(git('ls-tree', '-r', '--name-only', 'HEAD').decode().splitlines())
    expected = {r['path'] for r in rows}
    actual = {str(p) for p in pathlib.Path('src/Orbit.Api').glob('openapi*.json')}
    if actual != expected or artifact_paths(tracked) != expected:
        raise ValueError('Every generated artifact must be cataloged and committed')
    cache = pathlib.Path('src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache')
    generated = {pathlib.Path(p).resolve() for p in cache.read_text().splitlines()}
    if generated != {pathlib.Path(p).resolve() for p in expected}:
        raise ValueError('Generator output must include every supported artifact')
    for path in [CATALOG, *sorted(expected)]:
        if pathlib.Path(path).read_bytes() != git('show', 'HEAD:' + path):
            raise ValueError('Stale committed artifact: ' + path)
    for row in rows:
        check_document(row, pathlib.Path(row['path']).read_bytes())
    print('All supported OpenAPI artifacts match committed generation')


def plan(base):
    base_commit = git('rev-parse', '--verify', base + '^{commit}').decode().strip()
    if base_commit == git('rev-parse', 'HEAD').decode().strip():
        raise ValueError('A compatibility baseline must be the target commit, not the candidate')
    files = set(git('ls-tree', '-r', '--name-only', base_commit).decode().splitlines())
    previous = catalog(git('show', base_commit + ':' + CATALOG)) if CATALOG in files else LEGACY
    rows = catalog(pathlib.Path(CATALOG).read_bytes())
    old = {r['id']: r for r in previous}
    current = {r['id']: r for r in rows}
    if artifact_paths(files) != {r['path'] for r in previous}:
        raise ValueError('The target artifact inventory is incomplete')
    for identity, row in old.items():
        if identity not in current or any(row[k] != current[identity][k] for k in IDENTITY):
            raise ValueError('Published stream removed, renamed or substituted: ' + identity)
        if row['path'] not in files:
            raise ValueError('Published target artifact is missing: ' + identity)
    matrix = []
    for row in rows:
        check_document(row, pathlib.Path(row['path']).read_bytes())
        if row['id'] not in old:
            if row['path'] in files:
                raise ValueError('First publication cannot substitute an existing artifact')
            print('First publication, no prior same-stream artifact: ' + row['id'], file=sys.stderr)
        elif row['id'] != 'orbit-http-v1-r1':
            matrix.append(dict(id=row['id'], base=base_commit + ':' + row['path'], revision=row['path']))
    return {'include': matrix}


def evidence():
    rows = catalog(pathlib.Path(CATALOG).read_bytes())
    documents = {}
    enums = ('FrequencyUnit', 'DayOfWeek', 'InstanceStatus', 'ScheduledReminderWhen')
    for row in rows:
        doc = json.loads(pathlib.Path(row['path']).read_bytes())
        schemas = doc['components']['schemas']
        selected = {k: v for k, v in schemas.items() if k in enums or k.removesuffix('Input') in enums
                    or k.removesuffix('Input') in ('ScheduledReminderTime', 'RelativeReminderTime')}
        owners = {}
        def references(value, location):
            if isinstance(value, dict):
                for key, item in value.items():
                    if key == '$ref' and any(item.endswith('/' + e) or item.endswith('/' + e + 'Input') for e in enums):
                        owners[location] = item
                    else:
                        references(item, location + '/' + key)
            elif isinstance(value, list):
                for index, item in enumerate(value):
                    references(item, location + '/' + str(index))
        references(doc, '')
        documents[row['id']] = dict(
            sha256=hashlib.sha256(pathlib.Path(row['path']).read_bytes()).hexdigest(),
            rootKeys={k: {str: "string", dict: "object", list: "array", int: "integer", bool: "boolean", type(None): "null"}[type(v)] for k, v in doc.items()}, info=doc['info'],
            pathCount=len(doc['paths']),
            operationCount=sum(len(path) for path in doc['paths'].values()),
            components=selected, enumReferences=owners)
    return dict(catalog=rows, documents=documents)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['freshness', 'plan', 'evidence'])
    parser.add_argument('--base')
    args = parser.parse_args()
    try:
        if args.command == 'freshness':
            freshness()
        elif args.command == 'evidence':
            print(json.dumps(evidence(), indent=2))
        else:
            if not args.base:
                parser.error('plan requires --base for the actual target ref')
            print(json.dumps(plan(args.base), separators=(',', ':')))
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))


if __name__ == '__main__':
    main()
