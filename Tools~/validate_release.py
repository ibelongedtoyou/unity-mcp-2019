#!/usr/bin/env python3
"""Validate the release layout without modifying a Unity project."""

from __future__ import annotations

import importlib.util
import json
import re
import sys
import unittest
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[1]
REQUIRED_FILES = (
    "package.json",
    "README.md",
    "README.zh-CN.md",
    "FEATURES.md",
    "FEATURES.zh-CN.md",
    "CHANGELOG.md",
    "LICENSE.md",
    "SECURITY.md",
    "CONTRIBUTING.md",
    "RELEASE_CHECKLIST.md",
    "THIRD_PARTY_NOTICES.md",
    "Documentation~/tools-reference.md",
    "Documentation~/resources-reference.md",
    "Documentation~/support-matrix.md",
    "Editor/UnityMcp2019.Editor.asmdef",
    "Tools~/server.py",
    "Tests~/test_server.py",
)
LOCAL_PATH_PATTERNS = (
    re.compile(r"(?i)\b[A-Z]:[\\/]"),
    re.compile(r"(?i)/(?:Users|home)/[^\s\"']+"),
)
SECRET_PATTERNS = (
    re.compile(r"AKIA[0-9A-Z]{16}"),
    re.compile(r"gh[pousr]_[A-Za-z0-9_]{20,}"),
    re.compile(r"sk-[A-Za-z0-9_-]{20,}"),
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
)


def load_server():
    path = PACKAGE_ROOT / "Tools~" / "server.py"
    spec = importlib.util.spec_from_file_location("unity_mcp_2019_release", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot import {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate() -> list[str]:
    errors: list[str] = []
    for relative in REQUIRED_FILES:
        if not (PACKAGE_ROOT / relative).is_file():
            errors.append(f"Missing required file: {relative}")

    try:
        manifest = json.loads((PACKAGE_ROOT / "package.json").read_text(encoding="utf-8"))
    except Exception as exception:
        errors.append(f"Invalid package.json: {exception}")
        return errors

    if manifest.get("name") != "com.ibelongedtoyou.unity-mcp-2019":
        errors.append("Unexpected package name.")
    if manifest.get("version") != "0.3.0":
        errors.append("Unexpected package version.")
    if manifest.get("unity") != "2019.4":
        errors.append("Package must target Unity 2019.4.")

    try:
        server = load_server()
        if server.SERVER_NAME != "unity-mcp-2019":
            errors.append("Python server identity is project-specific.")
        if server.SERVER_VERSION != manifest.get("version"):
            errors.append("Python server and package versions differ.")
    except Exception as exception:
        errors.append(f"Python server cannot be imported: {exception}")

    text_extensions = {".cs", ".json", ".md", ".py", ".asmdef"}
    for path in PACKAGE_ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in text_extensions:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for pattern in LOCAL_PATH_PATTERNS:
            if pattern.search(text):
                errors.append(
                    f"Local absolute path in {path.relative_to(PACKAGE_ROOT)}"
                )
        for pattern in SECRET_PATTERNS:
            if pattern.search(text):
                errors.append(f"Possible credential in {path.relative_to(PACKAGE_ROOT)}")

    return errors


def run_tests() -> unittest.result.TestResult:
    suite = unittest.defaultTestLoader.discover(str(PACKAGE_ROOT / "Tests~"))
    return unittest.TextTestRunner(verbosity=2).run(suite)


def main() -> int:
    errors = validate()
    for error in errors:
        print(f"ERROR: {error}", file=sys.stderr)
    tests = run_tests()
    if errors or not tests.wasSuccessful():
        return 1
    print("Release validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
