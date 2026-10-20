import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys

CATALOG = 'src/Orbit.Api/OpenApi/artifacts.json'
FULL_COMMIT_PATTERN = r'[a-f\d]{40}'
IDENTITY = ('id', 'httpVersion', 'revision', 'documentName', 'path')
KEYS = set(IDENTITY) | {'deprecated', 'replacement'}
LEGACY = [
    {'id': f'orbit-http-v{v}-r1', 'httpVersion': f'{v}.0', 'revision': 'r1',
     'documentName': f'v{v}', 'path': 'src/Orbit.Api/openapi' + ('' if v == 1 else '_v2') + '.json',
     'deprecated': False, 'replacement': None}
    for v in (1, 2)
]
ENUMS = ('FrequencyUnit', 'DayOfWeek', 'InstanceStatus', 'ScheduledReminderWhen')


def resolve_commit(ref):
    if not re.fullmatch(r'[A-Za-z\d][A-Za-z\d/._-]*', ref, re.ASCII):
        raise ValueError('Invalid target ref')
    commit = subprocess.check_output(['git', 'rev-parse', '--verify', '--end-of-options', ref + '^{commit}']).decode().strip()
    if not re.fullmatch(FULL_COMMIT_PATTERN, commit, re.ASCII):
        raise ValueError('Git must return a full producer commit')
    return commit


