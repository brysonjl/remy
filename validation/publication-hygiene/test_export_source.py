"""Publication snapshot boundaries exercised against actual committed Git trees."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('source_export', Path(__file__).with_name('export-source.py'))
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


class SourceExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.root = self.directory / 'source'
        self.root.mkdir()
        self.git('init', '-q')
        self.write('app/source.swift', b'public synthetic source\n')
        self.write('HANDOFF.md', b'private internal notes\n')

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], stderr=subprocess.PIPE)

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def commit(self):
        self.git('add', '.')
        self.git('-c', 'user.name=Private Fixture Author', '-c', 'user.email=fixture@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'private fixture commit message')
        return self.git('rev-parse', 'HEAD').decode().strip()

    def reject(self, commit):
        output = self.directory / 'rejected.tar'
        with self.assertRaises(ValueError):
            exporter.export(self.root, commit, output)
        self.assertFalse(output.exists())

    def test_deterministic_committed_content_modes_links_and_no_history(self):
        script = self.write('run.sh', b'#!/bin/sh\nexit 0\n')
        script.chmod(0o755)
        link = self.root / 'validation/source.swift'
        link.parent.mkdir()
        link.symlink_to('../app/source.swift')
        commit = self.commit()
        self.write('app/source.swift', b'uncommitted must not appear\n')
        self.write('untracked.txt', b'private untracked bytes\n')
        first, second = self.directory / 'first.tar', self.directory / 'second.tar'
        report = exporter.export(self.root, commit, first)
        exporter.export(self.root, commit, second)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(report['source_commit'], commit)
        with tarfile.open(first) as archive:
            names = archive.getnames()
            self.assertNotIn('HANDOFF.md', names)
            self.assertNotIn('untracked.txt', names)
            self.assertFalse(any('.git' in Path(name).parts for name in names))
            self.assertEqual(archive.extractfile('app/source.swift').read(), b'public synthetic source\n')
            self.assertEqual(archive.getmember('run.sh').mode, 0o755)
            self.assertTrue(archive.getmember('validation/source.swift').issym())
            self.assertEqual(archive.getmember('validation/source.swift').linkname, '../app/source.swift')
            self.assertTrue(all(info.uid == info.gid == info.mtime == 0 and not info.uname and not info.gname for info in archive.getmembers()))
            manifest = json.load(archive.extractfile('SOURCE_MANIFEST.json'))
            self.assertEqual(manifest['source_commit'], commit)
        self.assertNotIn(b'fixture@example.invalid', first.read_bytes())
        self.assertNotIn(b'private fixture commit message', first.read_bytes())
        self.assertEqual(next(row for row in report['files'] if row['path'] == 'app/source.swift')['sha256'],
                         hashlib.sha256(b'public synthetic source\n').hexdigest())

    def test_secrets_private_filenames_audio_and_machine_paths_rejected(self):
        cases = [('secret.txt', ('sk-' + 'S' * 35).encode()),
                 ('Signing.local.xcconfig', b'harmless contents'),
                 ('fixture.wav', b'RIFF synthetic'),
                 ('paths.md', b'/' + b'Users/' + b'fixture/private/file')]
        for name, data in cases:
            with self.subTest(name=name):
                path = self.write(name, data)
                self.reject(self.commit())
                path.unlink()

    def test_escaping_broken_and_cyclic_links_rejected(self):
        for target in ('../../outside', '/tmp/outside', 'missing', 'link'):
            with self.subTest(target=target):
                link = self.root / 'validation/link'
                link.parent.mkdir(exist_ok=True)
                link.symlink_to(target)
                self.reject(self.commit())
                link.unlink()

    def test_only_exact_reviewed_png_or_font_binary_bytes_allowed(self):
        image = b'\x89PNG\r\n\x1a\n\0synthetic fixture'
        path = self.write('image.png', image)
        self.reject(self.commit())
        policy = {'image.png': hashlib.sha256(image).hexdigest()}
        self.write(exporter.POLICY_PATH, json.dumps(policy).encode())
        commit = self.commit()
        exporter.export(self.root, commit, self.directory / 'allowed.tar')
        path.write_bytes(image + b'changed bytes')
        self.reject(self.commit())
        path.unlink()
        self.write('unknown.bin', b'\0unknown binary')
        self.reject(self.commit())

    def test_submodules_and_unsafe_tree_paths_rejected(self):
        commit = self.commit()
        self.git('update-index', '--add', '--cacheinfo', f'160000,{commit},vendor/submodule')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'synthetic submodule')
        self.reject(self.git('rev-parse', 'HEAD').decode().strip())
        self.assertFalse(exporter.safe_name('../escape'))
        self.assertFalse(exporter.safe_name('.git/config'))
        self.assertFalse(exporter.safe_name('.local/private'))
        self.assertFalse(exporter.safe_name('folder\\escape'))

    def test_existing_output_preserved_and_invalid_revision_creates_nothing(self):
        commit = self.commit()
        output = self.directory / 'existing.tar'
        output.write_bytes(b'keep existing')
        with self.assertRaisesRegex(ValueError, 'already exists'):
            exporter.export(self.root, commit, output)
        self.assertEqual(output.read_bytes(), b'keep existing')
        missing = self.directory / 'missing.tar'
        with self.assertRaisesRegex(ValueError, 'full lowercase commit'):
            exporter.export(self.root, 'HEAD', missing)
        self.assertFalse(missing.exists())


if __name__ == '__main__':
    unittest.main()
