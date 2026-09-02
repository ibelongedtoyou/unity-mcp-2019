#!/usr/bin/env python3
"""Audit the Unity 2019 compatibility surface against a frozen unity-mcp main."""

from __future__ import annotations

import argparse
import ast
import importlib.util
import json
import re
import subprocess
from pathlib import Path
from typing import Any


TOOL_LINE = re.compile(r"^- \*\*\[`([^`]+)`\]", re.MULTILINE)
RESOURCE_HEADING = re.compile(r"^## `([^`]+)`", re.MULTILINE)
RESOURCE_URI = re.compile(r"\*\*URI:\*\* `([^`]+)`")
CLIENT_ROW = re.compile(r"^\| \*\*([^*]+)\*\* \|", re.MULTILINE)
CLIENT_ARRAY = re.compile(
    r"private\s+static\s+readonly\s+string\[\]\s+Clients\s*=\s*\{(.*?)\};",
    re.DOTALL,
)
QUOTED_STRING = re.compile(r'"([^"\\]*(?:\\.[^"\\]*)*)"')


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _load_server_module(server_path: Path) -> Any:
    spec = importlib.util.spec_from_file_location("unity_mcp_2019_audit", server_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load compatibility server: {server_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _extract_local_catalog(
    server_path: Path,
) -> tuple[dict[str, dict[str, Any]], set[str]]:
    module = _load_server_module(server_path)
    legacy_names = set(getattr(module, "LEGACY_TOOL_NAMES", set()))
    tools = {
        str(tool["name"]): tool
        for tool in module.TOOLS
        if str(tool.get("name") or "") not in legacy_names
    }
    resources = {
        str(resource.get("uri") or resource.get("uriTemplate"))
        for resource in [*module.RESOURCES, *module.RESOURCE_TEMPLATES]
        if not str(resource.get("uri") or resource.get("uriTemplate") or "").startswith(
            "unity2019://"
        )
    }
    return tools, resources


def _decorator_name(node: ast.expr) -> str:
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        return node.attr
    if isinstance(node, ast.Call):
        return _decorator_name(node.func)
    return ""


def _literal_strings(annotation: ast.expr | None) -> set[str]:
    if annotation is None:
        return set()
    values: set[str] = set()
    for node in ast.walk(annotation):
        if not isinstance(node, ast.Subscript) or _decorator_name(node.value) != "Literal":
            continue
        items = node.slice.elts if isinstance(node.slice, ast.Tuple) else [node.slice]
        for item in items:
            if isinstance(item, ast.Constant) and isinstance(item.value, str):
                values.add(item.value)
    return values


def _extract_upstream_signatures(upstream: Path) -> dict[str, dict[str, Any]]:
    signatures: dict[str, dict[str, Any]] = {}
    tool_root = upstream / "Server" / "src" / "services" / "tools"
    for path in tool_root.rglob("*.py"):
        tree = ast.parse(_read(path), filename=str(path))
        for node in ast.walk(tree):
            if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                continue
            if not any(
                _decorator_name(decorator) == "mcp_for_unity_tool"
                for decorator in node.decorator_list
            ):
                continue
            parameters: set[str] = set()
            action_values: set[str] = set()
            all_args = [*node.args.posonlyargs, *node.args.args, *node.args.kwonlyargs]
            for argument in all_args:
                if argument.arg in {"ctx", "unity_instance"}:
                    continue
                parameters.add(argument.arg)
                if argument.arg == "action":
                    action_values = _literal_strings(argument.annotation)
            signatures[node.name] = {
                "parameters": parameters,
                "actions": action_values,
            }
    return signatures


def _extract_local_signatures(
    tools: dict[str, dict[str, Any]],
) -> dict[str, dict[str, Any]]:
    signatures: dict[str, dict[str, Any]] = {}
    for name, tool in tools.items():
        properties = (tool.get("inputSchema") or {}).get("properties") or {}
        action_schema = properties.get("action") or {}
        signatures[name] = {
            "parameters": set(properties),
            "actions": set(action_schema.get("enum") or []),
        }
    return signatures


def _normalize_client(name: str) -> str:
    aliases = {"VS Code Copilot": "VS Code (Copilot)"}
    return aliases.get(name.strip(), name.strip())


def _extract_local_clients(control_window: Path) -> set[str]:
    match = CLIENT_ARRAY.search(_read(control_window))
    if not match:
        raise RuntimeError(f"Client list not found in {control_window}")
    return {
        _normalize_client(bytes(value, "utf-8").decode("unicode_escape"))
        for value in QUOTED_STRING.findall(match.group(1))
    }


def _git_head(upstream: Path) -> str:
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=upstream,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    return completed.stdout.strip() if completed.returncode == 0 else "unknown"


def build_report(
    upstream: Path,
    server_path: Path,
    control_window: Path,
) -> dict[str, Any]:
    tool_doc = _read(upstream / "website/docs/reference/tools/index.md")
    resource_doc = _read(upstream / "website/docs/reference/resources/index.md")
    client_doc = _read(upstream / "website/docs/getting-started/clients.md")
    package = json.loads(_read(upstream / "MCPForUnity/package.json"))

    upstream_tools = set(TOOL_LINE.findall(tool_doc))
    upstream_resource_uris = set(RESOURCE_URI.findall(resource_doc))
    upstream_clients = {_normalize_client(name) for name in CLIENT_ROW.findall(client_doc)}
    local_tools, local_resource_uris = _extract_local_catalog(server_path)
    local_clients = _extract_local_clients(control_window)
    upstream_signatures = _extract_upstream_signatures(upstream)
    local_signatures = _extract_local_signatures(local_tools)

    parameter_mismatches: dict[str, Any] = {}
    action_mismatches: dict[str, Any] = {}
    for name in sorted(upstream_tools & set(local_tools)):
        upstream_signature = upstream_signatures.get(name, {})
        local_signature = local_signatures.get(name, {})
        upstream_parameters = set(upstream_signature.get("parameters", set()))
        local_parameters = set(local_signature.get("parameters", set()))
        if upstream_parameters != local_parameters:
            parameter_mismatches[name] = {
                "missing": sorted(upstream_parameters - local_parameters),
                "extra": sorted(local_parameters - upstream_parameters),
            }
        upstream_actions = set(upstream_signature.get("actions", set()))
        local_actions = set(local_signature.get("actions", set()))
        if upstream_actions and upstream_actions != local_actions:
            action_mismatches[name] = {
                "missing": sorted(upstream_actions - local_actions),
                "extra": sorted(local_actions - upstream_actions),
            }

    coverage = {
        "tool_count": len(set(local_tools) & upstream_tools),
        "upstream_tool_count": len(upstream_tools),
        "missing_tools": sorted(upstream_tools - set(local_tools)),
        "extra_tools": sorted(set(local_tools) - upstream_tools),
        "resource_uri_count": len(local_resource_uris & upstream_resource_uris),
        "upstream_resource_uri_count": len(upstream_resource_uris),
        "missing_resource_uris": sorted(upstream_resource_uris - local_resource_uris),
        "extra_resource_uris": sorted(local_resource_uris - upstream_resource_uris),
        "client_count": len(local_clients & upstream_clients),
        "upstream_client_count": len(upstream_clients),
        "missing_clients": sorted(upstream_clients - local_clients),
        "extra_clients": sorted(local_clients - upstream_clients),
        "parameter_mismatches": parameter_mismatches,
        "action_mismatches": action_mismatches,
    }
    coverage["strict_match"] = not any(
        value
        for key, value in coverage.items()
        if key.startswith("missing_")
        or key.startswith("extra_")
        or key.endswith("_mismatches")
    )

    return {
        "upstream": {
            "repository": "CoplayDev/unity-mcp",
            "branch": "main",
            "commit": _git_head(upstream),
            "package_version": package.get("version"),
            "minimum_unity": package.get("unity"),
            "tools": sorted(upstream_tools),
            "resource_names": sorted(set(RESOURCE_HEADING.findall(resource_doc))),
            "resource_uris": sorted(upstream_resource_uris),
            "clients": sorted(upstream_clients),
        },
        "local": {
            "server": str(server_path.resolve()),
            "control_window": str(control_window.resolve()),
            "tools": sorted(local_tools),
            "resource_uris": sorted(local_resource_uris),
            "clients": sorted(local_clients),
        },
        "coverage": coverage,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Audit Unity 2019 MCP main parity")
    parser.add_argument("--upstream", required=True, help="Frozen unity-mcp main checkout")
    parser.add_argument("--server", required=True, help="Local compatibility server.py")
    parser.add_argument(
        "--control-window",
        required=True,
        help="Mcp2019ControlWindow.cs containing the supported client list",
    )
    parser.add_argument("--output", help="Optional JSON report path")
    parser.add_argument("--strict", action="store_true", help="Fail on any surface mismatch")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    report = build_report(
        Path(args.upstream).resolve(),
        Path(args.server).resolve(),
        Path(args.control_window).resolve(),
    )
    encoded = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        output = Path(args.output).resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(encoded, encoding="utf-8")
    print(encoded, end="")
    return 1 if args.strict and not report["coverage"]["strict_match"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
