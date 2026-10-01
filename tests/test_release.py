import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

from scripts.release import commit_notes, merge_manifest, package, published_changelog


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="fieldlang-release-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        source = Path(__file__).resolve().parents[1]
        self.project = self.root / "Jellyfin.Plugin.FieldLang"
        self.project.mkdir()
        for name in ("Jellyfin.Plugin.FieldLang.csproj", "build.yaml"):
            shutil.copyfile(source / "Jellyfin.Plugin.FieldLang" / name, self.project / name)
        for name in ("manifest.json", "CHANGELOG.md", "LICENSE"):
            shutil.copyfile(source / name, self.root / name)
        self.version = ET.parse(
            self.project / "Jellyfin.Plugin.FieldLang.csproj").findtext("PropertyGroup/AssemblyVersion")
        # Published current versions must not make future source-tree fixtures depend on an
        # actual built DLL's checksum. Every test starts with older catalogue entries only.
        manifest_path = self.root / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest[0]["versions"] = [entry for entry in manifest[0]["versions"] if entry["version"] != self.version]
        manifest_path.write_text(json.dumps(manifest))
        self.dll = self.project / "bin/Release/net10.0/Jellyfin.Plugin.FieldLang.dll"
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b"test plugin binary")
        self.repository = "idontwantfame/jellyfin-plugin-fieldlang"

    def packaged(self, folder="dist"):
        output = self.root / folder
        package(self.root, output, self.repository, "v" + self.version)
        return output, json.loads((output / "manifest.json").read_text())

    def test_archive_and_catalogue(self):
        output, manifest = self.packaged()
        archive = output / f"field-language_{self.version}.zip"
        with zipfile.ZipFile(archive) as bundle:
            self.assertEqual(bundle.namelist(), [self.dll.name, "LICENSE"])
            self.assertEqual(bundle.read(self.dll.name), self.dll.read_bytes())
            self.assertIn(b"rbcetin", bundle.read("LICENSE"))
        entry = manifest[0]["versions"][0]
        self.assertEqual(entry["version"], self.version)
        self.assertEqual(entry["checksum"], hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest())
        self.assertIn(f"/{self.repository}/releases/download/v{self.version}/", entry["sourceUrl"])
        self.assertEqual(manifest[0]["owner"], "idontwantfame")
        previous = json.loads((self.root / "manifest.json").read_text())
        self.assertEqual(manifest[0]["versions"][1:], previous[0]["versions"])
        self.assertIn(hashlib.sha256(archive.read_bytes()).hexdigest(), (output / (archive.name + ".sha256")).read_text())

    def test_archive_is_repeatable(self):
        first, _ = self.packaged("first")
        second, _ = self.packaged("second")
        name = f"field-language_{self.version}.zip"
        self.assertEqual((first / name).read_bytes(), (second / name).read_bytes())

    def test_ci_can_package_a_changed_current_version_without_changing_catalogue(self):
        _, published = self.packaged("published")
        path = self.root / "manifest.json"
        path.write_text(json.dumps(published))
        self.dll.write_bytes(b"changed development binary")
        _, candidate = self.packaged("development")
        self.assertEqual(json.loads(path.read_text()), published)
        self.assertNotEqual(candidate[0]["versions"][0]["checksum"], published[0]["versions"][0]["checksum"])
        with self.assertRaises(ValueError):
            merge_manifest(published, candidate)

    def test_wrong_tag_is_rejected_without_output(self):
        with self.assertRaises(ValueError):
            package(self.root, self.root / "dist", self.repository, "v999.0.0.0")
        self.assertFalse((self.root / "dist").exists())

    def test_version_mismatch_is_rejected(self):
        path = self.project / "build.yaml"
        path.write_text(path.read_text().replace(f'version: "{self.version}"', 'version: "999.0.0.0"'))
        with self.assertRaises(ValueError):
            self.packaged()

    def test_host_abi_mismatch_is_rejected(self):
        path = self.project / "build.yaml"
        path.write_text(path.read_text().replace('targetAbi: "12.0.0.0"', 'targetAbi: "13.0.0.0"'))
        with self.assertRaises(ValueError):
            self.packaged()

    def test_missing_build_is_rejected_before_output(self):
        self.dll.unlink()
        with self.assertRaises(FileNotFoundError):
            self.packaged()
        self.assertFalse((self.root / "dist").exists())

    def test_existing_output_is_not_overwritten(self):
        output, _ = self.packaged()
        before = (output / "metadata.json").read_bytes()
        with self.assertRaises(FileExistsError):
            self.packaged()
        self.assertEqual((output / "metadata.json").read_bytes(), before)

    def test_merge_is_idempotent(self):
        _, manifest = self.packaged()
        self.assertEqual(merge_manifest(manifest, manifest), manifest)

    def test_merge_preserves_newer_and_upstream_entries(self):
        _, released = self.packaged()
        current = json.loads((self.root / "manifest.json").read_text())
        newer = copy.deepcopy(released[0]["versions"][0])
        newer["version"] = "99.0.0.0"
        current[0]["versions"].insert(0, newer)
        merged = merge_manifest(current, released)
        self.assertEqual(merged[0]["versions"][0], newer)
        self.assertEqual(merged[0]["versions"][1], released[0]["versions"][0])
        self.assertEqual(merged[0]["versions"][2:], current[0]["versions"][1:])

    def test_changed_published_archive_is_rejected(self):
        _, released = self.packaged()
        changed = copy.deepcopy(released)
        changed[0]["versions"][0]["checksum"] = "0" * 32
        with self.assertRaises(ValueError):
            merge_manifest(released, changed)

    def test_different_plugin_is_rejected(self):
        _, released = self.packaged()
        changed = copy.deepcopy(released)
        changed[0]["guid"] = "another-plugin"
        with self.assertRaises(ValueError):
            merge_manifest(released, changed)

    def test_published_changelog_replaces_only_its_section(self):
        original = "# Changelog\n\n## 3.0.0.0 — Unreleased\n\nFuture\n\n## 2.1.0.0 — Unreleased\n\nDraft\n\n## 2.0.0.0\n\nOld\n"
        updated = published_changelog(original, "2.1.0.0", "Highlights\n\n### Commits\n\n- Fixed")
        self.assertIn("3.0.0.0 — Unreleased\n\nFuture", updated)
        self.assertNotIn("2.1.0.0 — Unreleased", updated)
        self.assertIn("## 2.1.0.0\n\nHighlights", updated)
        self.assertIn("2.0.0.0\n\nOld", updated)
        self.assertEqual(published_changelog(updated, "2.1.0.0", "Highlights\n\n### Commits\n\n- Fixed"), updated)

    def test_commit_notes_use_the_previous_release_tag(self):
        def git(*args):
            return subprocess.check_output(["git", "-C", str(self.root), "-c", "user.name=Test", "-c", "user.email=test@example.invalid", *args], text=True).strip()
        git("init", "-q")
        git("add", ".")
        git("commit", "-qm", "Old upstream work")
        git("tag", "v2.0.0.0")
        (self.root / "work.txt").write_text("new work")
        git("add", "work.txt")
        git("commit", "-qm", "fix: preserve [manual] titles")
        commit = git("rev-parse", "HEAD")
        git("tag", "v" + self.version)
        notes = commit_notes(self.root, self.repository, self.version, commit)
        self.assertIn("Commits since v2.0.0.0", notes)
        self.assertIn(r"preserve \[manual\] titles", notes)
        self.assertIn(f"https://github.com/{self.repository}/commit/{commit}", notes)
        self.assertNotIn("Old upstream work", notes)
        changelog_path = self.root / "CHANGELOG.md"
        changelog_path.write_text(changelog_path.read_text() + "\n\n" + notes + "\n")
        package(self.root, self.root / "with-notes", self.repository, "v" + self.version, commit)
        rendered = (self.root / "with-notes/release-notes.md").read_text()
        self.assertEqual(rendered.count("<!-- generated-commit-changelog -->"), 1)
        self.assertEqual(rendered.count(f"/commit/{commit}"), 1)


if __name__ == "__main__":
    unittest.main()
