import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("publication_check", Path(__file__).with_name("check.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class PublicationScanTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.run_git("init", "-q")

    def run_git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], stderr=subprocess.PIPE)

    def commit(self):
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Synthetic Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "synthetic fixture")

    def test_deleted_historical_secret_is_detected_without_disclosing_value(self):
        token = "sk-" + "X" * 35
        path = self.root / "old.txt"
        path.write_text(token)
        self.commit()
        path.unlink()
        self.commit()
        report = check.scan(self.root)
        self.assertTrue(any(item["scope"] == "history" and item["category"] == "provider_token"
                            for item in report["findings"]))
        self.assertNotIn(token, str(report))

    def test_current_untracked_secret_and_sensitive_filename_are_detected(self):
        (self.root / "clean.txt").write_text("public synthetic text")
        self.commit()
        (self.root / ".env").write_text("a harmless synthetic configuration")
        (self.root / "new.txt").write_text("Bearer " + "Z" * 36)
        report = check.scan(self.root)
        categories = {item["category"] for item in report["findings"]}
        self.assertIn("literal_bearer", categories)
        self.assertIn("sensitive_filename", categories)

    def test_symlink_is_not_followed_and_binary_and_large_inputs_need_review(self):
        (self.root / "clean.txt").write_text("clean")
        self.commit()
        outside = self.root.parent / (self.root.name + "-outside")
        outside.write_text("Bearer " + "Q" * 36)
        self.addCleanup(outside.unlink)
        (self.root / "linked").symlink_to(outside)
        (self.root / "audio.wav").write_bytes(b"RIFF\0synthetic")
        (self.root / "large.txt").write_text("a" * 100)
        report = check.scan(self.root, max_bytes=50)
        self.assertFalse(report["findings"])
        reasons = {item["reason"] for item in report["manual_review"]}
        self.assertTrue({"symlink_not_followed", "binary_content", "oversized_file"} <= reasons)

    def test_ignored_local_configuration_is_not_read(self):
        (self.root / ".gitignore").write_text("Signing.local.xcconfig\n")
        self.commit()
        (self.root / "Signing.local.xcconfig").write_text("Bearer " + "I" * 36)
        self.assertFalse(check.scan(self.root)["findings"])

    def test_sensitive_historical_filename_detected_even_with_duplicate_blob(self):
        (self.root / "clean.txt").write_text("same harmless content")
        (self.root / "certificate.p12").write_text("same harmless content")
        self.commit()
        (self.root / "certificate.p12").unlink()
        self.commit()
        report = check.scan(self.root)
        self.assertTrue(any(item["scope"] == "history"
                            and item["file"]["path"] == "certificate.p12"
                            and item["category"] == "sensitive_filename"
                            for item in report["findings"]))


if __name__ == "__main__":
    unittest.main()