def tracked_files(commit):
    if not re.fullmatch(FULL_COMMIT_PATTERN, commit, re.ASCII):
        raise ValueError('A full commit is required to list target files')
    return set(subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', '--end-of-options', commit]).decode().splitlines())


def committed_file(commit, path):
    if not re.fullmatch(FULL_COMMIT_PATTERN, commit, re.ASCII):
        raise ValueError('A full commit is required to read target files')
    if path != CATALOG and not re.fullmatch(r'src/Orbit\.Api/openapi(?:_v2|_v[12]-r[1-9]\d*)?\.json', path, re.ASCII):
        raise ValueError('Only publication files can be read from git')
    return subprocess.check_output(['git', 'show', '--end-of-options', commit + ':' + path])


def canonical_identity(value):
    match = re.fullmatch(r'orbit-http-v([12])-r([1-9]\d*)', value, re.ASCII)
    if not match:
        raise ValueError('Unknown artifact identity')
    version, revision = map(int, match.groups())
    name = f'v{version}' + ('' if revision == 1 else f'-r{revision}')
    path = 'src/Orbit.Api/openapi' + ('' if name == 'v1' else '_' + name) + '.json'
    return {'id': f'orbit-http-v{version}-r{revision}', 'httpVersion': f'{version}.0',
            'revision': f'r{revision}', 'documentName': name, 'path': path}


def validated_artifact(row):
    if not isinstance(row, dict) or set(row) != KEYS:
        raise ValueError('Artifact keys must match the catalog interface')
    if any(not isinstance(row[k], str) for k in IDENTITY):
        raise ValueError('Artifact identities must be strings')
    identity = canonical_identity(row['id'])
    if any(row[k] != identity[k] for k in IDENTITY):
        raise ValueError('Artifact version, revision, document name and path must agree')
    if type(row['deprecated']) is not bool or (row['replacement'] is not None and not isinstance(row['replacement'], str)):
        raise ValueError('Invalid artifact lifecycle metadata')
    return {**identity, 'deprecated': row['deprecated'], 'replacement': row['replacement']}


def validate_lifecycle(rows):
    for key in ('id', 'documentName', 'path'):
        if len({row[key] for row in rows}) != len(rows):
            raise ValueError('Duplicate artifact ' + key)
    indexed = {row['id']: row for row in rows}
    for row in rows:
        if row['replacement'] is None:
            continue
        other = indexed.get(row['replacement'])
        if not row['deprecated'] or other is None or other['httpVersion'] != row['httpVersion'] or other['id'] == row['id'] or other['deprecated']:
            raise ValueError('A deprecated artifact must name a current replacement for the same HTTP version')
    if any(legacy['id'] not in indexed for legacy in LEGACY):
        raise ValueError('Published legacy streams cannot be removed')


def catalog(raw):
    rows = json.loads(raw)
    if not isinstance(rows, list) or not rows:
        raise ValueError('The artifact catalog must be a nonempty array')
    rows = [validated_artifact(row) for row in rows]
    validate_lifecycle(rows)
    return rows


def repository_path(relative, root=None):
    repository = (root or pathlib.Path.cwd()).resolve()
    path = (repository / relative).resolve()
    if not path.is_relative_to(repository):
        raise ValueError('Publication file is outside the repository')
    return path


def artifact_path(row, root=None):
    return repository_path(validated_artifact(row)['path'], root)


def artifact_paths(paths):
    return {p for p in paths if re.fullmatch(r'src/Orbit\.Api/openapi[^/]*\.json', p)}


def check_document(row, raw):
    doc = json.loads(raw)
    if doc['openapi'] != '3.1.1' or doc['info']['version'] != row['httpVersion']:
        raise ValueError('Wrong OpenAPI or HTTP version: ' + row['id'])
    if doc['info']['title'] != 'Orbit.Api | ' + row['documentName'] or not doc['paths']:
        raise ValueError('Wrong or empty document: ' + row['id'])
    if row['revision'] != 'r1' and not doc['info'].get('description', '').startswith('Documentation revision ' + row['id'] + ' '):
        raise ValueError('Wrong document revision: ' + row['id'])


def freshness():
    rows = catalog(repository_path(CATALOG).read_bytes())
    head = resolve_commit('HEAD')
    tracked = tracked_files(head)
    expected = {r['path'] for r in rows}
    actual = {str(p) for p in pathlib.Path('src/Orbit.Api').glob('openapi*.json')}
    if actual != expected or artifact_paths(tracked) != expected:
        raise ValueError('Every generated artifact must be cataloged and committed')
    cache = repository_path('src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache')
    generated = set(cache.read_text().splitlines())
    if generated != {str(artifact_path(row)) for row in rows}:
        raise ValueError('Generator output must include every supported artifact')
    for path in [CATALOG, *sorted(expected)]:
        if repository_path(path).read_bytes() != committed_file(head, path):
            raise ValueError('Stale committed artifact: ' + path)
    for row in rows:
        check_document(row, artifact_path(row).read_bytes())
    print('All supported OpenAPI artifacts match committed generation')


def validate_target_inventory(previous, rows, files):
    current = {r['id']: r for r in rows}
    if artifact_paths(files) != {r['path'] for r in previous}:
        raise ValueError('The target artifact inventory is incomplete')
    for row in previous:
        identity = row['id']
        if identity not in current or any(row[k] != current[identity][k] for k in IDENTITY):
            raise ValueError('Published stream removed, renamed or substituted: ' + identity)
        if row['path'] not in files:
            raise ValueError('Published target artifact is missing: ' + identity)


def plan(base):
    base_commit = resolve_commit(base)
    if base_commit == resolve_commit('HEAD'):
        raise ValueError('A compatibility baseline must be the target commit, not the candidate')
    files = tracked_files(base_commit)
    previous = catalog(committed_file(base_commit, CATALOG)) if CATALOG in files else LEGACY
    rows = catalog(repository_path(CATALOG).read_bytes())
    validate_target_inventory(previous, rows, files)
    old = {r['id'] for r in previous}
    matrix = []
    for row in rows:
        check_document(row, artifact_path(row).read_bytes())
        if row['id'] not in old:
            if row['path'] in files:
                raise ValueError('First publication cannot substitute an existing artifact')
            print('First publication, no prior same-stream artifact: ' + row['id'], file=sys.stderr)
        elif row['id'] != 'orbit-http-v1-r1':
            matrix.append({'id': row['id'], 'base': base_commit + ':' + row['path'], 'revision': row['path']})
    return {'include': matrix}


def enum_references(value, location=''):
    owners = {}
    if isinstance(value, dict):
        for key, item in value.items():
            if key == '$ref' and any(item.endswith('/' + e) or item.endswith('/' + e + 'Input') for e in ENUMS):
                owners[location] = item
            else:
                owners.update(enum_references(item, location + '/' + key))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            owners.update(enum_references(item, location + '/' + str(index)))
    return owners


def document_evidence(row):
    raw = artifact_path(row).read_bytes()
    doc = json.loads(raw)
    selected = {k: v for k, v in doc['components']['schemas'].items()
                if k.removesuffix('Input') in (*ENUMS, 'ScheduledReminderTime', 'RelativeReminderTime')}
    types = {str: 'string', dict: 'object', list: 'array', int: 'integer', bool: 'boolean', type(None): 'null'}
    return {'sha256': hashlib.sha256(raw).hexdigest(),
            'rootKeys': {k: types[type(v)] for k, v in doc.items()}, 'info': doc['info'],
            'pathCount': len(doc['paths']), 'operationCount': sum(len(path) for path in doc['paths'].values()),
            'components': selected, 'enumReferences': enum_references(doc)}


def evidence():
    rows = catalog(repository_path(CATALOG).read_bytes())
    return {'catalog': rows, 'documents': {row['id']: document_evidence(row) for row in rows}}


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
