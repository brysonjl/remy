"""Check committed-source isolation without Xcode, networking or credentials."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('fresh_clone', Path(__file__).with_name('fresh-clone.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FreshCloneTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source with spaces'
        self.source.mkdir()
        subprocess.run(['git', 'init', '-q', str(self.source)], check=True)
        (self.source / '.gitignore').write_text('private/\n')
        (self.source / 'tracked.txt').write_text('committed fixture')
        self.git('add', '.')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture')

    def git(self, *arguments):
        return module.git(self.source, *arguments)

    def test_only_committed_inputs_copied_without_shared_objects(self):
        (self.source / 'tracked.txt').write_text('uncommitted change')
        (self.source / 'untracked.txt').write_text('untracked fixture')
        (self.source / 'private').mkdir()
        (self.source / 'private/ignored.txt').write_text('ignored fixture')
        clone, report = module.prepare(self.source, destination=self.root / 'new clone')
        self.assertEqual((clone / 'tracked.txt').read_text(), 'committed fixture')
        self.assertFalse((clone / 'untracked.txt').exists())
        self.assertFalse((clone / 'private').exists())
        self.assertFalse((clone / '.git/objects/info/alternates').exists())
        self.assertTrue(report['source_worktree_dirty'])
        self.assertEqual(report['source_commit'], self.git('rev-parse', 'HEAD'))
        self.assertEqual(report['full_validation'], 'not_run')

    def test_existing_destination_never_replaced(self):
        destination = self.root / 'existing'
        destination.mkdir()
        sentinel = destination / 'keep'
        sentinel.write_text('fixture')
        with self.assertRaisesRegex(ValueError, 'already exist'):
            module.prepare(self.source, destination=destination)
        self.assertEqual(sentinel.read_text(), 'fixture')


if __name__ == '__main__':
    unittest.main()
