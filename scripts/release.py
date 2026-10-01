#!/usr/bin/env python3
"""Package Field Language and merge published catalogue entries (standard library only)."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone


def version_key(version):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise ValueError(f"Expected a four-part plugin version, got {version!r}")
    return tuple(map(int, version.split(".")))


def scalar(text, key):
    match = re.search(rf'^{re.escape(key)}: "([^"\n]+)"\s*$', text, re.MULTILINE)
    if not match:
        raise ValueError(f"Missing quoted {key} in build.yaml")
    return match.group(1)


def merge_manifest(current, released):
    if len(current) != 1 or len(released) != 1 or current[0]["guid"] != released[0]["guid"]:
        raise ValueError("Manifest must describe the same single plugin")
    incoming = released[0]["versions"][0]
    versions = {entry["version"]: entry for entry in current[0]["versions"]}
    version_key(incoming["version"])
    if incoming["version"] in versions:
        previous = versions[incoming["version"]]
        for field in ("checksum", "sourceUrl", "targetAbi"):
            if previous[field] != incoming[field]:
                raise ValueError("Refusing to replace an existing version with different release assets")
    else:
        versions[incoming["version"]] = incoming
    plugin = dict(current[0])
    plugin["owner"] = released[0]["owner"]
    plugin["versions"] = sorted(versions.values(), key=lambda entry: version_key(entry["version"]), reverse=True)
    return [plugin]


def commit_notes(root, repository, version, commit):
    def git(*arguments):
        return subprocess.check_output(["git", "-C", str(root), *arguments], text=True).strip()

    # Only four-part, reachable, earlier version tags are release boundaries for this plugin.
    tags = git("tag", "--merged", commit, "--list", "v*").splitlines()
    previous = [tag for tag in tags if re.fullmatch(r"v\d+\.\d+\.\d+\.\d+", tag)
                and version_key(tag[1:]) < version_key(version)]
    baseline = max(previous, key=lambda tag: version_key(tag[1:]), default=None)
    revision = f"{baseline}..{commit}" if baseline else commit
    entries = git("log", "--no-merges", "--format=%H%x00%s", revision).splitlines()
    heading = f"### Commits since {baseline}" if baseline else "### Commits in this release"
    lines = ["<!-- generated-commit-changelog -->", "", heading, ""]
    for entry in entries:
        sha, subject = entry.split("\0", 1)
        subject = re.sub(r"([\\`*_\[\]<>])", r"\\\1", subject)
        lines.append(f"- {subject} ([{sha[:7]}](https://github.com/{repository}/commit/{sha}))")
    if not entries:
        lines.append("No additional commits since the previous version tag.")
    return "\n".join(lines)


def published_changelog(current, version, notes):
    header = f"## {version}"
    replacement = header + "\n\n" + notes.strip() + "\n\n"
    pattern = rf"^## {re.escape(version)}(?:[ \t][^\n]*)?\n.*?(?=^## |\Z)"
    if re.search(pattern, current, re.MULTILINE | re.DOTALL):
        return re.sub(pattern, lambda _: replacement, current, count=1, flags=re.MULTILINE | re.DOTALL).rstrip() + "\n"
    introduction, separator, history = current.partition("\n\n")
    return introduction + "\n\n" + replacement + history if separator else "# Changelog\n\n" + replacement + current


def package(root, output, repository, tag=None, commit=None):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be owner/name")
    project = root / "Jellyfin.Plugin.FieldLang"
    xml = ET.parse(project / "Jellyfin.Plugin.FieldLang.csproj").getroot()
    version = xml.findtext("PropertyGroup/AssemblyVersion")
    version_key(version or "")
    if xml.findtext("PropertyGroup/FileVersion") != version:
        raise ValueError("AssemblyVersion and FileVersion must agree")
    if xml.findtext("PropertyGroup/Version") != ".".join(version.split(".")[:3]):
        raise ValueError("Package Version must match the first three assembly version parts")
    build = (project / "build.yaml").read_text()
    if scalar(build, "version") != version:
        raise ValueError("build.yaml and project versions must agree")
    framework = scalar(build, "framework")
    if framework != xml.findtext("PropertyGroup/TargetFramework"):
        raise ValueError("build.yaml and project frameworks must agree")
    abi = scalar(build, "targetAbi")
    version_key(abi)
    controller = xml.find(".//PackageReference[@Include='Jellyfin.Controller']")
    model = xml.find(".//PackageReference[@Include='Jellyfin.Model']")
    if controller is None or model is None or any(ref.get("Version") + ".0" != abi for ref in (controller, model)):
        raise ValueError("Jellyfin package versions must agree with targetAbi")
    expected_tag = "v" + version
    if tag is not None and tag != expected_tag:
        raise ValueError(f"Tag must be {expected_tag}, not {tag}")
    manifest = json.loads((root / "manifest.json").read_text())
    if len(manifest) != 1 or manifest[0]["guid"] != scalar(build, "guid"):
        raise ValueError("Build and catalogue plugin identities must agree")
    if any(version_key(entry["version"]) > version_key(version) for entry in manifest[0]["versions"]):
        raise ValueError("The source version is older than the catalogue's latest release")
    changelog = (root / "CHANGELOG.md").read_text()
    section = re.search(rf"^## {re.escape(version)}(?:[ \t][^\n]*)?\n(.*?)(?=^## |\Z)", changelog, re.MULTILINE | re.DOTALL)
    if not section or not section.group(1).strip():
        raise ValueError(f"Missing CHANGELOG.md section for {version}")
    notes = section.group(1).strip().split("\n\n<!-- generated-commit-changelog -->", 1)[0]
    if commit:
        notes += "\n\n" + commit_notes(root, repository, version, commit)
    dll = project / "bin" / "Release" / framework / "Jellyfin.Plugin.FieldLang.dll"
    dll_data = dll.read_bytes()
    license_data = (root / "LICENSE").read_bytes()
    # A fresh directory prevents accidental replacement of a previously packaged release.
    output.mkdir(parents=True, exist_ok=False)
    archive_name = f"field-language_{version}.zip"
    archive = output / archive_name
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name, content in ((dll.name, dll_data), ("LICENSE", license_data)):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, content)
    data = archive.read_bytes()
    md5 = hashlib.md5(data, usedforsecurity=False).hexdigest()
    sha256 = hashlib.sha256(data).hexdigest()
    for algorithm, digest in (("md5", md5), ("sha256", sha256)):
        (output / f"{archive_name}.{algorithm}").write_text(f"{digest}  {archive_name}\n")
    release_plugin = dict(manifest[0])
    release_plugin["owner"] = scalar(build, "owner")
    release_plugin["versions"] = [{
        "version": version,
        "changelog": notes,
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/{repository}/releases/download/{expected_tag}/{archive_name}",
        "checksum": md5,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }]
    # CI can package development commits while the assembly version still matches a published
    # release. This is a candidate only; actual publication/merging still enforces immutability.
    baseline_plugin = dict(manifest[0])
    baseline_plugin["versions"] = [entry for entry in manifest[0]["versions"] if entry["version"] != version]
    candidate = merge_manifest([baseline_plugin], [release_plugin])
    (output / "manifest.json").write_text(json.dumps(candidate, indent=2) + "\n")
    (output / "release-notes.md").write_text(notes + "\n")
    metadata = {"version": version, "tag": expected_tag, "archive": archive_name, "repository": repository}
    (output / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    return metadata


def write_manifest(path, content):
    # Keep an existing catalogue intact if serialization or writing fails.
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as temporary:
        temporary_path = Path(temporary.name)
        try:
            json.dump(content, temporary, indent=2)
            temporary.write("\n")
            temporary.flush()
            os.fsync(temporary.fileno())
        except BaseException:
            temporary_path.unlink(missing_ok=True)
            raise
    try:
        os.chmod(temporary_path, 0o644)
        os.replace(temporary_path, path)
    finally:
        temporary_path.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    packaging = commands.add_parser("package")
    packaging.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    packaging.add_argument("--output", type=Path, required=True)
    packaging.add_argument("--repository", required=True)
    packaging.add_argument("--tag")
    packaging.add_argument("--commit", help="Git revision whose commits to include in release notes")
    merging = commands.add_parser("merge-manifest")
    merging.add_argument("--current", type=Path, required=True)
    merging.add_argument("--release", type=Path, required=True)
    merging.add_argument("--changelog", type=Path)
    args = parser.parse_args()
    if args.command == "package":
        print(json.dumps(package(args.root, args.output, args.repository, args.tag, args.commit)))
    else:
        current = json.loads(args.current.read_text())
        released = json.loads(args.release.read_text())
        write_manifest(args.current, merge_manifest(current, released))
        if args.changelog:
            version = released[0]["versions"][0]["version"]
            notes = released[0]["versions"][0]["changelog"]
            args.changelog.write_text(published_changelog(args.changelog.read_text(), version, notes))


if __name__ == "__main__":
    main()
