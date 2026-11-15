"""Release gates must reject stale/tampered candidates before obtaining credentials."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'eng'))
import release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.packages = self.root / 'packages'
        self.packages.mkdir()
        self.version = '0.1.0-preview.1'
        self.package = self.packages / f'NativeWeave.HelloWorld.{self.version}.nupkg'
        self.metadata = {
            'packageId': 'NativeWeave.HelloWorld', 'packageVersion': self.version,
            'rid': 'linux-x64', 'sourceDirty': False, 'sourceCommit': 'a' * 40,
            'nativePath': 'runtimes/linux-x64/native/libnativeweave_hello.so',
            'nativeSha256': hashlib.sha256(b'native fixture').hexdigest(),
        }
        self.write_candidate()
        self.artifacts_patch = patch.object(release, 'ARTIFACTS', self.root)
        self.artifacts_patch.start()
        self.addCleanup(self.artifacts_patch.stop)
        self.commit_patch = patch.object(release.subprocess, 'check_output', return_value='a' * 40 + '\n')
        self.commit_patch.start()
        self.addCleanup(self.commit_patch.stop)

    def write_candidate(self):
        with zipfile.ZipFile(self.package, 'w') as archive:
            archive.writestr('nativeweave/manifest.json', json.dumps(self.metadata))
            archive.writestr(self.metadata['nativePath'], b'native fixture')
        (self.packages / 'manifest.json').write_text(json.dumps(self.metadata))
        self.write_checksums()

    def write_checksums(self):
        (self.packages / 'SHA256SUMS').write_text(''.join(
            f'{release.digest(file)}  {file.name}\n'
            for file in [self.package, self.packages / 'manifest.json']))

    def test_clean_candidate_matches_commit_and_payload(self):
        self.assertEqual(self.package, release.validate_candidate(self.version))

    def test_archive_tamper_fails(self):
        self.package.write_bytes(self.package.read_bytes() + b'changed')
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            release.validate_candidate(self.version)

    def test_dirty_or_wrong_commit_is_not_releasable(self):
        for field, value in [('sourceDirty', True), ('sourceCommit', 'b' * 40)]:
            with self.subTest(field=field):
                old = self.metadata[field]
                self.metadata[field] = value
                self.write_candidate()
                with self.assertRaisesRegex(ValueError, 'clean candidate'):
                    release.validate_candidate(self.version)
                self.metadata[field] = old

    def test_wrong_rid_or_embedded_native_hash_fails(self):
        self.metadata['rid'] = 'osx-arm64'
        self.write_candidate()
        with self.assertRaisesRegex(ValueError, 'platform'):
            release.validate_candidate(self.version)
        self.metadata['rid'] = 'linux-x64'
        self.metadata['nativeSha256'] = '0' * 64
        self.write_candidate()
        with self.assertRaisesRegex(ValueError, 'Native payload'):
            release.validate_candidate(self.version)

    def test_checksum_paths_are_not_arbitrary_files(self):
        with (self.packages / 'SHA256SUMS').open('a') as stream:
            stream.write('0' * 64 + '  ../outside\n')
        with self.assertRaisesRegex(ValueError, 'checksum entry'):
            release.validate_candidate(self.version)

    def test_versions_are_canonical(self):
        for version in ['0.1.0-preview.1', '1.2.3']:
            self.assertEqual(version, release.validate_version(version))
        for version in ['v1.2.3', '1.2', '1.2.3+metadata', '1.2.3\nversion=evil', '1.2.3-01', '../outside']:
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.validate_version(version)

    def test_registry_payload_comparison_ignores_only_signature(self):
        downloaded = self.root / 'registry.nupkg'
        with zipfile.ZipFile(downloaded, 'w') as archive, zipfile.ZipFile(self.package) as original:
            for name in original.namelist():
                archive.writestr(name, original.read(name))
            archive.writestr('.signature.p7s', b'signature fixture, verified separately in publication')
        self.assertEqual(release.entries(self.package), release.entries(downloaded))
        with zipfile.ZipFile(downloaded, 'a') as archive:
            archive.writestr('unexpected.txt', b'not allowed')
        self.assertNotEqual(release.entries(self.package), release.entries(downloaded))

    def test_upload_evidence_survives_indexing_timeout(self):
        with patch.dict(release.os.environ, {'NUGET_API_KEY': 'test-only'}), \
                patch.object(release.subprocess, 'run') as command, \
                patch.object(release, 'download_registry_package', side_effect=TimeoutError('indexing')), \
                patch.object(release, 'consume') as consume:
            with self.assertRaises(TimeoutError):
                release.publish(self.version)
        self.assertEqual(command.call_count, 1)
        self.assertNotIn('test-only', str(command.call_args))
        consume.assert_not_called()
        result = json.loads((self.root / 'publication.json').read_text())
        self.assertEqual(result['status'], 'uploaded-awaiting-verification')
        self.assertEqual(result['version'], self.version)
        self.assertEqual(result['uploadSha256'], release.digest(self.package))


class VersionSelectionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix></PropertyGroup></Project>')
        root_patch = patch.object(release, 'ROOT', root)
        root_patch.start()
        self.addCleanup(root_patch.stop)
        self.event_file = root / 'pull-requests.json'
        self.pr = {'merged_at': '2026-11-15T04:22:16Z', 'merge_commit_sha': 'a' * 40,
                   'base': {'ref': 'main', 'repo': {'full_name': 'IzuSharp/NativeWeave'}}}
        self.event_file.write_text(json.dumps([[self.pr]]))
        self.env = {'GITHUB_EVENT_NAME': 'push', 'GITHUB_REF': 'refs/heads/main',
                    'GITHUB_SHA': 'a' * 40, 'GITHUB_REPOSITORY': 'IzuSharp/NativeWeave',
                    'MERGE_CANDIDATES': str(self.event_file),
                    'GITHUB_RUN_NUMBER': '148', 'GITHUB_RUN_ATTEMPT': '1'}

    def test_main_merge_increments_run_and_full_rerun_increments_attempt(self):
        self.assertEqual(release.select_version(self.env, '9.0.0'), '0.1.0-preview.148.1')
        self.env['GITHUB_RUN_NUMBER'] = '149'
        self.assertEqual(release.select_version(self.env), '0.1.0-preview.149.1')
        self.env['GITHUB_RUN_ATTEMPT'] = '2'
        self.assertEqual(release.select_version(self.env), '0.1.0-preview.149.2')

    def test_dispatch_can_allocate_or_use_an_explicit_version(self):
        self.env['GITHUB_EVENT_NAME'] = 'workflow_dispatch'
        self.assertEqual(release.select_version(self.env), '0.1.0-preview.148.1')
        self.assertEqual(release.select_version(self.env, '0.2.0-preview.1'), '0.2.0-preview.1')

    def test_version_tags_keep_their_identity_on_retries(self):
        self.env['GITHUB_EVENT_NAME'] = 'push'
        self.env['GITHUB_REF'] = 'refs/tags/v0.2.0-preview.3'
        self.assertEqual(release.select_version(self.env), '0.2.0-preview.3')
        self.env['GITHUB_RUN_ATTEMPT'] = '2'
        self.assertEqual(release.select_version(self.env), '0.2.0-preview.3')

    def test_branch_pushes_and_unmerged_pr_events_cannot_allocate_releases(self):
        for event, ref in [('push', 'refs/heads/codex/topic'),
                           ('pull_request', 'refs/pull/1/merge'),
                           ('pull_request_target', 'refs/heads/main'),
                           ('pull_request_target', 'refs/heads/develop'),
                           ('workflow_dispatch', 'refs/heads/codex/topic'),
                           ('workflow_dispatch', 'refs/tags/v0.2.0')]:
            with self.subTest(event=event, ref=ref), self.assertRaises(ValueError):
                release.select_version(dict(self.env, GITHUB_EVENT_NAME=event, GITHUB_REF=ref), '0.2.0')

    def test_direct_main_push_without_a_merged_pr_is_skipped(self):
        self.event_file.write_text(json.dumps([[]]))
        self.assertIsNone(release.select_version(self.env))

    def test_only_the_exact_merged_main_commit_qualifies(self):
        for field, value in [('merged_at', None), ('merge_commit_sha', 'b' * 40),
                             ('base', {'ref': 'develop', 'repo': {'full_name': 'IzuSharp/NativeWeave'}}),
                             ('base', {'ref': 'main', 'repo': {'full_name': 'someone/NativeWeave'}})]:
            self.event_file.write_text(json.dumps([[dict(self.pr, **{field: value})]]))
            with self.subTest(field=field, value=value):
                self.assertIsNone(release.select_version(self.env))

    def test_all_api_pages_are_considered(self):
        self.event_file.write_text(json.dumps([[dict(self.pr, merged_at=None)], [self.pr]]))
        self.assertEqual(release.select_version(self.env), '0.1.0-preview.148.1')

    def test_unavailable_merge_evidence_fails_instead_of_publishing(self):
        self.event_file.unlink()
        with self.assertRaises(OSError):
            release.select_version(self.env)

    def test_invalid_automatic_counters_are_rejected(self):
        for field in ['GITHUB_RUN_NUMBER', 'GITHUB_RUN_ATTEMPT']:
            for value in ['', '0', '-1', '01', '1.2', '1\n']:
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    release.select_version(dict(self.env, **{field: value}))

    def test_local_identity_still_validates_explicit_versions(self):
        self.assertEqual(release.select_version({}, '0.1.0-preview.1'), '0.1.0-preview.1')
        with self.assertRaises(ValueError):
            release.select_version({})


class RegistryPollingTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.download = Path(temporary.name) / 'download.nupkg'
        self.now = 0
        self.delays = []
        clock = patch.object(release.time, 'monotonic', side_effect=lambda: self.now)
        sleeper = patch.object(release.time, 'sleep', side_effect=self.advance)
        printer = patch('builtins.print')
        for mock in (clock, sleeper, printer):
            mock.start()
            self.addCleanup(mock.stop)

    def advance(self, seconds):
        self.delays.append(seconds)
        self.now += seconds

    def missing(self):
        return urllib.error.HTTPError('https://api.nuget.org/', 404, 'Not indexed', {}, None)

    def test_available_package_is_downloaded_immediately(self):
        with patch.object(release.urllib.request, 'urlopen', return_value=io.BytesIO(b'package')):
            release.download_registry_package('0.1.0-preview.148.1', self.download)
        self.assertEqual(self.delays, [])
        self.assertEqual(self.download.read_bytes(), b'package')

    def test_checks_are_two_minutes_apart_until_available(self):
        with patch.object(release.urllib.request, 'urlopen',
                          side_effect=[self.missing(), self.missing(), io.BytesIO(b'package')]) as request:
            release.download_registry_package('0.1.0-preview.148.1', self.download)
        self.assertEqual(self.delays, [120, 120])
        self.assertEqual(request.call_count, 3)
        self.assertEqual(self.download.read_bytes(), b'package')

    def test_indexing_timeout_is_bounded_and_explains_upload_already_happened(self):
        with patch.object(release.urllib.request, 'urlopen', side_effect=self.missing()) as request:
            with self.assertRaisesRegex(TimeoutError, 'was uploaded.*20 minutes'):
                release.download_registry_package('0.1.0-preview.148.1', self.download)
        self.assertEqual(self.now, 1200)
        self.assertEqual(self.delays, [120] * 10)
        self.assertEqual(request.call_count, 11)
        self.assertFalse(self.download.exists())

    def test_other_http_errors_are_not_hidden_as_indexing_delays(self):
        error = urllib.error.HTTPError('https://api.nuget.org/', 403, 'Forbidden', {}, None)
        with patch.object(release.urllib.request, 'urlopen', side_effect=error):
            with self.assertRaises(urllib.error.HTTPError):
                release.download_registry_package('0.1.0-preview.148.1', self.download)
        self.assertEqual(self.delays, [])
