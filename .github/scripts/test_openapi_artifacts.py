import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

import openapi_artifacts as guard

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / '.github/scripts/openapi_artifacts.py'


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = pathlib.Path(self.temp.name).resolve()
        self.catalog_file = guard.repository_path(guard.CATALOG, self.repo)
        self.rows = guard.catalog(guard.repository_path(guard.CATALOG, ROOT).read_bytes())
        for path in [guard.CATALOG, *(r['path'] for r in self.rows)]:
            target = guard.repository_path(path, self.repo)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(guard.repository_path(path, ROOT), target)
        cache = self.repo / 'src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache'
        cache.parent.mkdir(parents=True)
        cache.write_text('\n'.join(str(guard.artifact_path(r, self.repo)) for r in self.rows))
        self.git('init', '-q')
        self.git('config', 'gc.auto', '0')
        self.git('config', 'maintenance.auto', 'false')
        self.git('config', 'user.name', 'Contract guard')
        self.git('config', 'user.email', 'contract@example.com')
        self.commit()
        self.base = self.git('rev-parse', 'HEAD').stdout.strip()
        self.git('commit', '--allow-empty', '-qm', 'Candidate')

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.repo, text=True, capture_output=True, check=True)

    def commit(self):
        self.git('add', guard.CATALOG, *(r['path'] for r in self.rows))
        self.git('commit', '-qm', 'Published artifacts')

    def run_guard(self, command='plan', base=None):
        args = ['python3', str(SCRIPT), command]
        if command == 'plan':
            args += ['--base', base or self.base]
        return subprocess.run(args, cwd=self.repo, text=True, capture_output=True)

    def write_catalog(self):
        self.catalog_file.write_text(json.dumps(self.rows))

    def test_all_published_streams_use_their_actual_target_artifacts(self):
        result = self.run_guard()
        self.assertEqual(0, result.returncode, result.stderr)
        matrix = json.loads(result.stdout)['include']
        self.assertEqual(3, len(matrix))
        for row in matrix:
            self.assertEqual(self.base + ':' + row['revision'], row['base'])
            self.assertNotEqual(row['base'], row['revision'])

    def test_only_genuinely_absent_streams_are_first_publications(self):
        self.git('checkout', '-q', self.base)
        self.git('rm', '-q', guard.CATALOG, *(r['path'] for r in self.rows if r['revision'] == 'r2'))
        self.git('commit', '-qm', 'Legacy target')
        base = self.git('rev-parse', 'HEAD').stdout.strip()
        self.git('checkout', '-q', self.base)
        result = self.run_guard(base=base)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(['orbit-http-v2-r1'], [r['id'] for r in json.loads(result.stdout)['include']])
        self.assertEqual(2, result.stderr.count('First publication'))

    def test_deleting_any_published_stream_fails(self):
        for row in self.rows:
            with self.subTest(row=row['id']):
                self.catalog_file.write_text(json.dumps([r for r in self.rows if r != row]))
                self.assertNotEqual(0, self.run_guard().returncode)
                self.write_catalog()

    def test_renaming_a_stream_cannot_become_first_publication(self):
        row = self.rows[-1]
        source = guard.artifact_path(row, self.repo)
        row.update(id='orbit-http-v2-r3', revision='r3', documentName='v2-r3', path='src/Orbit.Api/openapi_v2-r3.json')
        source.rename(guard.artifact_path(row, self.repo))
        self.rows[1]['replacement'] = row['id']
        self.write_catalog()
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_missing_target_artifact_cannot_become_first_publication(self):
        self.git('rm', '-q', self.rows[-1]['path'])
        self.git('commit', '-qm', 'Incomplete target')
        base = self.git('rev-parse', 'HEAD').stdout.strip()
        self.git('checkout', '-q', self.base)
        self.assertNotEqual(0, self.run_guard(base=base).returncode)

    def test_candidate_is_never_its_own_baseline(self):
        self.assertNotEqual(0, self.run_guard(base='HEAD').returncode)

    def test_catalog_rejects_wrong_version_path_identity_and_shape(self):
        for key, value in [('httpVersion', '2.0'), ('path', '../openapi.json'), ('documentName', 'v2'), ('id', 'unknown'), ('revision', 'r3'), ('deprecated', 'false')]:
            with self.subTest(key=key):
                old = self.rows[2][key]
                self.rows[2][key] = value
                self.write_catalog()
                self.assertNotEqual(0, self.run_guard().returncode)
                self.rows[2][key] = old
        self.rows[2]['unexpected'] = True
        self.write_catalog()
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_freshness_rejects_every_missing_stale_and_uncommitted_artifact(self):
        for row in self.rows:
            path = guard.artifact_path(row, self.repo)
            original = path.read_bytes()
            with self.subTest(row=row['id'], mode='stale'):
                path.write_bytes(original + b'\n')
                self.assertNotEqual(0, self.run_guard('freshness').returncode)
            with self.subTest(row=row['id'], mode='missing'):
                path.unlink()
                self.assertNotEqual(0, self.run_guard('freshness').returncode)
            path.write_bytes(original)
        path = self.rows[-1]['path']
        self.git('rm', '--cached', '-q', path)
        self.git('commit', '-qm', 'Uncommitted output')
        self.assertNotEqual(0, self.run_guard('freshness').returncode)

    def test_freshness_accepts_complete_deterministic_output(self):
        result = self.run_guard('freshness')
        self.assertEqual(0, result.returncode, result.stderr)

    def test_temporary_repository_commits_do_not_launch_automatic_housekeeping(self):
        trace = self.repo / 'git-trace.json'
        with mock.patch.dict(os.environ, {'GIT_TRACE2_EVENT': str(trace)}):
            self.git('commit', '--allow-empty', '-qm', 'Housekeeping isolation')
        children = [event['argv'] for line in trace.read_text().splitlines()
                    if (event := json.loads(line))['event'] == 'child_start']
        housekeeping = [args for args in children if args[1:2] in (['maintenance'], ['gc'])]
        self.assertEqual([], housekeeping)

    def test_generator_cannot_silently_deregister_an_existing_stream(self):
        cache = self.repo / 'src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache'
        cache.write_text(str(guard.artifact_path(self.rows[0], self.repo)))
        self.assertNotEqual(0, self.run_guard('freshness').returncode)

    def test_wrong_document_version_fails(self):
        path = self.repo / 'src/Orbit.Api/openapi_v2-r2.json'
        doc = json.loads(path.read_bytes())
        doc['info']['version'] = '1.0'
        path.write_text(json.dumps(doc))
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_additive_endpoint_changes_remain_eligible_for_ordinary_diff(self):
        path = self.repo / 'src/Orbit.Api/openapi.json'
        doc = json.loads(path.read_bytes())
        doc['paths']['/api/Additional'] = {'get': {'responses': {'200': {'description': 'OK'}}}}
        path.write_text(json.dumps(doc))
        self.assertEqual(0, self.run_guard().returncode)

    def test_artifact_symlinks_cannot_escape_the_repository(self):
        path = self.repo / 'src/Orbit.Api/openapi_v2-r2.json'
        original = path.read_bytes()
        with tempfile.TemporaryDirectory() as outside:
            external = pathlib.Path(outside) / 'external.json'
            external.write_bytes(original)
            path.unlink()
            path.symlink_to(external)
            for command in ('plan', 'freshness', 'evidence'):
                with self.subTest(command=command):
                    result = self.run_guard(command)
                    self.assertNotEqual(0, result.returncode, result.stdout)
                    self.assertIn('outside the repository', result.stderr)

    def test_catalog_symlinks_cannot_escape_the_repository(self):
        path = self.repo / guard.CATALOG
        original = path.read_bytes()
        with tempfile.TemporaryDirectory() as outside:
            external = pathlib.Path(outside) / 'catalog.json'
            external.write_bytes(original)
            path.unlink()
            path.symlink_to(external)
            for command in ('plan', 'freshness', 'evidence'):
                with self.subTest(command=command):
                    result = self.run_guard(command)
                    self.assertNotEqual(0, result.returncode, result.stdout)
                    self.assertIn('outside the repository', result.stderr)

    def test_generator_cache_cannot_substitute_uncataloged_path_aliases(self):
        cache = self.repo / 'src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache'
        with tempfile.TemporaryDirectory() as outside:
            alias = pathlib.Path(outside) / 'alias.json'
            target = self.repo / 'src/Orbit.Api/openapi.json'
            alias.symlink_to(target)
            cache.write_text(cache.read_text().replace(str(target), str(alias)))
            result = self.run_guard('freshness')
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertIn('Generator output', result.stderr)

    def test_invalid_target_refs_are_rejected_before_invoking_git(self):
        for ref in ('--help', '-c core.pager=anything', 'HEAD^{commit}', 'HEAD:src/Orbit.Api/openapi.json',
                    'origin/main\nHEAD', 'origin/ma\u0131n', 'https://example.com/main'):
            with (self.subTest(ref=ref),
                  mock.patch.object(guard.subprocess, 'check_output',
                                    side_effect=AssertionError('Untrusted ref reached git')),
                  self.assertRaisesRegex(ValueError, 'Invalid target ref')):
                guard.plan(ref)

    def test_catalog_identity_digits_remain_ascii(self):
        for revision in ('2\u0663', '2\uff13'):
            row = self.rows[-1].copy()
            row.update(id='orbit-http-v2-r' + revision, revision='r' + revision,
                       documentName='v2-r' + revision, path='src/Orbit.Api/openapi_v2-r' + revision + '.json')
            raw = json.dumps([*self.rows[:-1], row])
            with self.subTest(revision=revision), self.assertRaises(ValueError):
                guard.catalog(raw)


if __name__ == '__main__':
    unittest.main()
