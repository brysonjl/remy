"""Portable bundle-policy tests: no signing, Xcode, or executable launch."""
import importlib.util
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('release_app', Path(__file__).with_name('verify-release-app.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class BundlePolicyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.app = Path(self.tmp.name) / 'Fixture with spaces.app'
        executable = self.app / 'Contents/MacOS/Remy'
        executable.parent.mkdir(parents=True)
        executable.write_bytes(b'fixture')
        executable.chmod(0o755)
        self.info = dict(CFBundleIdentifier='io.github.brysonjl.remy', CFBundleExecutable='Remy',
                         CFBundleShortVersionString='0.1.0', CFBundleVersion='1',
                         SUEnableAutomaticChecks=False, SUAutomaticallyUpdate=False)
        self.write_info()
        self.file = patch.object(module.subprocess, 'check_output', side_effect=lambda args, **kwargs: 'Mach-O fixture' if Path(args[-1]).name != 'Info.plist' else 'XML plist').start()
        self.lipo = patch.object(module.subprocess, 'run').start()
        self.addCleanup(patch.stopall)

    def write_info(self):
        (self.app / 'Contents/Info.plist').write_bytes(plistlib.dumps(self.info))

    def test_valid_manual_arm_bundle(self):
        self.assertEqual(module.verify(self.app, '0.1.0', '1'), 1)
        self.assertTrue(all(call.kwargs['check'] for call in self.lipo.call_args_list))

    def signing_output(self, *, details=None, entitlements=None):
        details = details if details is not None else 'TeamIdentifier=TESTTEAMID\nAuthority=Developer ID Application: Fixture\nCodeDirectory flags=0x10000(runtime)\n'
        entitlements = entitlements if entitlements is not None else {'com.apple.security.automation.apple-events': True}
        def output(args, **kwargs):
            if args[0] == 'codesign':
                return plistlib.dumps(entitlements) if '--entitlements' in args else details
            return 'XML plist' if Path(args[-1]).name == 'Info.plist' else 'Mach-O fixture'
        self.file.side_effect = output

    def test_distribution_signing_policy(self):
        self.signing_output()
        self.assertEqual(module.verify(self.app, '0.1.0', '1', 'TESTTEAMID'), 1)

    def test_bad_signing_policy(self):
        cases = [
            ({'details': 'TeamIdentifier=WRONG\nAuthority=Developer ID Application: Fixture\n(runtime)\n'}, 'identity/team'),
            ({'details': 'TeamIdentifier=TESTTEAMID\nAuthority=Developer ID Application: Fixture\n'}, 'runtime'),
            ({'entitlements': {'com.apple.security.get-task-allow': True}}, 'Debug entitlement'),
            ({'entitlements': {}}, 'automation entitlement'),
        ]
        for parameters, reason in cases:
            with self.subTest(reason=reason):
                self.signing_output(**parameters)
                with self.assertRaisesRegex(ValueError, reason):
                    module.verify(self.app, '0.1.0', '1', 'TESTTEAMID')

    def test_wrong_identity(self):
        self.info['CFBundleIdentifier'] = 'upstream.product'
        self.write_info()
        with self.assertRaisesRegex(ValueError, 'identity'):
            module.verify(self.app)

    def test_wrong_version(self):
        with self.assertRaisesRegex(ValueError, 'version/build'):
            module.verify(self.app, '0.2.0', '1')

    def test_update_feed_or_key_rejected(self):
        for key in ('SUFeedURL', 'SUPublicEDKey', 'SUPublicDSAKey'):
            with self.subTest(key=key):
                self.info[key] = 'fixture'
                self.write_info()
                with self.assertRaisesRegex(ValueError, 'Updater trust'):
                    module.verify(self.app)
                del self.info[key]

    def test_automatic_or_unspecified_updates_rejected(self):
        for value in (True, None):
            self.info['SUAutomaticallyUpdate'] = value
            if value is None:
                del self.info['SUAutomaticallyUpdate']
            self.write_info()
            with self.assertRaisesRegex(ValueError, 'Automatic updates'):
                module.verify(self.app)

    def test_missing_main(self):
        (self.app / 'Contents/MacOS/Remy').unlink()
        with self.assertRaisesRegex(ValueError, 'main executable'):
            module.verify(self.app)

    def test_executable_filename_cannot_escape_bundle(self):
        for value in ('../Remy', '/tmp/Remy', '.', 'folder\\Remy'):
            with self.subTest(value=value):
                self.info['CFBundleExecutable'] = value
                self.write_info()
                with self.assertRaisesRegex(ValueError, 'bundle-local'):
                    module.verify(self.app)

    def test_main_requires_executable_permission(self):
        (self.app / 'Contents/MacOS/Remy').chmod(0o644)
        with self.assertRaisesRegex(ValueError, 'executable permission'):
            module.verify(self.app)

    def test_non_macho_main(self):
        self.file.side_effect = None
        self.file.return_value = 'text'
        with self.assertRaisesRegex(ValueError, 'not Mach-O'):
            module.verify(self.app)

    def test_nested_intel_only_binary_rejected(self):
        helper = self.app / 'Contents/Frameworks/Helper'
        helper.parent.mkdir()
        helper.write_bytes(b'fixture')
        def lipo(arguments, **kwargs):
            if arguments[2] == str(helper):
                raise subprocess.CalledProcessError(1, arguments)
        self.lipo.side_effect = lipo
        with self.assertRaises(subprocess.CalledProcessError):
            module.verify(self.app)


if __name__ == '__main__':
    unittest.main()
