import json
import pathlib
import shutil
import subprocess
import tempfile
import unittest

import openapi_artifacts as guard

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / '.github/scripts/openapi_artifacts.py'


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = pathlib.Path(self.temp.name)
        self.rows = json.loads((ROOT / guard.CATALOG).read_bytes())
        for path in [guard.CATALOG, *(r['path'] for r in self.rows)]:
            target = self.repo / path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / path, target)
        cache = self.repo / 'src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache'
        cache.parent.mkdir(parents=True)
        cache.write_text('\n'.join(str(self.repo / r['path']) for r in self.rows))
        self.git('init', '-q')
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
        (self.repo / guard.CATALOG).write_text(json.dumps(self.rows))

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
                original = (self.repo / guard.CATALOG).read_bytes()
                (self.repo / guard.CATALOG).write_text(json.dumps([r for r in self.rows if r != row]))
                self.assertNotEqual(0, self.run_guard().returncode)
                (self.repo / guard.CATALOG).write_bytes(original)

    def test_renaming_a_stream_cannot_become_first_publication(self):
        row = self.rows[-1]
        source = self.repo / row['path']
        row.update(id='orbit-http-v2-r3', revision='r3', documentName='v2-r3', path='src/Orbit.Api/openapi_v2-r3.json')
        source.rename(self.repo / row['path'])
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
            path = self.repo / row['path']
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
        self.assertEqual(0, self.run_guard('freshness').returncode)

    def test_generator_cannot_silently_deregister_an_existing_stream(self):
        cache = self.repo / 'src/Orbit.Api/obj/Orbit.Api.OpenApiFiles.cache'
        cache.write_text(str(self.repo / self.rows[0]['path']))
        self.assertNotEqual(0, self.run_guard('freshness').returncode)

    def test_wrong_document_version_fails(self):
        path = self.repo / self.rows[-1]['path']
        doc = json.loads(path.read_bytes())
        doc['info']['version'] = '1.0'
        path.write_text(json.dumps(doc))
        self.assertNotEqual(0, self.run_guard().returncode)

    def test_additive_endpoint_changes_remain_eligible_for_ordinary_diff(self):
        path = self.repo / self.rows[0]['path']
        doc = json.loads(path.read_bytes())
        doc['paths']['/api/Additional'] = {'get': {'responses': {'200': {'description': 'OK'}}}}
        path.write_text(json.dumps(doc))
        self.assertEqual(0, self.run_guard().returncode)


if __name__ == '__main__':
    unittest.main()
