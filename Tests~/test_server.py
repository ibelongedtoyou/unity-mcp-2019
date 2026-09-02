from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[1]
SERVER_PATH = PACKAGE_ROOT / "Tools~" / "server.py"


def load_server():
    spec = importlib.util.spec_from_file_location("unity_mcp_2019_server_tests", SERVER_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot import {SERVER_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ServerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = load_server()

    def test_public_identity_and_version(self):
        self.assertEqual("unity-mcp-2019", self.server.SERVER_NAME)
        self.assertEqual("0.3.0", self.server.SERVER_VERSION)
        self.assertEqual("UnityMcp2019", self.server.RUNTIME_DIRECTORY_NAME)

    def test_product_runtime_names_are_consistent(self):
        source_paths = [
            PACKAGE_ROOT / "Tools~" / "server.py",
            PACKAGE_ROOT / "Editor" / "Mcp2019Bridge.cs",
            PACKAGE_ROOT / "Editor" / "Mcp2019BuildTools.cs",
            PACKAGE_ROOT / "Editor" / "Mcp2019ControlWindow.cs",
            PACKAGE_ROOT / "Editor" / "Mcp2019PackageTools.cs",
            PACKAGE_ROOT / "Editor" / "Mcp2019ServerManager.cs",
            PACKAGE_ROOT / "Editor" / "Mcp2019TestTools.cs",
        ]
        combined = "\n".join(
            path.read_text(encoding="utf-8") for path in source_paths
        )
        self.assertIn("UnityMcp2019", combined)
        self.assertIn("Tools/MCP for Unity 2019/", combined)

    def test_upstream_compatible_catalog_size(self):
        legacy = set(self.server.LEGACY_TOOL_NAMES)
        public_tools = {
            tool["name"] for tool in self.server.TOOLS if tool["name"] not in legacy
        }
        public_resources = {
            item.get("uri") or item.get("uriTemplate")
            for item in [*self.server.RESOURCES, *self.server.RESOURCE_TEMPLATES]
            if not str(item.get("uri") or item.get("uriTemplate") or "").startswith(
                "unity2019://"
            )
        }
        self.assertEqual(48, len(public_tools))
        self.assertEqual(25, len(public_resources))

    def test_http_origin_policy_is_loopback_only(self):
        self.assertTrue(self.server._origin_is_allowed("http://127.0.0.1:6500"))
        self.assertTrue(self.server._origin_is_allowed("http://localhost:6500"))
        self.assertFalse(self.server._origin_is_allowed("https://example.com"))

    def test_public_catalog_is_documented(self):
        tools_document = (PACKAGE_ROOT / "Documentation~" / "tools-reference.md").read_text(
            encoding="utf-8"
        )
        resources_document = (
            PACKAGE_ROOT / "Documentation~" / "resources-reference.md"
        ).read_text(encoding="utf-8")

        legacy = set(self.server.LEGACY_TOOL_NAMES)
        public_tools = sorted(
            tool["name"]
            for tool in self.server.TOOLS
            if tool["name"] not in legacy
        )
        public_resources = sorted(
            str(item.get("uri") or item.get("uriTemplate"))
            for item in [*self.server.RESOURCES, *self.server.RESOURCE_TEMPLATES]
            if not str(item.get("uri") or item.get("uriTemplate") or "").startswith(
                "unity2019://"
            )
        )

        missing_tools = [
            name for name in public_tools if f"`{name}`" not in tools_document
        ]
        missing_resources = [
            uri for uri in public_resources if f"`{uri}`" not in resources_document
        ]
        self.assertEqual([], missing_tools, "Public tools missing from documentation")
        self.assertEqual(
            [], missing_resources, "Public resources missing from documentation"
        )


if __name__ == "__main__":
    unittest.main()
