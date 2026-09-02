#!/usr/bin/env python3
"""Dependency-free stdio/HTTP MCP server for the Unity 2019 package bridge."""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import ctypes
import hashlib
import http.server
import ipaddress
import json
import os
import re
import secrets
import shutil
import socket
import sys
import tempfile
import threading
import time
import uuid
import zipfile
from html.parser import HTMLParser
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import unquote, urlencode, urlparse
from urllib.request import Request, urlopen


SERVER_NAME = "unity-mcp-2019"
SERVER_VERSION = "0.3.0"
RUNTIME_DIRECTORY_NAME = "UnityMcp2019"
LATEST_PROTOCOL_VERSION = "2025-06-18"
SUPPORTED_PROTOCOL_VERSIONS = {
    "2025-06-18",
    "2025-03-26",
    "2024-11-05",
}

MAX_HTTP_REQUEST_BYTES = 2 * 1024 * 1024
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}
SHOW_LEGACY_ALIASES = str(
    os.environ.get("UNITY_MCP_2019_SHOW_LEGACY") or ""
).strip().lower() in {"1", "true", "yes", "on"}

LEGACY_TOOL_NAMES = {
    "unity2019_editor_state",
    "unity2019_find_gameobjects",
    "unity2019_get_hierarchy",
    "unity2019_manage_component",
    "unity2019_manage_gameobject",
    "unity2019_ping",
    "unity2019_play_mode",
    "unity2019_read_console",
    "unity2019_refresh_assets",
    "unity2019_reload_active_scene",
    "unity2019_undo_redo",
}
LEGACY_RESOURCE_PREFIX = "unity2019://"

ALL_TOOL_GROUPS = {
    "animation",
    "asset_gen",
    "core",
    "docs",
    "probuilder",
    "profiling",
    "scripting_ext",
    "testing",
    "ui",
    "vfx",
}

TOOL_GROUP_BY_NAME = {
    "unity_docs": "docs",
    "unity_reflect": "docs",
    "execute_code": "scripting_ext",
    "manage_scriptable_object": "scripting_ext",
    "manage_animation": "animation",
    "manage_probuilder": "probuilder",
    "manage_profiler": "profiling",
    "run_tests": "testing",
    "get_test_job": "testing",
    "manage_ui": "ui",
    "manage_vfx": "vfx",
    "manage_shader": "vfx",
    "manage_texture": "vfx",
    "generate_audio": "asset_gen",
    "generate_image": "asset_gen",
    "generate_model": "asset_gen",
    "import_model": "asset_gen",
    "import_model_file": "asset_gen",
}


READ_ONLY_ANNOTATIONS = {
    "readOnlyHint": True,
    "destructiveHint": False,
    "idempotentHint": True,
    "openWorldHint": False,
}

ACTION_ANNOTATIONS = {
    "readOnlyHint": False,
    "destructiveHint": False,
    "idempotentHint": False,
    "openWorldHint": False,
}

DESTRUCTIVE_ANNOTATIONS = {
    "readOnlyHint": False,
    "destructiveHint": True,
    "idempotentHint": False,
    "openWorldHint": False,
}

VECTOR3_SCHEMA = {
    "type": "array",
    "items": {"type": "number"},
    "minItems": 3,
    "maxItems": 3,
}

TOOLS: list[dict[str, Any]] = [
    {
        "name": "unity2019_ping",
        "description": "Check the authenticated bridge connection to the Unity 2019 Editor.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity2019_editor_state",
        "description": (
            "Read compilation, asset update, play mode, and active scene state. "
            "Prefer the unity2019://editor/state resource when the client supports resources."
        ),
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity2019_read_console",
        "description": (
            "Read recent Unity log messages captured since the bridge loaded. "
            "This does not scrape older Console window history."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "limit": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50},
                "types": {
                    "type": "array",
                    "items": {
                        "type": "string",
                        "enum": ["Log", "Warning", "Error", "Assert", "Exception"],
                    },
                },
            },
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity2019_get_hierarchy",
        "description": "Read a bounded, flat snapshot of the active scene hierarchy.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "max_depth": {"type": "integer", "minimum": 0, "maximum": 10, "default": 4},
                "page_size": {"type": "integer", "minimum": 1, "maximum": 500, "default": 200},
                "max_items": {
                    "type": "integer",
                    "minimum": 1,
                    "maximum": 500,
                    "description": "Deprecated alias for page_size.",
                },
                "cursor": {"type": "string", "pattern": "^[0-9]+$"},
            },
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity2019_find_gameobjects",
        "description": "Find loaded-scene GameObjects by case-insensitive partial name.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {"type": "string", "minLength": 1},
                "include_inactive": {"type": "boolean", "default": True},
                "page_size": {"type": "integer", "minimum": 1, "maximum": 500, "default": 100},
                "max_results": {
                    "type": "integer",
                    "minimum": 1,
                    "maximum": 500,
                    "description": "Deprecated alias for page_size.",
                },
                "cursor": {"type": "string", "pattern": "^[0-9]+$"},
            },
            "required": ["query"],
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity2019_refresh_assets",
        "description": "Schedule AssetDatabase.Refresh on the next Unity Editor update.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "unity2019_play_mode",
        "description": "Schedule a play, stop, pause, or resume action in the Unity Editor.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["play", "stop", "pause", "resume"],
                }
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "unity2019_undo_redo",
        "description": "Perform one Unity Editor undo or redo step outside Play Mode.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["undo", "redo"]}
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "unity2019_reload_active_scene",
        "description": (
            "Reload the active saved scene from disk and discard all unsaved scene "
            "changes. Requires explicit confirmation and is disabled in Play Mode."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {"confirm_discard_changes": {"type": "boolean"}},
            "required": ["confirm_discard_changes"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "unity2019_manage_gameobject",
        "description": (
            "Create, modify, or delete one loaded-scene GameObject. Existing objects "
            "are addressed only by instance_id; all changes are registered with Unity Undo."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "modify", "delete"]},
                "instance_id": {"type": "integer"},
                "name": {"type": "string", "minLength": 1, "maxLength": 200},
                "primitive": {
                    "type": "string",
                    "enum": ["empty", "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"],
                    "default": "empty",
                },
                "parent_instance_id": {"type": "integer"},
                "active": {"type": "boolean"},
                "position": VECTOR3_SCHEMA,
                "rotation_euler": VECTOR3_SCHEMA,
                "scale": VECTOR3_SCHEMA,
                "confirm": {"type": "boolean", "default": False},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "unity2019_manage_component",
        "description": (
            "Add, remove, or set one supported serialized property on a component. "
            "Object references, arrays, and m_Script are intentionally unsupported; "
            "all changes are registered with Unity Undo."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["add", "remove", "set_property"]},
                "instance_id": {"type": "integer"},
                "component_type": {"type": "string", "minLength": 1},
                "component_index": {"type": "integer", "minimum": 0, "default": 0},
                "property_path": {"type": "string", "minLength": 1},
                "value": {
                    "anyOf": [
                        {"type": "boolean"},
                        {"type": "integer"},
                        {"type": "number"},
                        {"type": "string"},
                        {
                            "type": "array",
                            "items": {"type": "number"},
                            "minItems": 2,
                            "maxItems": 4,
                        },
                    ]
                },
                "confirm": {"type": "boolean", "default": False},
            },
            "required": ["action", "instance_id", "component_type"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
]

# Exact CoplayDev main tool names. The unity2019_* entries above remain as
# backwards-compatible aliases while clients migrate to the upstream surface.
TOOLS += [
    {
        "name": "batch_execute",
        "description": "Execute multiple MCP commands in one deterministic batch.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "commands": {"type": "array", "minItems": 1, "maxItems": 25},
                "parallel": {"type": "boolean"},
                "fail_fast": {"type": "boolean"},
                "max_parallelism": {"type": "integer", "minimum": 1},
            },
            "required": ["commands"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "execute_menu_item",
        "description": "Execute a Unity menu item by path.",
        "inputSchema": {
            "type": "object",
            "properties": {"menu_path": {"type": "string", "minLength": 1}},
            "required": ["menu_path"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "find_gameobjects",
        "description": "Search for GameObjects by name, tag, layer, component, path, or ID.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "search_term": {"type": "string", "minLength": 1},
                "search_method": {
                    "type": "string",
                    "enum": ["by_name", "by_tag", "by_layer", "by_component", "by_path", "by_id"],
                    "default": "by_name",
                },
                "include_inactive": {"type": ["boolean", "string"]},
                "page_size": {"type": ["integer", "string"]},
                "cursor": {"type": ["integer", "string"]},
            },
            "required": ["search_term"],
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "manage_gameobject",
        "description": "Perform CRUD operations on GameObjects.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "modify", "delete", "duplicate", "move_relative", "look_at"]},
                "target": {"type": ["integer", "string"]},
                "search_method": {"type": "string", "enum": ["by_id", "by_name", "by_path", "by_tag", "by_layer", "by_component"]},
                "name": {"type": "string"}, "tag": {"type": "string"},
                "parent": {"type": ["integer", "string"]}, "position": {},
                "rotation": {}, "scale": {}, "components_to_add": {},
                "primitive_type": {"type": "string"}, "save_as_prefab": {},
                "prefab_path": {"type": "string"}, "prefab_folder": {"type": "string"},
                "set_active": {}, "layer": {"type": "string"}, "is_static": {},
                "components_to_remove": {}, "component_properties": {},
                "new_name": {"type": "string"}, "offset": {},
                "reference_object": {"type": "string"}, "direction": {"type": "string"},
                "distance": {"type": "number"}, "world_space": {},
                "look_at_target": {}, "look_at_up": {}
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_components",
        "description": "Add, remove, or set properties on GameObject components.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["add", "remove", "set_property"]},
                "target": {"type": ["integer", "string"]},
                "component_type": {"type": "string"},
                "search_method": {"type": "string"},
                "property": {"type": "string"},
                "value": {},
                "properties": {"type": ["object", "string"]},
                "component_index": {"type": "integer", "minimum": 0},
            },
            "required": ["action", "target", "component_type"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_editor",
        "description": "Control and query Unity Editor state and settings.",
        "inputSchema": {"type": "object", "properties": {"action": {"type": "string", "enum": ["telemetry_status", "telemetry_ping", "play", "pause", "stop", "set_active_tool", "add_tag", "remove_tag", "add_layer", "remove_layer", "deploy_package", "restore_package", "undo", "redo"]}, "tool_name": {"type": "string"}, "tag_name": {"type": "string"}, "layer_name": {"type": "string"}}, "required": ["action"], "additionalProperties": False},
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_scene",
        "description": "Perform CRUD operations on Unity scenes.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "load", "save", "get_hierarchy", "get_active", "get_build_settings", "scene_view_frame", "close_scene", "set_active_scene", "get_loaded_scenes", "move_to_scene", "validate"]},
                "name": {"type": "string"}, "path": {"type": "string"},
                "build_index": {}, "scene_view_target": {}, "parent": {},
                "page_size": {}, "cursor": {}, "max_nodes": {}, "max_depth": {},
                "max_children_per_node": {}, "include_transform": {},
                "scene_name": {"type": "string"}, "scene_path": {"type": "string"},
                "target": {}, "remove_scene": {}, "additive": {},
                "template": {"type": "string"}, "auto_repair": {}
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "read_console",
        "description": "Get messages from or clear the Unity Editor console.",
        "inputSchema": {"type": "object", "properties": {"action": {"type": "string", "enum": ["get", "clear"]}, "types": {}, "count": {}, "filter_text": {"type": "string"}, "page_size": {}, "cursor": {}, "format": {"type": "string"}, "include_stacktrace": {}}, "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "refresh_unity",
        "description": "Request an AssetDatabase refresh and optional script compilation.",
        "inputSchema": {"type": "object", "properties": {"mode": {"type": "string"}, "scope": {"type": "string"}, "compile": {"type": "string"}, "wait_for_ready": {"type": "boolean"}}, "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "create_script",
        "description": "Create a C# script under Assets/.",
        "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}, "contents": {"type": "string"}, "script_type": {"type": "string"}, "namespace": {"type": "string"}}, "required": ["path", "contents"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "delete_script",
        "description": "Delete a C# script by URI or Assets-relative path.",
        "inputSchema": {"type": "object", "properties": {"uri": {"type": "string"}}, "required": ["uri"], "additionalProperties": False},
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "get_sha",
        "description": "Get SHA256 and metadata for a Unity C# script.",
        "inputSchema": {"type": "object", "properties": {"uri": {"type": "string"}}, "required": ["uri"], "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "find_in_file",
        "description": "Search a project file with a regular expression.",
        "inputSchema": {"type": "object", "properties": {"uri": {"type": "string"}, "pattern": {"type": "string"}, "project_root": {"type": "string"}, "max_results": {"type": "integer"}, "ignore_case": {}}, "required": ["uri", "pattern"], "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "apply_text_edits",
        "description": "Apply atomic 1-based line and column edits to a C# script.",
        "inputSchema": {"type": "object", "properties": {"uri": {"type": "string"}, "edits": {"type": "array"}, "precondition_sha256": {"type": "string"}, "strict": {"type": "boolean"}, "options": {"type": "object"}}, "required": ["uri", "edits"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "validate_script",
        "description": "Validate a C# script and return diagnostics.",
        "inputSchema": {"type": "object", "properties": {"uri": {"type": "string"}, "level": {"type": "string", "enum": ["basic", "standard"]}, "include_diagnostics": {"type": "boolean"}}, "required": ["uri"], "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "manage_script",
        "description": "Compatibility router for C# script create, read, and delete.",
        "inputSchema": {"type": "object", "properties": {"action": {"type": "string", "enum": ["create", "read", "delete"]}, "name": {"type": "string"}, "path": {"type": "string"}, "contents": {"type": "string"}, "script_type": {"type": "string"}, "namespace": {"type": "string"}}, "required": ["action", "name", "path"], "additionalProperties": False},
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_script_capabilities",
        "description": "Get supported script edit operations, limits, and guards.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "debug_request_context",
        "description": "Return the current MCP request context.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": True},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "set_active_instance",
        "description": "Select the active Unity instance for this server.",
        "inputSchema": {"type": "object", "properties": {"instance": {"type": "string"}}, "required": ["instance"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
]


TOOLS += [
    {
        "name": "script_apply_edits",
        "description": "Apply structured method, class, and anchor edits to a C# script.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "path": {"type": "string"},
                "edits": {},
                "options": {"type": "object"},
                "script_type": {"type": "string"},
                "namespace": {"type": ["string", "null"]},
            },
            "required": ["name", "path", "edits"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "unity_reflect",
        "description": "Inspect the live Unity 2019 C# API through reflection.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["get_type", "get_member", "search"]},
                "class_name": {"type": "string"},
                "member_name": {"type": "string"},
                "query": {"type": "string"},
                "scope": {"type": "string", "enum": ["unity", "packages", "project", "all"]},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "unity_docs",
        "description": "Fetch official Unity ScriptReference, Manual, and package documentation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"},
                "class_name": {"type": "string"},
                "member_name": {"type": "string"},
                "version": {"type": "string"},
                "slug": {"type": "string"},
                "package": {"type": "string"},
                "page": {"type": "string"},
                "pkg_version": {"type": "string"},
                "query": {"type": "string"},
                "queries": {"type": "string"},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": READ_ONLY_ANNOTATIONS,
    },
    {
        "name": "manage_tools",
        "description": "Manage which CoplayDev tool groups are visible in this MCP session.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["list_groups", "activate", "deactivate", "sync", "reset"]},
                "group": {"type": "string"},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "execute_code",
        "description": "Execute an in-memory C# method body in the Unity Editor with history and replay.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["execute", "get_history", "replay", "clear_history"]},
                "code": {"type": "string"},
                "safety_checks": {"type": "boolean"},
                "index": {"type": "integer"},
                "limit": {"type": "integer"},
                "compiler": {"type": "string", "enum": ["auto", "roslyn", "codedom"]},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "execute_custom_tool",
        "description": "Execute a project-scoped custom tool registered in Unity.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "tool_name": {"type": "string"},
                "parameters": {"type": "object"},
            },
            "required": ["tool_name"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_shader",
        "description": "Create, read, update, or delete Unity shader source assets.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "read", "update", "delete"]},
                "name": {"type": "string"},
                "path": {"type": "string"},
                "contents": {"type": "string"},
            },
            "required": ["action", "name", "path"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_asset",
        "description": "Import, create, modify, delete, duplicate, move, search, and inspect Unity assets.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["import", "create", "modify", "delete", "duplicate", "move", "rename", "search", "get_info", "create_folder", "get_components"]}, "path": {"type": "string"},
                "asset_type": {"type": "string"}, "properties": {},
                "destination": {"type": "string"}, "generate_preview": {"type": "boolean"},
                "search_pattern": {"type": "string"}, "filter_type": {"type": "string"},
                "filter_date_after": {"type": "string"}, "page_size": {}, "page_number": {}
            },
            "required": ["action", "path"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_material",
        "description": "Create, inspect, configure, and assign Unity materials.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["ping", "create", "set_material_shader_property", "set_material_color", "assign_material_to_renderer", "set_renderer_color", "get_material_info"]}, "material_path": {"type": "string"},
                "property": {"type": "string"}, "shader": {"type": "string"},
                "properties": {}, "value": {}, "color": {}, "target": {},
                "search_method": {"type": "string"}, "slot": {"type": "integer"},
                "mode": {"type": "string"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_texture",
        "description": "Create and modify procedural textures, sprites, noise, gradients, patterns, and importer settings.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "modify", "delete", "create_sprite", "apply_pattern", "apply_gradient", "apply_noise", "set_import_settings"]}, "path": {"type": "string"},
                "width": {"type": "integer"}, "height": {"type": "integer"},
                "fill_color": {}, "pattern": {"type": "string"}, "palette": {},
                "pattern_size": {"type": "integer"}, "pixels": {}, "image_path": {"type": "string"},
                "gradient_type": {"type": "string"}, "gradient_angle": {"type": "number"},
                "noise_scale": {"type": "number"}, "octaves": {"type": "integer"},
                "set_pixels": {"type": "object"}, "as_sprite": {}, "import_settings": {"type": "object"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_scriptable_object",
        "description": "Create and patch ScriptableObject assets through Unity SerializedObject paths.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "modify"]},
                "type_name": {"type": "string"}, "folder_path": {"type": "string"},
                "asset_name": {"type": "string"}, "overwrite": {}, "target": {},
                "patches": {}, "dry_run": {}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_prefabs",
        "description": "Inspect, create, edit, and control Unity Prefab assets and Prefab Stage.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create_from_gameobject", "get_info", "get_hierarchy", "modify_contents", "open_prefab_stage", "save_prefab_stage", "close_prefab_stage"]}, "prefab_path": {"type": "string"},
                "target": {}, "allow_overwrite": {"type": "boolean"},
                "search_inactive": {"type": "boolean"}, "unlink_if_instance": {"type": "boolean"},
                "position": {}, "rotation": {}, "scale": {}, "name": {"type": "string"},
                "tag": {"type": "string"}, "layer": {"type": "string"}, "set_active": {"type": "boolean"},
                "parent": {"type": "string"}, "components_to_add": {"type": "array"},
                "components_to_remove": {"type": "array"}, "create_child": {}, "delete_child": {},
                "component_properties": {"type": "object"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_animation",
        "description": (
            "Manage Animator runtime controls, AnimatorController assets, and "
            "AnimationClip assets. Action-specific parameters belong in properties."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"},
                "target": {},
                "search_method": {
                    "type": "string",
                    "enum": ["by_id", "by_name", "by_path", "by_tag", "by_layer"],
                },
                "clip_path": {"type": "string"},
                "controller_path": {"type": "string"},
                "properties": {},
            },
            "required": ["action"],
            "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_packages",
        "description": (
            "List, search, inspect, add, remove, embed, and resolve Unity packages, "
            "and manage scoped registries. Long operations return a job_id for status polling."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "package": {"type": "string"},
                "force": {"type": "boolean"}, "query": {"type": "string"},
                "job_id": {"type": "string"}, "name": {"type": "string"},
                "url": {"type": "string"},
                "scopes": {"type": "array", "items": {"type": "string"}},
            },
            "required": ["action"], "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_build",
        "description": (
            "Build players, poll build jobs, inspect or switch platforms, edit PlayerSettings, "
            "manage build scenes, and run batch builds. Build Profiles require Unity 6 and are unavailable in 2019."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "target": {"type": "string"},
                "output_path": {"type": "string"}, "scenes": {},
                "development": {}, "options": {}, "subtarget": {"type": "string"},
                "scripting_backend": {"type": "string"}, "profile": {"type": "string"},
                "property": {"type": "string"}, "value": {"type": "string"},
                "activate": {}, "targets": {}, "profiles": {},
                "output_dir": {"type": "string"}, "job_id": {"type": "string"},
            },
            "required": ["action"], "additionalProperties": False,
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_camera",
        "description": "Manage Unity Camera and optional Cinemachine cameras, including screenshots.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "target": {}, "search_method": {"type": "string"},
                "properties": {}, "screenshot_file_name": {"type": "string"},
                "screenshot_super_size": {}, "camera": {}, "include_image": {},
                "max_resolution": {}, "capture_source": {"type": "string"}, "batch": {"type": "string"},
                "view_target": {}, "view_position": {}, "view_rotation": {},
                "orbit_angles": {}, "orbit_elevations": {}, "orbit_distance": {},
                "orbit_fov": {}, "output_folder": {"type": "string"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_physics",
        "description": "Manage Unity 3D/2D physics settings, layers, materials, joints, queries, forces, rigidbodies, and validation.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "dimension": {"type": "string"}, "settings": {},
                "layer_a": {}, "layer_b": {}, "collide": {}, "name": {}, "path": {},
                "dynamic_friction": {}, "static_friction": {}, "bounciness": {}, "friction": {},
                "friction_combine": {}, "bounce_combine": {}, "material_path": {}, "target": {},
                "collider_type": {}, "search_method": {}, "joint_type": {}, "connected_body": {},
                "motor": {}, "limits": {}, "spring": {}, "drive": {}, "properties": {},
                "origin": {}, "direction": {}, "max_distance": {}, "layer_mask": {},
                "query_trigger_interaction": {}, "shape": {}, "position": {}, "size": {},
                "start": {}, "end": {}, "point1": {}, "point2": {}, "height": {},
                "capsule_direction": {}, "angle": {}, "force": {}, "force_mode": {},
                "force_type": {}, "torque": {}, "explosion_position": {}, "explosion_radius": {},
                "explosion_force": {}, "upwards_modifier": {}, "steps": {}, "step_size": {},
                "page_size": {}, "cursor": {}, "component_index": {}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_graphics",
        "description": (
            "Manage rendering volumes, light baking, rendering statistics, render-pipeline "
            "settings, URP renderer features, and skybox/environment settings."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "target": {}, "effect": {"type": "string"},
                "parameters": {}, "properties": {}, "settings": {}, "name": {"type": "string"},
                "is_global": {}, "weight": {}, "priority": {}, "profile_path": {"type": "string"},
                "effects": {}, "path": {"type": "string"}, "level": {"type": "string"},
                "position": {}, "grid_size": {}, "spacing": {}, "size": {}, "resolution": {},
                "mode": {"type": "string"}, "hdr": {}, "box_projection": {}, "positions": {},
                "index": {}, "active": {}, "order": {}, "async_bake": {},
                "feature_type": {"type": "string"}, "material": {"type": "string"},
                "color": {}, "intensity": {}, "ambient_mode": {"type": "string"},
                "equator_color": {}, "ground_color": {}, "fog_enabled": {},
                "fog_mode": {"type": "string"}, "fog_color": {}, "fog_density": {},
                "fog_start": {}, "fog_end": {}, "bounces": {},
                "reflection_mode": {"type": "string"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_profiler",
        "description": (
            "Control the Unity Profiler, read frame/counter/object memory data, manage "
            "memory snapshots, and inspect the Frame Debugger."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "category": {"type": "string"},
                "counters": {"type": "array", "items": {"type": "string"}},
                "object_path": {"type": "string"}, "log_file": {"type": "string"},
                "enable_callstacks": {"type": "boolean"}, "areas": {"type": "object"},
                "snapshot_path": {"type": "string"}, "search_path": {"type": "string"},
                "snapshot_a": {"type": "string"}, "snapshot_b": {"type": "string"},
                "page_size": {"type": "integer"}, "cursor": {"type": "integer"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "manage_ui",
        "description": (
            "Manage UI Toolkit UXML/USS assets, UIDocument and PanelSettings objects, "
            "visual trees, live visual elements, and UI captures."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["ping", "create", "read", "update", "delete", "attach_ui_document", "detach_ui_document", "create_panel_settings", "update_panel_settings", "get_visual_tree", "render_ui", "link_stylesheet", "list", "modify_visual_element"]}, "path": {"type": "string"},
                "contents": {"type": "string"}, "target": {"type": "string"},
                "source_asset": {"type": "string"}, "panel_settings": {"type": "string"},
                "sort_order": {"type": "integer"}, "scale_mode": {"type": "string"},
                "reference_resolution": {"type": "object"}, "settings": {"type": "object"},
                "max_depth": {"type": "integer"}, "width": {"type": "integer"},
                "height": {"type": "integer"}, "include_image": {"type": "boolean"},
                "max_resolution": {"type": "integer"}, "screenshot_file_name": {"type": "string"},
                "output_folder": {"type": "string"}, "stylesheet": {"type": "string"},
                "filter_type": {"type": "string"}, "page_size": {"type": "integer"},
                "page_number": {"type": "integer"}, "element_name": {"type": "string"},
                "text": {"type": "string"}, "add_classes": {"type": "array", "items": {"type": "string"}},
                "remove_classes": {"type": "array", "items": {"type": "string"}},
                "toggle_classes": {"type": "array", "items": {"type": "string"}},
                "style": {"type": "object"}, "enabled": {"type": "boolean"},
                "visible": {"type": "boolean"}, "tooltip": {"type": "string"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_vfx",
        "description": (
            "Manage ParticleSystem, optional Visual Effect Graph, LineRenderer, and "
            "TrailRenderer components. Action-specific values belong in properties."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "target": {},
                "search_method": {"type": "string"}, "properties": {},
                "component_index": {"type": "integer"}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "manage_probuilder",
        "description": (
            "Manage ProBuilder shapes, meshes, vertices, faces, UVs, smoothing, selection, "
            "and mesh utilities. Requires com.unity.probuilder."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "action": {"type": "string"}, "target": {},
                "search_method": {"type": "string"}, "properties": {}
            },
            "required": ["action"], "additionalProperties": False
        },
        "annotations": DESTRUCTIVE_ANNOTATIONS,
    },
    {
        "name": "generate_image",
        "description": "Generate images through configured fal.ai or OpenRouter providers and import them into Unity.",
        "inputSchema": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["generate", "remove_background", "status", "cancel", "list_providers"]}, "provider": {"type": "string"}, "mode": {"type": "string"},
            "prompt": {"type": "string"}, "image_path": {"type": "string"}, "image_url": {"type": "string"},
            "model": {"type": "string"}, "transparent": {"type": "boolean"}, "width": {"type": "integer"},
            "height": {"type": "integer"}, "name": {"type": "string"}, "output_folder": {"type": "string"},
            "job_id": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "generate_audio",
        "description": "Generate sound effects or music through a configured fal.ai provider and import the AudioClip.",
        "inputSchema": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["generate", "status", "cancel", "list_providers"]}, "provider": {"type": "string"}, "prompt": {"type": "string"},
            "model": {"type": "string"}, "duration": {"type": "number"}, "name": {"type": "string"},
            "output_folder": {"type": "string"}, "job_id": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "generate_model",
        "description": "Generate 3D models through configured Tripo or Meshy providers and import them into Unity.",
        "inputSchema": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["generate", "status", "cancel", "list_providers"]}, "provider": {"type": "string"}, "mode": {"type": "string"},
            "prompt": {"type": "string"}, "image_path": {"type": "string"}, "image_url": {"type": "string"},
            "format": {"type": "string"}, "target_size": {"type": "number"}, "texture": {"type": "boolean"},
            "tier": {"type": "string"}, "model": {"type": "string"}, "name": {"type": "string"},
            "output_folder": {"type": "string"}, "job_id": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "import_model",
        "description": "Search, preview, and import downloadable Sketchfab models with a configured provider token.",
        "inputSchema": {"type": "object", "properties": {
            "action": {"type": "string", "enum": ["search", "preview", "import", "status", "cancel", "list_providers"]}, "query": {"type": "string"}, "categories": {"type": "string"},
            "downloadable": {"type": "boolean"}, "count": {"type": "integer"}, "cursor": {"type": "string"},
            "uid": {"type": "string"}, "target_size": {"type": "number"}, "name": {"type": "string"},
            "output_folder": {"type": "string"}, "job_id": {"type": "string"}
        }, "required": ["action"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "import_model_file",
        "description": "Copy and import a local FBX, OBJ, GLB, glTF, or safe ZIP model bundle into the Unity project.",
        "inputSchema": {"type": "object", "properties": {
            "source_path": {"type": "string"}, "name": {"type": "string"},
            "output_folder": {"type": "string"}, "target_size": {"type": "number"},
            "animation_type": {"type": "string", "enum": ["none", "generic", "humanoid", "legacy"]}
        }, "required": ["source_path"], "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "run_tests",
        "description": "Start an asynchronous Unity EditMode or PlayMode test run.",
        "inputSchema": {"type": "object", "properties": {
            "mode": {"type": "string", "enum": ["EditMode", "PlayMode"]},
            "test_names": {}, "group_names": {}, "category_names": {}, "assembly_names": {},
            "include_failed_tests": {"type": "boolean"}, "include_details": {"type": "boolean"},
            "init_timeout": {"type": "integer"}, "clear_stuck": {"type": "boolean"}
        }, "additionalProperties": False},
        "annotations": ACTION_ANNOTATIONS,
    },
    {
        "name": "get_test_job",
        "description": "Poll an asynchronous Unity test job.",
        "inputSchema": {"type": "object", "properties": {
            "job_id": {"type": "string"}, "include_failed_tests": {"type": "boolean"},
            "include_details": {"type": "boolean"}, "wait_timeout": {"type": "integer"}
        }, "required": ["job_id"], "additionalProperties": False},
        "annotations": READ_ONLY_ANNOTATIONS,
    },
]


RESOURCES: list[dict[str, Any]] = [
    {
        "uri": "unity2019://editor/state",
        "name": "editor_state",
        "title": "Unity 2019 Editor State",
        "description": "Canonical readiness and active-scene snapshot.",
        "mimeType": "application/json",
    },
    {
        "uri": "mcpforunity://editor/state",
        "name": "editor_state_upstream",
        "title": "Unity Editor State",
        "description": "CoplayDev-compatible alias for the canonical editor state.",
        "mimeType": "application/json",
    },
]

RESOURCE_TEMPLATES: list[dict[str, Any]] = [
    {
        "uriTemplate": "unity2019://scene/gameobject/{instance_id}",
        "name": "gameobject",
        "title": "Unity 2019 Scene GameObject",
        "description": (
            "Detailed loaded-scene GameObject state by instance ID, including transform, "
            "parent/child IDs, and component types."
        ),
        "mimeType": "application/json",
    },
    {
        "uriTemplate": "mcpforunity://scene/gameobject/{instance_id}",
        "name": "gameobject_upstream",
        "title": "Scene GameObject",
        "description": "CoplayDev-compatible loaded-scene GameObject detail.",
        "mimeType": "application/json",
    },
]

RESOURCES += [
    {"uri": "mcpforunity://custom-tools", "name": "custom_tools", "title": "Custom Tools", "description": "Project-scoped custom MCP tools.", "mimeType": "application/json"},
    {"uri": "mcpforunity://editor/active-tool", "name": "editor_active_tool", "title": "Active Editor Tool", "description": "Currently active Unity transform tool.", "mimeType": "application/json"},
    {"uri": "mcpforunity://editor/prefab-stage", "name": "editor_prefab_stage", "title": "Prefab Stage", "description": "Current Prefab isolation stage.", "mimeType": "application/json"},
    {"uri": "mcpforunity://editor/selection", "name": "editor_selection", "title": "Editor Selection", "description": "Currently selected Unity objects.", "mimeType": "application/json"},
    {"uri": "mcpforunity://editor/windows", "name": "editor_windows", "title": "Editor Windows", "description": "Open Unity Editor windows.", "mimeType": "application/json"},
    {"uri": "mcpforunity://instances", "name": "unity_instances", "title": "Unity Instances", "description": "Available Unity instances for this server.", "mimeType": "application/json"},
    {"uri": "mcpforunity://menu-items", "name": "menu_items", "title": "Unity Menu Items", "description": "Executable Unity menu paths.", "mimeType": "application/json"},
    {"uri": "mcpforunity://pipeline/renderer-features", "name": "renderer_features", "title": "Renderer Features", "description": "Active render pipeline and renderer features.", "mimeType": "application/json"},
    {"uri": "mcpforunity://prefab-api", "name": "prefab_api", "title": "Prefab API", "description": "Prefab management capability summary.", "mimeType": "application/json"},
    {"uri": "mcpforunity://project/info", "name": "project_info", "title": "Project Info", "description": "Unity project and runtime metadata.", "mimeType": "application/json"},
    {"uri": "mcpforunity://project/layers", "name": "project_layers", "title": "Project Layers", "description": "Configured Unity layers.", "mimeType": "application/json"},
    {"uri": "mcpforunity://project/tags", "name": "project_tags", "title": "Project Tags", "description": "Configured Unity tags.", "mimeType": "application/json"},
    {"uri": "mcpforunity://rendering/stats", "name": "rendering_stats", "title": "Rendering Stats", "description": "Current Unity rendering counters.", "mimeType": "application/json"},
    {"uri": "mcpforunity://scene/cameras", "name": "cameras", "title": "Scene Cameras", "description": "Cameras in loaded scenes.", "mimeType": "application/json"},
    {"uri": "mcpforunity://scene/gameobject-api", "name": "gameobject_api", "title": "GameObject API", "description": "GameObject management capability summary.", "mimeType": "application/json"},
    {"uri": "mcpforunity://scene/volumes", "name": "volumes", "title": "Scene Volumes", "description": "Volume-like components in loaded scenes.", "mimeType": "application/json"},
    {"uri": "mcpforunity://tests", "name": "get_tests", "title": "Unity Tests", "description": "Discovered EditMode and PlayMode tests.", "mimeType": "application/json"},
    {"uri": "mcpforunity://tool-groups", "name": "tool_groups", "title": "Tool Groups", "description": "Known MCP tool groups and availability.", "mimeType": "application/json"},
]

RESOURCE_TEMPLATES += [
    {"uriTemplate": "mcpforunity://prefab/{encoded_path}", "name": "prefab_info", "title": "Prefab Info", "description": "Prefab asset metadata by encoded Assets path.", "mimeType": "application/json"},
    {"uriTemplate": "mcpforunity://prefab/{encoded_path}/hierarchy", "name": "prefab_hierarchy", "title": "Prefab Hierarchy", "description": "Prefab asset hierarchy by encoded Assets path.", "mimeType": "application/json"},
    {"uriTemplate": "mcpforunity://scene/gameobject/{instance_id}/component/{component_name}", "name": "gameobject_component", "title": "GameObject Component", "description": "Serialized component detail on a loaded GameObject.", "mimeType": "application/json"},
    {"uriTemplate": "mcpforunity://scene/gameobject/{instance_id}/components", "name": "gameobject_components", "title": "GameObject Components", "description": "Component list on a loaded GameObject.", "mimeType": "application/json"},
    {"uriTemplate": "mcpforunity://tests/{mode}", "name": "get_tests_for_mode", "title": "Unity Tests By Mode", "description": "Discovered tests for editmode or playmode.", "mimeType": "application/json"},
]


class BridgeError(RuntimeError):
    """Raised for connection, authentication, or Unity-side command failures."""


class BridgeCancelled(BridgeError):
    """Raised when the MCP client cancels a pending bridge request."""


class UnityDocumentationParser(HTMLParser):
    """Small dependency-free extractor for Unity documentation pages."""

    def __init__(self) -> None:
        super().__init__()
        self.title = ""
        self._title_depth = 0
        self._ignored_depth = 0
        self._current: list[str] = []
        self.blocks: list[str] = []

    def handle_starttag(
        self, tag: str, attrs: list[tuple[str, str | None]]
    ) -> None:
        if tag in {"script", "style", "nav", "footer"}:
            self._ignored_depth += 1
            return
        if self._ignored_depth:
            return
        if tag == "title":
            self._title_depth += 1
            self._current = []
        elif tag in {"h1", "h2", "h3", "p", "pre", "li", "td"}:
            self._current = []

    def handle_endtag(self, tag: str) -> None:
        if tag in {"script", "style", "nav", "footer"} and self._ignored_depth:
            self._ignored_depth -= 1
            return
        if self._ignored_depth:
            return
        text = " ".join("".join(self._current).split())
        if tag == "title" and self._title_depth:
            self._title_depth -= 1
            if text and not self.title:
                self.title = text
        elif tag in {"h1", "h2", "h3", "p", "pre", "li", "td"}:
            if text and (not self.blocks or self.blocks[-1] != text):
                self.blocks.append(text)
        self._current = []

    def handle_data(self, data: str) -> None:
        if not self._ignored_depth:
            self._current.append(data)


class UnityBridgeClient:
    def __init__(self, project: Path, timeout: float) -> None:
        self.project = project.resolve()
        self.timeout = timeout
        self.connection_path = (
            self.project / "Library" / RUNTIME_DIRECTORY_NAME / "connection.json"
        )

    def call(
        self,
        method: str,
        arguments: dict[str, Any],
        cancelled: threading.Event | None = None,
    ) -> Any:
        metadata = self._load_connection_metadata()
        request_id = uuid.uuid4().hex
        payload = {
            "Version": 1,
            "Id": request_id,
            "Token": metadata["Token"],
            "Method": method,
            "ArgumentsJson": json.dumps(
                arguments, ensure_ascii=False, separators=(",", ":")
            ),
            "DeadlineUtcTicks": self._deadline_utc_ticks(),
        }
        encoded = (
            json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n"
        ).encode("utf-8")

        host = metadata.get("Host", "127.0.0.1")
        port = metadata.get("Port")
        if host not in {"127.0.0.1", "localhost", "::1"}:
            raise BridgeError(f"Refusing non-loopback bridge host: {host!r}")
        if not isinstance(port, int) or not 1 <= port <= 65535:
            raise BridgeError("Bridge connection metadata contains an invalid port.")

        try:
            client = socket.create_connection((host, port), timeout=self.timeout)
        except (OSError, TimeoutError) as error:
            raise BridgeError(
                "Cannot reach the Unity 2019 bridge. Open the target Unity project in "
                f"Unity and wait for compilation. Detail: {error}"
            ) from error

        try:
            with client:
                client.sendall(encoded)
                response_line = self._receive_line(client, cancelled)
        except BridgeCancelled:
            raise
        except (socket.timeout, TimeoutError) as error:
            raise BridgeError(
                "Unity bridge accepted the connection but did not answer within "
                f"{self.timeout:g} seconds. The Editor main thread is probably "
                "compiling or importing assets."
            ) from error
        except OSError as error:
            raise BridgeError(
                f"Unity bridge connection failed while reading the response: {error}"
            ) from error

        if not response_line:
            raise BridgeError("Unity bridge closed the connection without a response.")

        try:
            response = json.loads(response_line.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise BridgeError("Unity bridge returned malformed JSON.") from error

        if response.get("Id") != request_id:
            raise BridgeError("Unity bridge response id does not match the request.")
        if not response.get("Ok"):
            raise BridgeError(response.get("Error") or "Unity bridge command failed.")

        result_json = response.get("ResultJson") or "{}"
        try:
            return json.loads(result_json)
        except json.JSONDecodeError as error:
            raise BridgeError("Unity bridge result payload is malformed.") from error

    def _receive_line(
        self,
        client: socket.socket,
        cancelled: threading.Event | None,
    ) -> bytes:
        deadline = time.monotonic() + self.timeout
        received = bytearray()

        while True:
            if cancelled is not None and cancelled.is_set():
                raise BridgeCancelled("Unity bridge request was cancelled by the MCP client.")

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise socket.timeout()

            client.settimeout(min(0.25, remaining))
            try:
                chunk = client.recv(4096)
            except socket.timeout:
                continue

            if not chunk:
                return bytes(received)

            received.extend(chunk)
            newline = received.find(b"\n")
            if newline >= 0:
                return bytes(received[:newline])
            if len(received) > 2 * 1024 * 1024:
                raise BridgeError("Unity bridge response exceeded the 2 MiB safety limit.")

    def _deadline_utc_ticks(self) -> int:
        unix_epoch_ticks = 621355968000000000
        return unix_epoch_ticks + int((time.time() + self.timeout) * 10_000_000)

    def _load_connection_metadata(self) -> dict[str, Any]:
        last_error: Exception | None = None
        for _ in range(5):
            try:
                with self.connection_path.open("r", encoding="utf-8-sig") as stream:
                    metadata = json.load(stream)
                if not isinstance(metadata, dict):
                    raise ValueError("connection metadata is not an object")
                if not metadata.get("Token"):
                    raise ValueError("connection metadata has no token")
                metadata_project = Path(str(metadata.get("ProjectPath", ""))).resolve()
                if metadata_project != self.project:
                    raise ValueError(
                        "connection metadata belongs to another Unity project: "
                        f"{metadata_project}"
                    )
                return metadata
            except (OSError, ValueError, json.JSONDecodeError) as error:
                last_error = error
                time.sleep(0.05)

        raise BridgeError(
            "Unity 2019 bridge metadata is unavailable at "
            f"{self.connection_path}. Open Unity and wait for compilation. "
            f"Detail: {last_error}"
        )


class McpServer:
    def __init__(self, project: Path, timeout: float) -> None:
        self.project = project.resolve()
        self.bridge = UnityBridgeClient(self.project, timeout)
        self._request_events: dict[Any, threading.Event] = {}
        self._cancelled_before_start: set[Any] = set()
        self._request_events_lock = threading.Lock()
        self._active_tool_groups = set(ALL_TOOL_GROUPS)
        self._asset_job_lock = threading.Lock()
        self._asset_job_cancel: dict[str, threading.Event] = {}
        self._asset_job_dir = (
            self.project / "Library" / RUNTIME_DIRECTORY_NAME / "asset-gen-jobs"
        )
        self._asset_job_dir.mkdir(parents=True, exist_ok=True)
        self._asset_executor = concurrent.futures.ThreadPoolExecutor(
            max_workers=2, thread_name_prefix="mcp2019-asset-gen"
        )

    def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        request_id = message.get("id")
        method = message.get("method")
        params = message.get("params") or {}

        if not isinstance(method, str):
            return self._rpc_error(request_id, -32600, "Invalid JSON-RPC request.")

        if method == "initialize":
            requested = params.get("protocolVersion")
            protocol = (
                requested
                if requested in SUPPORTED_PROTOCOL_VERSIONS
                else LATEST_PROTOCOL_VERSION
            )
            return self._rpc_result(
                request_id,
                {
                    "protocolVersion": protocol,
                    "capabilities": {
                        "tools": {"listChanged": True},
                        "resources": {"subscribe": False, "listChanged": False},
                    },
                    "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                    "instructions": (
                        "Controls the local Unity 2019.4 Editor through a "
                        "small authenticated bridge. Read unity2019://editor/state "
                        "before mutations. Use find_gameobjects to obtain instance IDs, "
                        "then read unity2019://scene/gameobject/{instance_id}. All scene "
                        "mutations support Unity Undo and are disabled during Play Mode."
                    ),
                },
            )

        if method == "notifications/cancelled":
            self.cancel_request(params.get("requestId"))
            return None

        if method in {"notifications/initialized", "exit"}:
            return None

        if method == "ping":
            return self._rpc_result(request_id, {})

        if method == "shutdown":
            return self._rpc_result(request_id, {})

        if method == "tools/list":
            return self._rpc_result(request_id, {"tools": self._visible_tools()})

        if method == "tools/call":
            return self._call_tool(request_id, params)

        if method == "resources/list":
            return self._rpc_result(
                request_id, {"resources": self._visible_resources()}
            )

        if method == "resources/templates/list":
            return self._rpc_result(
                request_id,
                {"resourceTemplates": self._visible_resource_templates()},
            )

        if method == "resources/read":
            return self._read_resource(request_id, params)

        if request_id is None:
            return None
        return self._rpc_error(request_id, -32601, f"Method not found: {method}")

    def _call_tool(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        name = params.get("name")
        arguments = params.get("arguments") or {}

        if not isinstance(name, str):
            return self._rpc_error(request_id, -32602, "Tool name is required.")
        if not isinstance(arguments, dict):
            return self._rpc_error(request_id, -32602, "Tool arguments must be an object.")

        cancelled = self._register_request(request_id)
        try:
            result = self._execute_tool(name, arguments, cancelled)
            text_result = result
            image_blocks: list[dict[str, Any]] = []
            if isinstance(result, dict):
                data = result.get("Data") or result.get("data")
                if isinstance(data, dict):
                    records = data.get("Images") or data.get("images") or []
                    if isinstance(records, list):
                        for record in records:
                            if not isinstance(record, dict):
                                continue
                            encoded = record.get("Base64") or record.get("base64")
                            if isinstance(encoded, str) and encoded:
                                image_blocks.append({
                                    "type": "image", "data": encoded,
                                    "mimeType": record.get("MimeType") or record.get("mimeType") or "image/png",
                                })
                    if not image_blocks:
                        encoded = data.get("ImageBase64") or data.get("imageBase64")
                        if isinstance(encoded, str) and encoded:
                            image_blocks.append({
                                "type": "image", "data": encoded,
                                "mimeType": data.get("ImageMimeType") or data.get("imageMimeType") or "image/png",
                            })
                    if image_blocks:
                        text_result = json.loads(json.dumps(result, ensure_ascii=False))
                        text_data = text_result.get("Data") or text_result.get("data")
                        if isinstance(text_data, dict):
                            text_data["ImageBase64"] = ""
                            for record in text_data.get("Images") or []:
                                if isinstance(record, dict):
                                    record["Base64"] = ""
            text = json.dumps(text_result, ensure_ascii=False, indent=2, sort_keys=True)
            return self._rpc_result(
                request_id,
                {"content": [{"type": "text", "text": text}] + image_blocks, "isError": False},
            )
        except BridgeCancelled as error:
            return self._rpc_error(request_id, -32800, str(error))
        except (BridgeError, ValueError, OSError) as error:
            return self._rpc_result(
                request_id,
                {
                    "content": [{"type": "text", "text": str(error)}],
                    "isError": True,
                },
            )
        finally:
            self._unregister_request(request_id)

    def _execute_tool(
        self,
        name: str,
        arguments: dict[str, Any],
        cancelled: threading.Event,
    ) -> Any:
        local_tools = {
            "apply_text_edits",
            "batch_execute",
            "create_script",
            "debug_request_context",
            "delete_script",
            "find_in_file",
            "get_sha",
            "get_test_job",
            "generate_audio",
            "generate_image",
            "generate_model",
            "import_model",
            "import_model_file",
            "manage_script",
            "manage_script_capabilities",
            "manage_tools",
            "manage_shader",
            "manage_packages",
            "manage_build",
            "manage_scriptable_object",
            "script_apply_edits",
            "set_active_instance",
            "unity_docs",
            "validate_script",
        }
        if name in local_tools:
            return self._call_local_tool(name, arguments, cancelled)
        method, bridge_arguments = self._map_tool(name, arguments)
        try:
            result = self.bridge.call(method, bridge_arguments, cancelled)
        except BridgeError as error:
            if name not in LEGACY_TOOL_NAMES and self._is_bridge_argument_error(error):
                return {"success": False, "message": str(error)}
            raise
        if name in LEGACY_TOOL_NAMES:
            return result
        return self._normalize_upstream_tool_result(result)

    @staticmethod
    def _is_bridge_argument_error(error: BridgeError) -> bool:
        message = str(error)
        return message.startswith(
            (
                "ArgumentException:",
                "InvalidOperationException:",
                "KeyNotFoundException:",
                "NotSupportedException:",
            )
        )

    @classmethod
    def _normalize_upstream_tool_result(cls, result: Any) -> dict[str, Any]:
        """Convert the Unity 2019 JsonUtility shape to CoplayDev's MCP envelope."""
        if not isinstance(result, dict):
            return {"success": True, "data": cls._lower_camel_value(result)}

        raw = dict(result)
        success_value = raw.pop("success", raw.pop("Success", raw.pop("Ok", None)))
        message = raw.pop("message", raw.pop("Message", ""))
        explicit_data = raw.pop("data", raw.pop("Data", None))
        normalized_remainder = cls._lower_camel_value(raw)

        if explicit_data is not None:
            data = cls._lower_camel_value(explicit_data)
            if normalized_remainder:
                if isinstance(data, dict):
                    data = {**normalized_remainder, **data}
                else:
                    data = {**normalized_remainder, "value": data}
        else:
            data = normalized_remainder or None

        response: dict[str, Any] = {
            "success": True if success_value is None else bool(success_value)
        }
        if message:
            response["message"] = str(message)
        if data is not None:
            response["data"] = data
        return response

    @classmethod
    def _lower_camel_value(cls, value: Any) -> Any:
        if isinstance(value, dict):
            return {
                cls._lower_camel_key(str(key)): cls._lower_camel_value(item)
                for key, item in value.items()
            }
        if isinstance(value, list):
            return [cls._lower_camel_value(item) for item in value]
        return value

    @staticmethod
    def _lower_camel_key(key: str) -> str:
        special = {
            "InstanceId": "instanceID",
            "InstanceIds": "instanceIDs",
            "ParentInstanceId": "parentInstanceID",
            "ParentInstanceIds": "parentInstanceIDs",
            "ChildInstanceId": "childInstanceID",
            "ChildInstanceIds": "childInstanceIDs",
            "RootInstanceId": "rootInstanceID",
            "GameObjectInstanceId": "gameObjectInstanceID",
            "ComponentInstanceId": "componentInstanceID",
            "PrefabInstanceId": "prefabInstanceID",
            "JobId": "jobId",
            "UnityJobId": "unityJobId",
            "AssetPath": "assetPath",
            "ScenePath": "scenePath",
            "Sha256": "sha256",
            "Guid": "guid",
            "Uri": "uri",
            "Url": "url",
            "Utc": "utc",
            "Cpu": "cpu",
            "Gpu": "gpu",
            "Fps": "fps",
        }
        if key in special:
            return special[key]
        if not key or not key[0].isupper():
            return key
        return key[0].lower() + key[1:]

    def _call_local_tool(
        self,
        name: str,
        arguments: dict[str, Any],
        cancelled: threading.Event,
    ) -> Any:
        if name == "debug_request_context":
            return {
                "success": True,
                "data": {
                    "transport": "unity2019-compat",
                    "project": str(self.project),
                    "cancelled": cancelled.is_set(),
                },
            }
        if name == "set_active_instance":
            requested = arguments.get("instance")
            if not isinstance(requested, str) or not requested.strip():
                raise ValueError("instance must be a non-empty string.")
            accepted = {
                self.project.name.lower(),
                str(self.project).lower(),
            }
            normalized = requested.strip().lower()
            if normalized not in accepted and not normalized.startswith(
                self.project.name.lower() + "@"
            ):
                raise ValueError(
                    "This Unity 2019 server is project-isolated; the requested instance "
                    f"does not match {self.project}."
                )
            return {
                "success": True,
                "message": "Active Unity instance selected.",
                "data": {"instance": self.project.name, "project": str(self.project)},
            }
        if name == "manage_script_capabilities":
            return {
                "success": True,
                "data": {
                    "ops": [
                        "create",
                        "read",
                        "delete",
                        "apply_text_edits",
                    ],
                    "text_ops": ["range_replace"],
                    "max_edit_payload_bytes": 2 * 1024 * 1024,
                    "guards": {"assets_only": True, "sha256_precondition": True},
                },
            }
        if name == "batch_execute":
            return self._execute_batch(arguments, cancelled)
        if name == "create_script":
            path = self._resolve_project_file(arguments.get("path"), require_cs=True)
            contents = arguments.get("contents")
            if not isinstance(contents, str):
                raise ValueError("contents must be a string.")
            if path.exists():
                raise ValueError(f"Script already exists: {self._asset_path(path)}")
            self._write_text_atomic(path, contents)
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
            return self._script_result("Script created.", path, contents, refresh)
        if name == "delete_script":
            path = self._resolve_project_file(arguments.get("uri"), require_cs=True)
            if not path.is_file():
                raise ValueError(f"Script does not exist: {self._asset_path(path)}")
            path.unlink()
            meta_path = Path(str(path) + ".meta")
            if meta_path.is_file():
                meta_path.unlink()
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
            return {
                "success": True,
                "message": "Script deleted.",
                "data": {"path": self._asset_path(path), "refresh": refresh},
            }
        if name == "get_sha":
            path = self._resolve_project_file(arguments.get("uri"), require_cs=True)
            contents = self._read_project_text(path)
            stat = path.stat()
            return {
                "success": True,
                "data": {
                    "path": self._asset_path(path),
                    "sha256": self._sha256(contents),
                    "length": len(contents),
                    "last_modified_utc": stat.st_mtime,
                },
            }
        if name == "find_in_file":
            return self._find_in_file(arguments)
        if name == "apply_text_edits":
            return self._apply_text_edits(arguments, cancelled)
        if name == "validate_script":
            path = self._resolve_project_file(arguments.get("uri"), require_cs=True)
            contents = self._read_project_text(path)
            return self._validate_script_text(
                path,
                contents,
                str(arguments.get("level") or "standard"),
                bool(arguments.get("include_diagnostics", True)),
                cancelled,
            )
        if name == "manage_script":
            return self._manage_script(arguments, cancelled)
        if name == "script_apply_edits":
            return self._script_apply_edits(arguments, cancelled)
        if name == "unity_docs":
            return self._unity_docs(arguments)
        if name == "manage_tools":
            return self._manage_tools(arguments)
        if name == "manage_shader":
            return self._manage_shader(arguments, cancelled)
        if name == "manage_packages":
            return self._manage_packages(arguments, cancelled)
        if name == "manage_build":
            return self._manage_build(arguments, cancelled)
        if name == "manage_scriptable_object":
            return self._manage_scriptable_object(arguments, cancelled)
        if name == "get_test_job":
            return self._get_test_job(arguments, cancelled)
        if name in {"generate_image", "generate_audio", "generate_model", "import_model"}:
            return self._asset_generation_tool(name, arguments, cancelled)
        if name == "import_model_file":
            return self._import_model_file(arguments, cancelled)
        raise ValueError(f"Unknown local tool: {name}")

    def _execute_batch(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        commands = arguments.get("commands")
        if not isinstance(commands, list) or not commands:
            raise ValueError("commands must be a non-empty list.")
        if len(commands) > 25:
            raise ValueError("batch_execute supports at most 25 commands in Unity 2019 mode.")
        fail_fast = bool(arguments.get("fail_fast", False))
        catalog = {str(tool.get("name") or ""): tool for tool in TOOLS}

        def run_one(index: int, command: Any) -> dict[str, Any]:
            if cancelled.is_set():
                raise BridgeCancelled("Batch execution was cancelled.")
            if not isinstance(command, dict):
                return {
                    "tool": None,
                    "callSucceeded": False,
                    "error": f"Command at index {index} must be an object.",
                }
            tool_name = command.get("tool")
            params = command.get("params") or {}
            if not isinstance(tool_name, str) or not tool_name:
                return {
                    "tool": tool_name,
                    "callSucceeded": False,
                    "error": "Each command requires a non-empty tool name.",
                }
            if tool_name == "batch_execute":
                return {
                    "tool": tool_name,
                    "callSucceeded": False,
                    "error": "Nested batch_execute calls are not supported.",
                }
            if not isinstance(params, dict):
                return {
                    "tool": tool_name,
                    "callSucceeded": False,
                    "error": "Command params must be an object.",
                }
            if "unity_instance" in params:
                return {
                    "tool": tool_name,
                    "callSucceeded": False,
                    "error": "Per-command unity_instance routing is not supported.",
                }
            try:
                value = self._execute_tool(tool_name, params, cancelled)
                return {"tool": tool_name, "callSucceeded": True, "result": value}
            except BridgeCancelled:
                raise
            except (BridgeError, ValueError, OSError) as error:
                return {
                    "tool": tool_name,
                    "callSucceeded": False,
                    "error": str(error),
                }

        parallel_requested = bool(arguments.get("parallel", False))
        read_only_batch = all(
            isinstance(command, dict)
            and isinstance(command.get("tool"), str)
            and bool(
                (catalog.get(str(command.get("tool"))) or {})
                .get("annotations", {})
                .get("readOnlyHint", False)
            )
            for command in commands
        )
        parallel_applied = parallel_requested and read_only_batch and len(commands) > 1
        results: list[dict[str, Any]] = []
        if parallel_applied:
            worker_count = self._bounded_int(
                arguments.get("max_parallelism"), min(4, len(commands)), 1, 8
            )
            with concurrent.futures.ThreadPoolExecutor(
                max_workers=worker_count,
                thread_name_prefix="mcp2019-batch",
            ) as executor:
                futures = [
                    executor.submit(run_one, index, command)
                    for index, command in enumerate(commands)
                ]
                for future in futures:
                    result = future.result()
                    results.append(result)
                    if fail_fast and not result["callSucceeded"]:
                        for pending in futures[len(results) :]:
                            pending.cancel()
                        break
        else:
            for index, command in enumerate(commands):
                result = run_one(index, command)
                results.append(result)
                if fail_fast and not result["callSucceeded"]:
                    break

        success_count = sum(1 for result in results if result["callSucceeded"])
        failure_count = len(results) - success_count
        return {
            "success": failure_count == 0,
            "message": (
                "Batch execution completed."
                if failure_count == 0
                else "One or more commands failed."
            ),
            "data": {
                "results": results,
                "callSuccessCount": success_count,
                "callFailureCount": failure_count,
                "parallelRequested": parallel_requested,
                "parallelApplied": parallel_applied,
                "maxParallelism": arguments.get("max_parallelism"),
            },
        }

    def _visible_tools(self) -> list[dict[str, Any]]:
        visible: list[dict[str, Any]] = []
        for tool in TOOLS:
            name = str(tool.get("name") or "")
            if not SHOW_LEGACY_ALIASES and name in LEGACY_TOOL_NAMES:
                continue
            group = TOOL_GROUP_BY_NAME.get(name, "core")
            if group in self._active_tool_groups or name == "manage_tools":
                visible.append(tool)
        return visible

    @staticmethod
    def _visible_resources() -> list[dict[str, Any]]:
        if SHOW_LEGACY_ALIASES:
            return RESOURCES
        return [
            resource
            for resource in RESOURCES
            if not str(resource.get("uri") or "").startswith(LEGACY_RESOURCE_PREFIX)
        ]

    @staticmethod
    def _visible_resource_templates() -> list[dict[str, Any]]:
        if SHOW_LEGACY_ALIASES:
            return RESOURCE_TEMPLATES
        return [
            resource
            for resource in RESOURCE_TEMPLATES
            if not str(resource.get("uriTemplate") or "").startswith(
                LEGACY_RESOURCE_PREFIX
            )
        ]

    def _manage_tools(self, arguments: dict[str, Any]) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        group_value = arguments.get("group")
        group = str(group_value or "").lower()
        if action in {"activate", "deactivate"}:
            if group not in ALL_TOOL_GROUPS:
                raise ValueError(
                    "group must be one of: " + ", ".join(sorted(ALL_TOOL_GROUPS))
                )
            if group == "core" and action == "deactivate":
                raise ValueError("The core tool group cannot be deactivated.")
            if action == "activate":
                self._active_tool_groups.add(group)
            else:
                self._active_tool_groups.discard(group)
        elif action in {"reset", "sync"}:
            self._active_tool_groups = set(ALL_TOOL_GROUPS)
        elif action != "list_groups":
            raise ValueError(
                "manage_tools action must be list_groups, activate, deactivate, sync, or reset."
            )
        groups = []
        for name in sorted(ALL_TOOL_GROUPS):
            registered_count = sum(
                1
                for tool in TOOLS
                if TOOL_GROUP_BY_NAME.get(str(tool.get("name") or ""), "core") == name
            )
            groups.append(
                {
                    "name": name,
                    "active": name in self._active_tool_groups,
                    "registeredToolCount": registered_count,
                }
            )
        return {
            "success": True,
            "message": "Tool group visibility updated." if action != "list_groups" else "Tool groups listed.",
            "data": {"groups": groups, "visibleToolCount": len(self._visible_tools())},
        }

    def _manage_script(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        name = arguments.get("name")
        directory = arguments.get("path")
        if not isinstance(name, str) or not name.strip():
            raise ValueError("name must be a non-empty string.")
        if not isinstance(directory, str):
            raise ValueError("path must be a string.")
        file_name = name if name.lower().endswith(".cs") else name + ".cs"
        normalized_directory = directory.replace("\\", "/").strip("/")
        if not normalized_directory.lower().startswith("assets"):
            normalized_directory = "Assets/" + normalized_directory
        uri = normalized_directory.rstrip("/") + "/" + file_name
        path = self._resolve_project_file(uri, require_cs=True)
        if action == "read":
            contents = self._read_project_text(path)
            return self._script_result("Script read.", path, contents, None)
        if action == "create":
            if path.exists():
                raise ValueError(f"Script already exists: {self._asset_path(path)}")
            contents = arguments.get("contents")
            if not isinstance(contents, str) or not contents:
                class_name = Path(file_name).stem
                namespace = arguments.get("namespace")
                body = (
                    "using UnityEngine;\n\npublic class "
                    + class_name
                    + " : MonoBehaviour\n{\n}\n"
                )
                if isinstance(namespace, str) and namespace.strip():
                    body = (
                        "using UnityEngine;\n\nnamespace "
                        + namespace.strip()
                        + "\n{\n    public class "
                        + class_name
                        + " : MonoBehaviour\n    {\n    }\n}\n"
                    )
                contents = body
            self._write_text_atomic(path, contents)
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
            return self._script_result("Script created.", path, contents, refresh)
        if action == "delete":
            return self._call_local_tool(
                "delete_script", {"uri": self._asset_path(path)}, cancelled
            )
        raise ValueError("manage_script action must be create, read, or delete.")

    def _manage_shader(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        name = arguments.get("name")
        directory = arguments.get("path")
        if not isinstance(name, str) or not name.strip():
            raise ValueError("name must be a non-empty string.")
        if not isinstance(directory, str):
            raise ValueError("path must be a string.")
        file_name = name.strip()
        if not file_name.lower().endswith(".shader"):
            file_name += ".shader"
        normalized = directory.replace("\\", "/").strip("/")
        if not normalized.lower().startswith("assets"):
            normalized = "Assets/" + normalized
        path = self._resolve_project_file(
            normalized.rstrip("/") + "/" + file_name, require_cs=False
        )
        if path.suffix.lower() != ".shader":
            raise ValueError("Shader file must use the .shader extension.")
        if action == "read":
            contents = self._read_project_text(path)
            return self._script_result("Shader read.", path, contents, None)
        if action in {"create", "update"}:
            contents = arguments.get("contents")
            if not isinstance(contents, str) or not contents.strip():
                raise ValueError(f"{action} requires non-empty shader contents.")
            if action == "create" and path.exists():
                raise ValueError(f"Shader already exists: {self._asset_path(path)}")
            if action == "update" and not path.is_file():
                raise ValueError(f"Shader does not exist: {self._asset_path(path)}")
            if not re.search(r"\bShader\s+\"[^\"]+\"", contents):
                raise ValueError("Shader source must declare Shader \"Name\".")
            validation = self._validate_script_text(path, contents, "basic", True)
            if not validation["success"]:
                raise ValueError("Shader source failed structural validation.")
            self._write_text_atomic(path, contents)
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
            return self._script_result(
                "Shader created." if action == "create" else "Shader updated.",
                path,
                contents,
                refresh,
            )
        if action == "delete":
            if not path.is_file():
                raise ValueError(f"Shader does not exist: {self._asset_path(path)}")
            path.unlink()
            meta = Path(str(path) + ".meta")
            if meta.is_file():
                meta.unlink()
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
            return {
                "success": True,
                "message": "Shader deleted.",
                "data": {"path": self._asset_path(path), "refresh": refresh},
            }
        raise ValueError("manage_shader action must be create, read, update, or delete.")

    def _manage_scriptable_object(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        if action == "create":
            type_name = arguments.get("type_name")
            folder = arguments.get("folder_path")
            asset_name = arguments.get("asset_name")
            if not isinstance(type_name, str) or not type_name.strip():
                raise ValueError("create requires type_name.")
            if not isinstance(folder, str) or not folder.strip():
                raise ValueError("create requires folder_path.")
            if not isinstance(asset_name, str) or not asset_name.strip():
                raise ValueError("create requires asset_name.")
            normalized_folder = folder.replace("\\", "/").strip("/")
            if not normalized_folder.lower().startswith("assets"):
                normalized_folder = "Assets/" + normalized_folder
            file_name = asset_name.strip()
            if not file_name.lower().endswith(".asset"):
                file_name += ".asset"
            asset_path = normalized_folder.rstrip("/") + "/" + file_name
            resolved = self._resolve_project_file(asset_path, require_cs=False)
            overwrite = self._as_bool(arguments.get("overwrite"), False)
            if resolved.exists() and not overwrite:
                raise ValueError(f"ScriptableObject asset already exists: {asset_path}")
            if resolved.exists() and overwrite:
                deleted = self.bridge.call(
                    "manage_asset",
                    {"Action": "delete", "Path": asset_path},
                    cancelled,
                )
                if not deleted.get("Success", False):
                    raise ValueError("Unity failed to overwrite the existing asset.")
            return self.bridge.call(
                "manage_asset",
                {
                    "Action": "create",
                    "Path": asset_path,
                    "AssetType": type_name.strip(),
                    "Properties": [],
                },
                cancelled,
            )
        if action == "modify":
            target = arguments.get("target")
            if isinstance(target, str):
                asset_path = target
            elif isinstance(target, dict):
                asset_path = target.get("path") or ""
                if not asset_path and target.get("guid"):
                    guid = str(target["guid"])
                    lookup = self.bridge.call(
                        "manage_asset",
                        {
                            "Action": "resolve_guid",
                            "Path": guid,
                        },
                        cancelled,
                    )
                    asset_path = lookup.get("Path") or ""
            else:
                asset_path = ""
            if not isinstance(asset_path, str) or not asset_path.strip():
                raise ValueError("modify target must provide an asset path or resolvable guid.")
            patches_value = arguments.get("patches")
            if isinstance(patches_value, str):
                try:
                    patches_value = json.loads(patches_value)
                except json.JSONDecodeError as error:
                    raise ValueError("patches must contain valid JSON.") from error
            if not isinstance(patches_value, list) or not patches_value:
                raise ValueError("modify requires a non-empty patches list.")
            properties: dict[str, Any] = {}
            for index, patch in enumerate(patches_value):
                if not isinstance(patch, dict):
                    raise ValueError(f"Patch {index} must be an object.")
                operation = str(patch.get("op") or "set").lower()
                if operation not in {"set", "replace"}:
                    raise ValueError(
                        "Unity 2019 ScriptableObject patches support op=set or replace."
                    )
                path = patch.get("path") or patch.get("property_path")
                if not isinstance(path, str) or not path:
                    raise ValueError(f"Patch {index} requires path.")
                value = patch.get("value")
                if isinstance(value, dict) and "ref" in value:
                    value = value["ref"]
                if "ref" in patch:
                    value = patch["ref"]
                properties[path] = value
            encoded = self._serialized_patches(properties)
            if self._as_bool(arguments.get("dry_run"), False):
                return {
                    "success": True,
                    "message": "ScriptableObject patches validated without applying.",
                    "data": {"path": asset_path, "patches": encoded},
                }
            return self.bridge.call(
                "manage_asset",
                {"Action": "modify", "Path": asset_path, "Properties": encoded},
                cancelled,
            )
        raise ValueError("manage_scriptable_object action must be create or modify.")

    def _get_test_job(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        job_id = arguments.get("job_id")
        if not isinstance(job_id, str) or not job_id.strip():
            raise ValueError("job_id must be a non-empty string.")
        wait_timeout = self._bounded_int(arguments.get("wait_timeout"), 0, 0, 60)
        deadline = time.monotonic() + wait_timeout
        while True:
            result = self.bridge.call(
                "get_test_job",
                {
                    "JobId": job_id.strip(),
                    "IncludeFailedTests": self._as_bool(
                        arguments.get("include_failed_tests"), False
                    ),
                    "IncludeDetails": self._as_bool(
                        arguments.get("include_details"), False
                    ),
                },
                cancelled,
            )
            if str(result.get("Status") or "").lower() not in {"queued", "running"}:
                return result
            if time.monotonic() >= deadline:
                return result
            if cancelled.wait(min(0.5, max(0.0, deadline - time.monotonic()))):
                raise BridgeCancelled("Test job polling was cancelled.")

    def _asset_generation_tool(
        self,
        tool_name: str,
        arguments: dict[str, Any],
        cancelled: threading.Event,
    ) -> dict[str, Any]:
        valid_actions = {
            "generate_image": {"generate", "remove_background", "status", "cancel", "list_providers"},
            "generate_audio": {"generate", "status", "cancel", "list_providers"},
            "generate_model": {"generate", "status", "cancel", "list_providers"},
            "import_model": {"search", "preview", "import", "status", "cancel", "list_providers"},
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in valid_actions[tool_name]:
            return {"success": False, "message": f"Unknown {tool_name} action: {action}"}
        if action == "remove_background":
            return {
                "success": False,
                "message": "remove_background is unsupported in this version; no job was created.",
            }
        if action == "list_providers":
            return self._asset_provider_list(tool_name)
        if action == "status":
            return self._asset_job_status(arguments.get("job_id"))
        if action == "cancel":
            return self._asset_job_cancel_request(arguments.get("job_id"))

        provider = self._asset_provider_for(tool_name, arguments.get("provider"))
        key = self._asset_provider_key(provider)
        if not key:
            return {
                "success": False,
                "message": (
                    f"Provider '{provider}' is not configured. Set "
                    f"{self._asset_provider_env_name(provider)} in the MCP server environment; "
                    "the key value is never returned through MCP."
                ),
                "data": {"provider": provider, "configured": False},
            }

        if tool_name == "import_model" and action in {"search", "preview"}:
            try:
                return self._sketchfab_read(action, arguments, key, cancelled)
            except (OSError, ValueError) as error:
                return {"success": False, "message": self._redact(str(error), key)}

        prompt = arguments.get("prompt")
        mode = str(arguments.get("mode") or "text").lower()
        if tool_name in {"generate_image", "generate_audio", "generate_model"}:
            if mode not in {"text", "image"}:
                return {"success": False, "message": "mode must be text or image."}
            has_image = bool(arguments.get("image_path") or arguments.get("image_url"))
            if mode == "image" and tool_name != "generate_audio" and not has_image:
                return {"success": False, "message": "image mode requires image_path or image_url."}
            if mode == "text" and (not isinstance(prompt, str) or not prompt.strip()):
                return {"success": False, "message": "prompt is required for text generation."}
        if tool_name == "import_model":
            uid = arguments.get("uid")
            if not isinstance(uid, str) or not uid.strip():
                return {"success": False, "message": "uid is required for import."}

        job_id = uuid.uuid4().hex
        record = {
            "job_id": job_id,
            "tool": tool_name,
            "provider": provider,
            "state": "queued",
            "progress": 0.0,
            "created_utc": time.time(),
            "updated_utc": time.time(),
            "asset_path": "",
            "asset_guid": "",
            "error": "",
        }
        self._write_asset_job(record)
        cancel_event = threading.Event()
        with self._asset_job_lock:
            self._asset_job_cancel[job_id] = cancel_event
        self._asset_executor.submit(
            self._run_asset_job,
            job_id,
            tool_name,
            provider,
            dict(arguments),
            key,
            cancel_event,
        )
        return {
            "success": True,
            "message": "Asset generation job submitted.",
            "data": {"job_id": job_id, "state": "queued", "provider": provider},
        }

    def _asset_provider_list(self, tool_name: str) -> dict[str, Any]:
        ids = {
            "generate_image": ["fal", "openrouter"],
            "generate_audio": ["fal"],
            "generate_model": ["tripo", "meshy"],
            "import_model": ["sketchfab"],
        }[tool_name]
        capabilities = {
            "fal": ["text_to_image", "image_to_image", "text_to_audio"],
            "openrouter": ["text_to_image", "image_to_image"],
            "tripo": ["text_to_3d", "hosted_image_to_3d"],
            "meshy": ["text_to_3d", "image_to_3d"],
            "sketchfab": ["search", "preview", "download"],
        }
        providers = [
            {
                "id": provider,
                "configured": bool(self._asset_provider_key(provider)),
                "environment_variable": self._asset_provider_env_name(provider),
                "capabilities": capabilities[provider],
            }
            for provider in ids
        ]
        return {
            "success": True,
            "message": "Asset providers listed; secret values are not exposed.",
            "data": {"providers": providers},
        }

    @staticmethod
    def _asset_provider_env_name(provider: str) -> str:
        return {
            "fal": "FAL_KEY",
            "openrouter": "OPENROUTER_API_KEY",
            "tripo": "TRIPO_API_KEY",
            "meshy": "MESHY_API_KEY",
            "sketchfab": "SKETCHFAB_API_TOKEN",
        }[provider]

    @staticmethod
    def _asset_provider_key(provider: str) -> str:
        name = McpServer._asset_provider_env_name(provider)
        environment_value = str(os.environ.get(name) or "").strip()
        if environment_value:
            return environment_value
        return McpServer._windows_credential_value(provider)

    @staticmethod
    def _windows_credential_value(provider: str) -> str:
        if sys.platform != "win32":
            return ""

        class Credential(ctypes.Structure):
            _fields_ = [
                ("Flags", ctypes.c_uint32),
                ("Type", ctypes.c_uint32),
                ("TargetName", ctypes.c_void_p),
                ("Comment", ctypes.c_void_p),
                ("LastWrittenLow", ctypes.c_uint32),
                ("LastWrittenHigh", ctypes.c_uint32),
                ("CredentialBlobSize", ctypes.c_uint32),
                ("CredentialBlob", ctypes.c_void_p),
                ("Persist", ctypes.c_uint32),
                ("AttributeCount", ctypes.c_uint32),
                ("Attributes", ctypes.c_void_p),
                ("TargetAlias", ctypes.c_void_p),
                ("UserName", ctypes.c_void_p),
            ]

        target = "UnityMcp2019.AssetGen." + provider
        credential_pointer = ctypes.c_void_p()
        advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
        advapi32.CredReadW.argtypes = [
            ctypes.c_wchar_p,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.POINTER(ctypes.c_void_p),
        ]
        advapi32.CredReadW.restype = ctypes.c_bool
        advapi32.CredFree.argtypes = [ctypes.c_void_p]
        advapi32.CredFree.restype = None
        if not advapi32.CredReadW(target, 1, 0, ctypes.byref(credential_pointer)):
            return ""
        try:
            credential = ctypes.cast(
                credential_pointer, ctypes.POINTER(Credential)
            ).contents
            if not credential.CredentialBlob or credential.CredentialBlobSize <= 0:
                return ""
            raw = ctypes.string_at(
                credential.CredentialBlob, credential.CredentialBlobSize
            )
            return raw.decode("utf-8").strip()
        except (UnicodeDecodeError, ValueError, OSError):
            return ""
        finally:
            advapi32.CredFree(credential_pointer)

    @staticmethod
    def _asset_provider_for(tool_name: str, requested: Any) -> str:
        defaults = {
            "generate_image": "fal",
            "generate_audio": "fal",
            "generate_model": "tripo",
            "import_model": "sketchfab",
        }
        allowed = {
            "generate_image": {"fal", "openrouter"},
            "generate_audio": {"fal"},
            "generate_model": {"tripo", "meshy"},
            "import_model": {"sketchfab"},
        }
        provider = str(requested or defaults[tool_name]).strip().lower()
        if provider not in allowed[tool_name]:
            raise ValueError(
                f"provider must be one of: {', '.join(sorted(allowed[tool_name]))}"
            )
        return provider

    def _asset_job_status(self, value: Any) -> dict[str, Any]:
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{32}", value):
            return {"success": False, "message": "job_id must be a valid asset job id."}
        record = self._read_asset_job(value)
        if record is None:
            return {"success": False, "message": f"Asset job was not found: {value}"}
        return {
            "success": True,
            "message": "Asset job status read.",
            "data": record,
        }

    def _asset_job_cancel_request(self, value: Any) -> dict[str, Any]:
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{32}", value):
            return {"success": False, "message": "job_id must be a valid asset job id."}
        record = self._read_asset_job(value)
        if record is None:
            return {"success": False, "message": f"Asset job was not found: {value}"}
        with self._asset_job_lock:
            event = self._asset_job_cancel.get(value)
        if event is not None:
            event.set()
        if record.get("state") not in {"succeeded", "failed", "cancelled"}:
            record["state"] = "cancelled"
            record["updated_utc"] = time.time()
            self._write_asset_job(record)
        return {
            "success": True,
            "message": "Asset job cancellation requested.",
            "data": record,
        }

    def _asset_job_path(self, job_id: str) -> Path:
        return self._asset_job_dir / (job_id + ".json")

    def _read_asset_job(self, job_id: str) -> dict[str, Any] | None:
        path = self._asset_job_path(job_id)
        try:
            value = json.loads(path.read_text(encoding="utf-8-sig"))
            return value if isinstance(value, dict) else None
        except (OSError, json.JSONDecodeError):
            return None

    def _write_asset_job(self, record: dict[str, Any]) -> None:
        path = self._asset_job_path(str(record["job_id"]))
        path.parent.mkdir(parents=True, exist_ok=True)
        record["updated_utc"] = time.time()
        with self._asset_job_lock:
            fd, temporary = tempfile.mkstemp(
                prefix=path.name + ".", suffix=".tmp", dir=str(path.parent)
            )
            try:
                with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
                    json.dump(record, stream, ensure_ascii=False, indent=2, sort_keys=True)
                os.replace(temporary, path)
            finally:
                if os.path.exists(temporary):
                    os.unlink(temporary)

    def _update_asset_job(self, job_id: str, **changes: Any) -> dict[str, Any]:
        record = self._read_asset_job(job_id) or {"job_id": job_id}
        record.update(changes)
        self._write_asset_job(record)
        return record

    def _run_asset_job(
        self,
        job_id: str,
        tool_name: str,
        provider: str,
        arguments: dict[str, Any],
        key: str,
        cancel_event: threading.Event,
    ) -> None:
        try:
            self._update_asset_job(job_id, state="running", progress=0.02)
            if tool_name == "generate_image":
                source = self._generate_image_result(
                    provider, arguments, key, job_id, cancel_event
                )
                kind = "image"
            elif tool_name == "generate_audio":
                source = self._generate_audio_result(
                    arguments, key, job_id, cancel_event
                )
                kind = "audio"
            elif tool_name == "generate_model":
                source = self._generate_model_result(
                    provider, arguments, key, job_id, cancel_event
                )
                kind = "model"
            else:
                source = self._sketchfab_import_result(
                    arguments, key, job_id, cancel_event
                )
                kind = "model"
            self._asset_cancel_check(cancel_event)
            asset_path = self._write_generated_result(kind, arguments, source)
            if kind == "model":
                asset_path = self._prepare_model_asset(asset_path)
            self._update_asset_job(
                job_id, state="importing", progress=0.92, asset_path=asset_path
            )
            bridge_arguments = {
                "Kind": kind,
                "AssetPath": asset_path,
                "Transparent": self._as_bool(arguments.get("transparent"), False),
                "TargetSize": float(arguments.get("target_size") or 1.0),
                "AnimationType": str(arguments.get("animation_type") or "none"),
            }
            imported = self.bridge.call("import_generated_asset", bridge_arguments)
            success = imported.get("Success", False) if isinstance(imported, dict) else False
            if not success:
                message = imported.get("Message") if isinstance(imported, dict) else str(imported)
                raise OSError(message or "Unity failed to import the generated asset.")
            data = imported.get("Data") or {}
            self._update_asset_job(
                job_id,
                state="succeeded",
                progress=1.0,
                asset_path=data.get("AssetPath") or asset_path,
                asset_guid=data.get("AssetGuid") or "",
                error="",
            )
        except BridgeCancelled:
            self._update_asset_job(job_id, state="cancelled", error="Cancelled.")
        except Exception as error:
            self._update_asset_job(
                job_id,
                state="cancelled" if cancel_event.is_set() else "failed",
                error=self._redact(str(error), key)[:4000],
            )
        finally:
            with self._asset_job_lock:
                self._asset_job_cancel.pop(job_id, None)

    @staticmethod
    def _asset_cancel_check(cancel_event: threading.Event) -> None:
        if cancel_event.is_set():
            raise BridgeCancelled("Asset job was cancelled.")

    def _generate_image_result(
        self,
        provider: str,
        arguments: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        if provider == "openrouter":
            model = str(arguments.get("model") or "google/gemini-2.5-flash-image")
            mode = str(arguments.get("mode") or "text").lower()
            content: Any = str(arguments.get("prompt") or "")
            if mode == "image":
                image_ref = self._provider_image_reference(arguments)
                content = [
                    {"type": "text", "text": str(arguments.get("prompt") or "")},
                    {"type": "image_url", "image_url": {"url": image_ref}},
                ]
            body = {
                "model": model,
                "modalities": ["image", "text"],
                "messages": [{"role": "user", "content": content}],
            }
            result = self._http_json(
                "https://openrouter.ai/api/v1/chat/completions",
                "POST",
                {"Authorization": "Bearer " + key},
                body,
                {"openrouter.ai"},
                key,
            )
            message = ((result.get("choices") or [{}])[0].get("message") or {})
            images = message.get("images") or []
            image_url = ""
            if images:
                first = images[0] or {}
                image_url = ((first.get("image_url") or {}).get("url") or first.get("url") or "")
            if not image_url and isinstance(message.get("content"), list):
                for part in message["content"]:
                    image_url = ((part.get("image_url") or {}).get("url") or "")
                    if image_url:
                        break
            if not image_url:
                raise OSError("OpenRouter returned no image output.")
            return {"url": image_url, "extension": ".png"}

        model = str(arguments.get("model") or "fal-ai/flux-2")
        mode = str(arguments.get("mode") or "text").lower()
        endpoint = "https://queue.fal.run/" + model
        body: dict[str, Any] = {
            "prompt": str(arguments.get("prompt") or ""),
            "num_images": 1,
        }
        if mode == "image":
            endpoint += "/edit"
            body["image_urls"] = [self._provider_image_reference(arguments)]
        elif arguments.get("width") and arguments.get("height"):
            body["image_size"] = {
                "width": int(arguments["width"]),
                "height": int(arguments["height"]),
            }
        result = self._fal_queue(endpoint, body, key, job_id, cancel_event)
        images = result.get("images") or []
        url = ((images[0] or {}).get("url") if images else "") or ((result.get("image") or {}).get("url") or "")
        if not url:
            raise OSError("fal completed but returned no image URL.")
        return {"url": url, "extension": self._url_extension(url, ".png")}

    def _generate_audio_result(
        self,
        arguments: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        model = str(arguments.get("model") or "fal-ai/stable-audio-25/text-to-audio")
        body: dict[str, Any] = {"prompt": str(arguments.get("prompt") or "")}
        if arguments.get("duration") is not None:
            duration = max(1, int(float(arguments["duration"])))
            body["seconds_total" if "stable-audio" in model else "duration"] = duration
        result = self._fal_queue(
            "https://queue.fal.run/" + model,
            body,
            key,
            job_id,
            cancel_event,
        )
        url = (
            ((result.get("audio") or {}).get("url") or "")
            or ((result.get("audio_file") or {}).get("url") or "")
            or str(result.get("audio_url") or "")
        )
        if not url:
            raise OSError("fal completed but returned no audio URL.")
        return {"url": url, "extension": self._url_extension(url, ".wav")}

    def _generate_model_result(
        self,
        provider: str,
        arguments: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        if provider == "tripo":
            endpoint = "https://api.tripo3d.ai/v2/openapi/task"
            mode = str(arguments.get("mode") or "text").lower()
            model = str(arguments.get("model") or "v3.1-20260211")
            if mode == "image":
                image_url = arguments.get("image_url")
                if not image_url:
                    raise ValueError(
                        "Tripo image input requires a hosted image_url; use Meshy for a local image_path."
                    )
                body = {
                    "type": "image_to_model",
                    "model_version": model,
                    "file": {"type": "url", "url": str(image_url)},
                }
            else:
                body = {
                    "type": "text_to_model",
                    "prompt": str(arguments.get("prompt") or ""),
                    "model_version": model,
                }
            submitted = self._http_json(
                endpoint,
                "POST",
                {"Authorization": "Bearer " + key},
                body,
                {"api.tripo3d.ai"},
                key,
            )
            task_id = str((submitted.get("data") or {}).get("task_id") or "")
            if not task_id:
                raise OSError("Tripo submit returned no task_id.")
            for _ in range(300):
                self._asset_cancel_check(cancel_event)
                status = self._http_json(
                    endpoint + "/" + task_id,
                    "GET",
                    {"Authorization": "Bearer " + key},
                    None,
                    {"api.tripo3d.ai"},
                    key,
                )
                data = status.get("data") or {}
                state = str(data.get("status") or "").lower()
                progress = min(0.88, max(0.05, float(data.get("progress") or 0) / 100.0 * 0.88))
                self._update_asset_job(job_id, state="running", progress=progress)
                if state in {"success", "succeeded"}:
                    output = data.get("output") or data.get("result") or {}
                    for field in ("pbr_model", "model", "base_model"):
                        candidate = output.get(field)
                        url = candidate.get("url") if isinstance(candidate, dict) else candidate
                        if url:
                            extension = "." + str(arguments.get("format") or self._url_extension(str(url), ".glb").lstrip("."))
                            return {"url": str(url), "extension": extension}
                    raise OSError("Tripo succeeded but returned no model URL.")
                if state in {"failed", "error", "cancelled", "canceled", "banned", "expired"}:
                    raise OSError(str(data.get("error") or data.get("message") or "Tripo task failed."))
                cancel_event.wait(2.0)
            raise TimeoutError("Tripo generation timed out after 10 minutes.")

        return self._meshy_generate(arguments, key, job_id, cancel_event)

    def _meshy_generate(
        self,
        arguments: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        text_endpoint = "https://api.meshy.ai/openapi/v2/text-to-3d"
        image_endpoint = "https://api.meshy.ai/openapi/v1/image-to-3d"
        image_mode = str(arguments.get("mode") or "text").lower() == "image"
        model = str(arguments.get("model") or "meshy-6")
        want_texture = self._as_bool(arguments.get("texture"), True)
        if image_mode:
            endpoint = image_endpoint
            body = {
                "image_url": self._provider_image_reference(arguments),
                "ai_model": model,
                "should_texture": want_texture,
            }
        else:
            endpoint = text_endpoint
            body = {
                "mode": "preview",
                "prompt": str(arguments.get("prompt") or ""),
                "ai_model": model,
            }
        task_id = self._meshy_submit(endpoint, body, key)
        refine = False
        for _ in range(300):
            self._asset_cancel_check(cancel_event)
            poll_endpoint = text_endpoint if refine or not image_mode else image_endpoint
            status = self._http_json(
                poll_endpoint + "/" + task_id,
                "GET",
                {"Authorization": "Bearer " + key},
                None,
                {"api.meshy.ai"},
                key,
            )
            state = str(status.get("status") or "").upper()
            raw = min(1.0, max(0.0, float(status.get("progress") or 0) / 100.0))
            progress = (0.5 + raw * 0.38) if refine else (raw * (0.44 if want_texture and not image_mode else 0.88))
            self._update_asset_job(job_id, state="running", progress=progress)
            if state == "SUCCEEDED":
                if want_texture and not image_mode and not refine:
                    task_id = self._meshy_submit(
                        text_endpoint,
                        {"mode": "refine", "preview_task_id": task_id, "ai_model": model},
                        key,
                    )
                    refine = True
                    continue
                urls = status.get("model_urls") or {}
                requested = str(arguments.get("format") or "glb").lower().lstrip(".")
                url = urls.get(requested) or urls.get("glb") or urls.get("fbx")
                if not url:
                    raise OSError("Meshy succeeded but returned no model URL.")
                return {"url": str(url), "extension": "." + requested}
            if state in {"FAILED", "EXPIRED", "CANCELED", "CANCELLED"}:
                detail = (status.get("task_error") or {}).get("message") or status.get("message")
                raise OSError(str(detail or "Meshy task failed."))
            cancel_event.wait(2.0)
        raise TimeoutError("Meshy generation timed out after 10 minutes.")

    def _meshy_submit(
        self, endpoint: str, body: dict[str, Any], key: str
    ) -> str:
        result = self._http_json(
            endpoint,
            "POST",
            {"Authorization": "Bearer " + key},
            body,
            {"api.meshy.ai"},
            key,
        )
        task_id = str(result.get("result") or "")
        if not task_id:
            raise OSError("Meshy submit returned no task id.")
        return task_id

    def _fal_queue(
        self,
        endpoint: str,
        body: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        submitted = self._http_json(
            endpoint,
            "POST",
            {"Authorization": "Key " + key},
            body,
            {"queue.fal.run"},
            key,
        )
        response_url = str(submitted.get("response_url") or "")
        if not response_url:
            request_id = str(submitted.get("request_id") or "")
            if not request_id:
                raise OSError("fal submit returned no request_id.")
            base_endpoint = endpoint[:-5] if endpoint.endswith("/edit") else endpoint
            response_url = base_endpoint + "/requests/" + request_id
        self._require_https_host(response_url, {"queue.fal.run"})
        for index in range(300):
            self._asset_cancel_check(cancel_event)
            status = self._http_json(
                response_url + "/status",
                "GET",
                {"Authorization": "Key " + key},
                None,
                {"queue.fal.run"},
                key,
            )
            state = str(status.get("status") or "").upper()
            self._update_asset_job(
                job_id,
                state="queued" if state == "IN_QUEUE" else "running",
                progress=min(0.88, 0.05 + index / 300.0 * 0.83),
            )
            if state in {"COMPLETED", "OK"}:
                return self._http_json(
                    response_url,
                    "GET",
                    {"Authorization": "Key " + key},
                    None,
                    {"queue.fal.run"},
                    key,
                )
            if state in {"ERROR", "FAILED"}:
                raise OSError(str(status.get("error") or "fal task failed."))
            if state not in {"IN_PROGRESS", "IN_QUEUE"}:
                raise OSError(f"fal returned an unexpected status '{state}'.")
            cancel_event.wait(2.0)
        raise TimeoutError("fal generation timed out after 10 minutes.")

    def _sketchfab_read(
        self,
        action: str,
        arguments: dict[str, Any],
        key: str,
        cancelled: threading.Event,
    ) -> dict[str, Any]:
        if cancelled.is_set():
            raise BridgeCancelled("Sketchfab request was cancelled.")
        headers = {"Authorization": "Token " + key}
        if action == "search":
            query = {
                "type": "models",
                "downloadable": "true" if self._as_bool(arguments.get("downloadable"), True) else "false",
                "q": str(arguments.get("query") or ""),
            }
            if arguments.get("categories"):
                query["categories"] = str(arguments["categories"])
            if arguments.get("count") is not None:
                query["count"] = str(self._bounded_int(arguments.get("count"), 24, 1, 100))
            if arguments.get("cursor"):
                query["cursor"] = str(arguments["cursor"])
            url = "https://api.sketchfab.com/v3/search?" + urlencode(query)
        else:
            uid = arguments.get("uid")
            if not isinstance(uid, str) or not uid.strip():
                return {"success": False, "message": "uid is required for preview."}
            url = "https://api.sketchfab.com/v3/models/" + uid.strip()
        data = self._http_json(
            url, "GET", headers, None, {"api.sketchfab.com"}, key
        )
        return {
            "success": True,
            "message": "Sketchfab " + action + " completed.",
            "data": data,
        }

    def _sketchfab_import_result(
        self,
        arguments: dict[str, Any],
        key: str,
        job_id: str,
        cancel_event: threading.Event,
    ) -> dict[str, Any]:
        uid = str(arguments.get("uid") or "")
        self._asset_cancel_check(cancel_event)
        result = self._http_json(
            "https://api.sketchfab.com/v3/models/" + uid + "/download",
            "GET",
            {"Authorization": "Token " + key},
            None,
            {"api.sketchfab.com"},
            key,
        )
        url = str((result.get("gltf") or {}).get("url") or "")
        if not url:
            raise OSError("Sketchfab download response contained no glTF URL.")
        self._update_asset_job(job_id, state="running", progress=0.25)
        return {"url": url, "extension": ".zip"}

    def _provider_image_reference(self, arguments: dict[str, Any]) -> str:
        url = arguments.get("image_url")
        if isinstance(url, str) and url.strip():
            self._require_public_https(url.strip())
            return url.strip()
        value = arguments.get("image_path")
        if not isinstance(value, str) or not value.strip():
            raise ValueError("image_path or image_url is required.")
        path = self._resolve_source_path(value)
        if not path.is_file():
            raise ValueError(f"Source image was not found: {value}")
        extension = path.suffix.lower()
        mime = {
            ".png": "image/png",
            ".jpg": "image/jpeg",
            ".jpeg": "image/jpeg",
            ".webp": "image/webp",
        }.get(extension)
        if not mime:
            raise ValueError("image_path must be PNG, JPEG, or WebP.")
        encoded = base64.b64encode(path.read_bytes()).decode("ascii")
        return f"data:{mime};base64,{encoded}"

    def _http_json(
        self,
        url: str,
        method: str,
        headers: dict[str, str],
        payload: dict[str, Any] | None,
        allowed_hosts: set[str],
        key: str,
    ) -> dict[str, Any]:
        self._require_https_host(url, allowed_hosts)
        body = None if payload is None else json.dumps(payload, separators=(",", ":")).encode("utf-8")
        request_headers = {"Accept": "application/json", "User-Agent": "unity-mcp-2019/0.3"}
        request_headers.update(headers)
        if body is not None:
            request_headers["Content-Type"] = "application/json"
        request = Request(url, data=body, headers=request_headers, method=method)
        try:
            with urlopen(request, timeout=60) as response:
                raw = response.read(8 * 1024 * 1024 + 1)
                if len(raw) > 8 * 1024 * 1024:
                    raise OSError("Provider JSON response exceeded 8 MiB.")
        except HTTPError as error:
            detail = error.read(4096).decode("utf-8", errors="replace")
            raise OSError(
                self._redact(
                    f"Provider request failed (HTTP {error.code}): {detail[:1000]}", key
                )
            ) from error
        except URLError as error:
            raise OSError(f"Provider request failed: {error.reason}") from error
        try:
            value = json.loads(raw.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise OSError("Provider returned invalid JSON.") from error
        if not isinstance(value, dict):
            raise OSError("Provider returned a non-object JSON payload.")
        return value

    @staticmethod
    def _require_https_host(url: str, hosts: set[str]) -> None:
        parsed = urlparse(url)
        if parsed.scheme.lower() != "https" or (parsed.hostname or "").lower() not in hosts:
            raise ValueError("Provider URL host is not allowlisted.")

    @staticmethod
    def _require_public_https(url: str) -> None:
        parsed = urlparse(url)
        if parsed.scheme.lower() != "https" or not parsed.hostname:
            raise ValueError("Remote asset URL must use https.")
        try:
            address = ipaddress.ip_address(parsed.hostname)
        except ValueError:
            return
        if not address.is_global:
            raise ValueError("Remote asset URL cannot target a private or loopback address.")

    @staticmethod
    def _redact(message: str, key: str) -> str:
        return message.replace(key, "***") if key else message

    @staticmethod
    def _url_extension(url: str, fallback: str) -> str:
        if url.lower().startswith("data:"):
            prefix = url[5 : url.find(";")].lower()
            return {"image/jpeg": ".jpg", "image/webp": ".webp", "image/png": ".png"}.get(prefix, fallback)
        extension = Path(urlparse(url).path).suffix.lower()
        return extension if 1 < len(extension) <= 6 else fallback

    def _write_generated_result(
        self,
        kind: str,
        arguments: dict[str, Any],
        source: dict[str, Any],
    ) -> str:
        extension = str(source.get("extension") or "").lower()
        allowed = {
            "image": {".png", ".jpg", ".jpeg", ".webp"},
            "audio": {".wav", ".mp3", ".ogg", ".aiff", ".aif"},
            "model": {".fbx", ".obj", ".glb", ".gltf", ".zip", ".usdz"},
        }[kind]
        if extension not in allowed:
            raise OSError(f"Provider returned unsupported {kind} extension: {extension}")
        folder = self._asset_output_folder(
            arguments.get("output_folder"),
            {"image": "Images", "audio": "Audio", "model": "Models"}[kind],
        )
        name = self._sanitize_asset_name(
            str(arguments.get("name") or f"generated_{kind}")
        )
        path = folder / (name + extension)
        counter = 1
        while path.exists():
            path = folder / f"{name}_{counter}{extension}"
            counter += 1
        temporary = path.with_name(path.name + ".part")
        try:
            url = str(source.get("url") or "")
            if url.lower().startswith("data:"):
                marker = url.find("base64,")
                if marker < 0:
                    raise OSError("Provider data URL was not base64 encoded.")
                temporary.write_bytes(base64.b64decode(url[marker + 7 :], validate=True))
            else:
                self._require_public_https(url)
                request = Request(url, headers={"User-Agent": "unity-mcp-2019/0.3"})
                with urlopen(request, timeout=120) as response, temporary.open("wb") as output:
                    total = 0
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        total += len(chunk)
                        if total > 1024 * 1024 * 1024:
                            raise OSError("Generated asset exceeded the 1 GiB download limit.")
                        output.write(chunk)
            if temporary.stat().st_size == 0:
                raise OSError("Provider returned an empty asset file.")
            os.replace(temporary, path)
        finally:
            if temporary.exists():
                temporary.unlink()
        return self._asset_path(path)

    def _asset_output_folder(self, requested: Any, category: str) -> Path:
        value = str(requested or f"Assets/Generated/{category}").replace("\\", "/").strip("/")
        if not value.lower().startswith("assets/") or ".." in value.split("/"):
            raise ValueError("output_folder must be under Assets and cannot contain traversal sequences.")
        folder = (self.project / value).resolve()
        assets = (self.project / "Assets").resolve()
        try:
            folder.relative_to(assets)
        except ValueError as error:
            raise ValueError("output_folder must stay inside the project Assets folder.") from error
        folder.mkdir(parents=True, exist_ok=True)
        return folder

    @staticmethod
    def _sanitize_asset_name(value: str) -> str:
        name = re.sub(r"[^A-Za-z0-9._-]+", "_", value.strip()).strip("._")
        return name or "asset"

    def _resolve_source_path(self, value: str) -> Path:
        normalized = value.replace("\\", "/")
        if normalized == "Assets" or normalized.startswith("Assets/"):
            path = (self.project / normalized).resolve()
            try:
                path.relative_to((self.project / "Assets").resolve())
            except ValueError as error:
                raise ValueError("Assets-relative source escaped the project.") from error
            return path
        path = Path(value)
        if not path.is_absolute():
            raise ValueError("source_path/image_path must be absolute or Assets-relative.")
        return path.resolve()

    def _import_model_file(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        value = arguments.get("source_path")
        if not isinstance(value, str) or not value.strip():
            raise ValueError("source_path is required.")
        source = self._resolve_source_path(value)
        if not source.is_file():
            raise ValueError(f"Source model file was not found: {value}")
        extension = source.suffix.lower()
        if extension not in {".fbx", ".obj", ".glb", ".gltf", ".zip"}:
            raise ValueError(
                "Unsupported model extension. Supported: .fbx, .obj, .glb, .gltf, .zip."
            )
        animation_type = str(arguments.get("animation_type") or "none").lower()
        if animation_type not in {"none", "generic", "humanoid", "legacy"}:
            raise ValueError("animation_type must be none, generic, humanoid, or legacy.")
        folder = self._asset_output_folder(arguments.get("output_folder"), "Imported")
        name = self._sanitize_asset_name(
            str(arguments.get("name") or source.stem)
        )
        destination = folder / (name + extension)
        counter = 1
        while destination.exists():
            destination = folder / f"{name}_{counter}{extension}"
            counter += 1
        if cancelled.is_set():
            raise BridgeCancelled("Model import was cancelled.")
        shutil.copy2(source, destination)
        asset_path = self._asset_path(destination)
        if extension == ".zip":
            asset_path = self._prepare_model_asset(asset_path)
        return self.bridge.call(
            "import_generated_asset",
            {
                "Kind": "model",
                "AssetPath": asset_path,
                "TargetSize": float(arguments.get("target_size") or 1.0),
                "AnimationType": animation_type,
            },
            cancelled,
        )

    def _prepare_model_asset(self, asset_path: str) -> str:
        if not asset_path.lower().endswith(".zip"):
            return asset_path
        zip_path = (self.project / asset_path).resolve()
        output = zip_path.with_suffix("")
        output.mkdir(parents=True, exist_ok=True)
        allowed = {
            ".gltf", ".glb", ".bin", ".fbx", ".obj", ".mtl", ".png", ".jpg",
            ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".webp", ".exr", ".hdr",
            ".ktx", ".ktx2", ".basis", ".dds",
        }
        total = 0
        with zipfile.ZipFile(zip_path, "r") as archive:
            for info in archive.infolist():
                if info.is_dir():
                    continue
                mode = (info.external_attr >> 16) & 0o170000
                if mode == 0o120000:
                    continue
                extension = Path(info.filename).suffix.lower()
                if extension not in allowed:
                    continue
                destination = (output / info.filename.replace("\\", "/")).resolve()
                try:
                    destination.relative_to(output.resolve())
                except ValueError as error:
                    raise ValueError("ZIP contains a path traversal entry.") from error
                total += info.file_size
                if total > 2 * 1024 * 1024 * 1024:
                    raise ValueError("ZIP extracted content exceeds the 2 GiB safety limit.")
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info, "r") as source, destination.open("wb") as target:
                    shutil.copyfileobj(source, target, length=1024 * 1024)
        candidates = sorted(
            (
                item
                for item in output.rglob("*")
                if item.is_file() and item.suffix.lower() in {".fbx", ".obj", ".glb", ".gltf"}
            ),
            key=lambda item: (
                1 if item.suffix.lower() in {".glb", ".gltf"} else 0,
                str(item).lower(),
            ),
        )
        if not candidates:
            raise ValueError("ZIP extracted successfully but contained no supported model file.")
        return self._asset_path(candidates[0])

    def _manage_packages(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        actions = {
            "list_packages", "search_packages", "get_package_info", "ping",
            "status", "add_package", "remove_package", "embed_package",
            "resolve_packages", "add_registry", "remove_registry", "list_registries",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_packages action.")

        manifest_path = self.project / "Packages" / "manifest.json"
        if action in {"list_registries", "add_registry", "remove_registry"}:
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError) as error:
                raise ValueError(f"Unable to read Packages/manifest.json: {error}") from error
            if not isinstance(manifest, dict):
                raise ValueError("Packages/manifest.json must contain an object.")
            registries = manifest.get("scopedRegistries") or []
            if not isinstance(registries, list):
                raise ValueError("manifest scopedRegistries must be an array.")
            if action == "list_registries":
                return {
                    "success": True,
                    "message": f"Found {len(registries)} scoped registries.",
                    "data": {"registries": registries, "count": len(registries)},
                }

            registry_name = str(arguments.get("name") or "").strip()
            if not registry_name:
                raise ValueError("name is required for registry changes.")
            if action == "add_registry":
                registry_url = str(arguments.get("url") or "").strip()
                scopes = arguments.get("scopes")
                if not registry_url.startswith(("https://", "http://")):
                    raise ValueError("url must be an HTTP(S) registry URL.")
                if not isinstance(scopes, list) or not scopes or not all(
                    isinstance(scope, str) and scope.strip() for scope in scopes
                ):
                    raise ValueError("scopes must be a non-empty string array.")
                if any(
                    str(item.get("name") or "").lower() == registry_name.lower()
                    or str(item.get("url") or "").lower() == registry_url.lower()
                    for item in registries if isinstance(item, dict)
                ):
                    raise ValueError("A registry with the same name or URL already exists.")
                registry = {
                    "name": registry_name,
                    "url": registry_url,
                    "scopes": [scope.strip() for scope in scopes],
                }
                registries.append(registry)
                message = f"Added scoped registry '{registry_name}'."
                data = registry
            else:
                original_count = len(registries)
                registries = [
                    item for item in registries
                    if not isinstance(item, dict)
                    or str(item.get("name") or "").lower() != registry_name.lower()
                ]
                if len(registries) == original_count:
                    raise ValueError(f"Scoped registry '{registry_name}' was not found.")
                message = f"Removed scoped registry '{registry_name}'."
                data = {"name": registry_name}

            manifest["scopedRegistries"] = registries
            temporary = manifest_path.with_suffix(".json.mcp2019.tmp")
            temporary.write_text(
                json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            os.replace(temporary, manifest_path)
            resolution = self.bridge.call("manage_packages", {"Action": "resolve_packages"}, cancelled)
            return {"success": True, "message": message, "data": data, "resolution": resolution}

        mapped = {
            "Action": action,
            "Package": str(arguments.get("package") or "").strip(),
            "Query": str(arguments.get("query") or "").strip(),
            "JobId": str(arguments.get("job_id") or "").strip(),
            "Force": self._as_bool(arguments.get("force"), False),
        }
        if action == "remove_package" and not mapped["Force"]:
            lock_path = self.project / "Packages" / "packages-lock.json"
            try:
                lock_data = json.loads(lock_path.read_text(encoding="utf-8-sig"))
                dependencies = lock_data.get("dependencies", {})
            except (OSError, json.JSONDecodeError) as error:
                raise ValueError(
                    "Cannot verify package dependents from packages-lock.json; "
                    "set force=true to remove anyway."
                ) from error
            requested = mapped["Package"]
            dependents = []
            if isinstance(dependencies, dict):
                for package_name, package_data in dependencies.items():
                    nested = package_data.get("dependencies", {}) if isinstance(package_data, dict) else {}
                    if isinstance(nested, dict) and requested in nested:
                        dependents.append(str(package_name))
            if dependents:
                raise ValueError(
                    f"Cannot remove '{requested}'; required by: {', '.join(sorted(dependents))}. "
                    "Set force=true to override."
                )
            mapped["Force"] = True
        if action in {"add_package", "remove_package", "embed_package", "get_package_info"}:
            if not mapped["Package"] or any(character in mapped["Package"] for character in "\r\n\0"):
                raise ValueError("package must be a non-empty single-line identifier.")
        if action == "search_packages" and not mapped["Query"]:
            raise ValueError("query is required for search_packages.")
        return self.bridge.call("manage_packages", mapped, cancelled)

    def _manage_build(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        actions = {"build", "status", "platform", "settings", "scenes", "profiles", "batch", "cancel"}
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_build action.")

        def parse_list(value: Any, label: str) -> list[Any]:
            if value is None:
                return []
            if isinstance(value, str):
                stripped = value.strip()
                if not stripped:
                    return []
                try:
                    decoded = json.loads(stripped)
                except json.JSONDecodeError:
                    decoded = [item.strip() for item in stripped.split(",") if item.strip()]
                value = decoded
            if not isinstance(value, list):
                raise ValueError(f"{label} must be an array, JSON array, or comma-separated string.")
            return value

        mapped: dict[str, Any] = {
            "Action": action,
            "Target": str(arguments.get("target") or "").strip(),
            "OutputPath": str(arguments.get("output_path") or "").replace("\\", "/").strip(),
            "Subtarget": str(arguments.get("subtarget") or "").strip(),
            "ScriptingBackend": str(arguments.get("scripting_backend") or "").strip(),
            "Profile": str(arguments.get("profile") or "").replace("\\", "/").strip(),
            "Property": str(arguments.get("property") or "").strip(),
            "OutputDir": str(arguments.get("output_dir") or "").replace("\\", "/").strip(),
            "JobId": str(arguments.get("job_id") or "").strip(),
        }
        if "development" in arguments and arguments.get("development") is not None:
            mapped["Development"] = self._as_bool(arguments.get("development"), False)
            mapped["HasDevelopment"] = True
        if "activate" in arguments and arguments.get("activate") is not None:
            mapped["Activate"] = self._as_bool(arguments.get("activate"), False)
            mapped["HasActivate"] = True
        if "value" in arguments and arguments.get("value") is not None:
            mapped["Value"] = str(arguments.get("value"))
            mapped["HasValue"] = True

        mapped["Options"] = [str(item) for item in parse_list(arguments.get("options"), "options")]
        mapped["Targets"] = [str(item) for item in parse_list(arguments.get("targets"), "targets")]
        mapped["Profiles"] = [str(item).replace("\\", "/") for item in parse_list(arguments.get("profiles"), "profiles")]
        if "scenes" in arguments and arguments.get("scenes") is not None:
            scene_items = parse_list(arguments.get("scenes"), "scenes")
            entries = []
            for item in scene_items:
                if isinstance(item, str):
                    entries.append({"Path": item.replace("\\", "/"), "Enabled": True})
                elif isinstance(item, dict):
                    path = item.get("path")
                    if not isinstance(path, str) or not path.strip():
                        raise ValueError("Each scene object requires a non-empty path.")
                    entries.append({
                        "Path": path.replace("\\", "/").strip(),
                        "Enabled": self._as_bool(item.get("enabled"), True),
                    })
                else:
                    raise ValueError("Each scene must be a path string or {path, enabled} object.")
            mapped["SceneEntries"] = entries
            mapped["HasScenes"] = True
        return self.bridge.call("manage_build", mapped, cancelled)

    def _script_apply_edits(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        name = arguments.get("name")
        directory = arguments.get("path")
        if not isinstance(name, str) or not name.strip():
            raise ValueError("name must be a non-empty string.")
        if not isinstance(directory, str):
            raise ValueError("path must be a string.")
        file_name = name.strip()
        if not file_name.lower().endswith(".cs"):
            file_name += ".cs"
        normalized_directory = directory.replace("\\", "/").strip("/")
        if not normalized_directory.lower().startswith("assets"):
            normalized_directory = "Assets/" + normalized_directory
        path = self._resolve_project_file(
            normalized_directory.rstrip("/") + "/" + file_name,
            require_cs=True,
        )
        contents = self._read_project_text(path)
        edits_value = arguments.get("edits")
        if isinstance(edits_value, str):
            try:
                edits_value = json.loads(edits_value)
            except json.JSONDecodeError as error:
                raise ValueError("edits must contain a valid JSON list.") from error
        if not isinstance(edits_value, list) or not edits_value:
            raise ValueError("edits must be a non-empty list.")
        if len(edits_value) > 50:
            raise ValueError("script_apply_edits supports at most 50 edits per call.")

        updated = contents
        default_class = Path(file_name).stem
        applied: list[dict[str, Any]] = []
        for index, raw_edit in enumerate(edits_value):
            if not isinstance(raw_edit, dict):
                raise ValueError(f"Edit at index {index} must be an object.")
            operation = str(raw_edit.get("op") or "").lower()
            before_sha = self._sha256(updated)
            if operation in {"anchor_insert", "anchor_delete", "anchor_replace"}:
                updated = self._apply_anchor_edit(updated, raw_edit, operation)
            elif operation in {"replace_method", "delete_method", "insert_method"}:
                class_name = raw_edit.get("className") or raw_edit.get("class_name")
                if not isinstance(class_name, str) or not class_name.strip():
                    class_name = default_class
                updated = self._apply_method_edit(
                    updated, raw_edit, operation, class_name.strip()
                )
            else:
                raise ValueError(
                    f"Unsupported structured edit op at index {index}: {operation}"
                )
            applied.append(
                {
                    "index": index,
                    "op": operation,
                    "beforeSha256": before_sha,
                    "afterSha256": self._sha256(updated),
                }
            )

        options = arguments.get("options") or {}
        if not isinstance(options, dict):
            raise ValueError("options must be an object.")
        validation_level = str(options.get("validate") or "standard").lower()
        validation = self._validate_script_text(
            path, updated, validation_level, True, cancelled
        )
        if not validation["success"]:
            raise ValueError(
                "Structured edits were not written because validation failed: "
                + json.dumps(validation["data"].get("diagnostics", []), ensure_ascii=False)
            )

        self._write_text_atomic(path, updated)
        refresh_mode = str(options.get("refresh") or "immediate").lower()
        refresh = None
        if refresh_mode not in {"none", "false", "deferred"}:
            refresh = self.bridge.call("refresh_assets", {}, cancelled)
        result = self._script_result(
            "Structured script edits applied.", path, updated, refresh
        )
        result["data"]["appliedEdits"] = applied
        result["data"]["validation"] = validation["data"]
        return result

    def _apply_anchor_edit(
        self, contents: str, edit: dict[str, Any], operation: str
    ) -> str:
        anchor = edit.get("anchor")
        if not isinstance(anchor, str) or not anchor:
            raise ValueError(f"{operation} requires a non-empty anchor regex.")
        if len(anchor) > 1000:
            raise ValueError("anchor regex must contain at most 1000 characters.")
        try:
            expression = re.compile(anchor, re.MULTILINE)
        except re.error as error:
            raise ValueError(f"Invalid anchor regex: {error}") from error
        matches = list(expression.finditer(contents))
        occurrence = edit.get("occurrence")
        if occurrence is not None:
            occurrence_index = self._bounded_int(occurrence, 1, 1, len(matches)) - 1
            matches = matches[occurrence_index : occurrence_index + 1]
        if len(matches) != 1:
            raise ValueError(
                f"{operation} anchor must match exactly once; matched {len(matches)} times."
            )
        match = matches[0]
        replacement = edit.get("replacement", edit.get("text", ""))
        if not isinstance(replacement, str):
            raise ValueError(f"{operation} replacement must be a string.")
        if operation == "anchor_delete":
            return contents[: match.start()] + contents[match.end() :]
        if operation == "anchor_replace":
            return contents[: match.start()] + replacement + contents[match.end() :]
        position = str(edit.get("position") or "after").lower()
        if position == "before":
            return contents[: match.start()] + replacement + contents[match.start() :]
        if position == "after":
            return contents[: match.end()] + replacement + contents[match.end() :]
        raise ValueError("anchor_insert position must be before or after.")

    def _apply_method_edit(
        self,
        contents: str,
        edit: dict[str, Any],
        operation: str,
        class_name: str,
    ) -> str:
        class_open, class_close = self._find_class_body(contents, class_name)
        method_name = edit.get("methodName") or edit.get("method_name")
        replacement = edit.get("replacement", "")
        if operation in {"replace_method", "delete_method"}:
            if not isinstance(method_name, str) or not method_name.strip():
                raise ValueError(f"{operation} requires methodName.")
            start, end = self._find_method_range(
                contents, class_open, class_close, method_name.strip()
            )
            if operation == "delete_method":
                return contents[:start] + contents[end:]
            if not isinstance(replacement, str) or not replacement.strip():
                raise ValueError("replace_method requires a non-empty replacement.")
            indent = self._indent_at(contents, start)
            prepared = self._indent_block(replacement.strip(), indent)
            return contents[:start] + prepared + contents[end:]

        if not isinstance(replacement, str) or not replacement.strip():
            raise ValueError("insert_method requires a non-empty replacement.")
        position = str(edit.get("position") or "end").lower()
        class_indent = self._indent_at(contents, class_open)
        member_indent = class_indent + "    "
        prepared = self._indent_block(replacement.strip(), member_indent)
        if position == "start":
            insertion = class_open + 1
            payload = "\n" + prepared + "\n"
        elif position == "end":
            insertion = class_close
            payload = "\n" + prepared + "\n" + class_indent
        elif position in {"before", "after"}:
            anchor_name = (
                edit.get("beforeMethodName")
                if position == "before"
                else edit.get("afterMethodName")
            )
            if not anchor_name:
                anchor_name = method_name
            if not isinstance(anchor_name, str) or not anchor_name.strip():
                raise ValueError(
                    f"insert_method position={position} requires an anchor method name."
                )
            anchor_start, anchor_end = self._find_method_range(
                contents, class_open, class_close, anchor_name.strip()
            )
            if position == "before":
                insertion = anchor_start
                payload = prepared + "\n\n"
            else:
                insertion = anchor_end
                payload = "\n\n" + prepared
        else:
            raise ValueError("insert_method position must be start, end, before, or after.")
        return contents[:insertion] + payload + contents[insertion:]

    @classmethod
    def _find_class_body(cls, contents: str, class_name: str) -> tuple[int, int]:
        masked = cls._mask_csharp(contents)
        pattern = re.compile(
            r"\b(?:class|struct|interface)\s+" + re.escape(class_name) + r"\b"
        )
        matches = list(pattern.finditer(masked))
        if len(matches) != 1:
            raise ValueError(
                f"Class {class_name} must be found exactly once; found {len(matches)}."
            )
        opening = masked.find("{", matches[0].end())
        if opening < 0:
            raise ValueError(f"Class {class_name} has no body.")
        return opening, cls._matching_delimiter(masked, opening, "{", "}")

    @classmethod
    def _find_method_range(
        cls,
        contents: str,
        class_open: int,
        class_close: int,
        method_name: str,
    ) -> tuple[int, int]:
        masked = cls._mask_csharp(contents)
        pattern = re.compile(r"\b" + re.escape(method_name) + r"\s*\(")
        ranges: list[tuple[int, int]] = []
        for match in pattern.finditer(masked, class_open + 1, class_close):
            prefix_body = masked[class_open + 1 : match.start()]
            if prefix_body.count("{") != prefix_body.count("}"):
                continue
            prefix = masked[max(class_open + 1, match.start() - 2) : match.start()]
            if "." in prefix:
                continue
            opening_paren = masked.find("(", match.start(), match.end())
            closing_paren = cls._matching_delimiter(masked, opening_paren, "(", ")")
            cursor = closing_paren + 1
            while cursor < class_close and masked[cursor].isspace():
                cursor += 1
            body_open = masked.find("{", cursor, min(class_close, cursor + 1000))
            semicolon = masked.find(";", cursor, min(class_close, cursor + 1000))
            arrow = masked.find("=>", cursor, min(class_close, cursor + 1000))
            if arrow >= 0 and (body_open < 0 or arrow < body_open):
                if semicolon < 0:
                    continue
                end = semicolon + 1
            elif body_open >= 0 and (semicolon < 0 or body_open < semicolon):
                end = cls._matching_delimiter(masked, body_open, "{", "}") + 1
            else:
                continue
            start = contents.rfind("\n", 0, match.start()) + 1
            while end < len(contents) and contents[end] in " \t":
                end += 1
            ranges.append((start, end))
        unique = list(dict.fromkeys(ranges))
        if len(unique) != 1:
            raise ValueError(
                f"Method {method_name} must resolve to exactly one body; found {len(unique)}."
            )
        return unique[0]

    @staticmethod
    def _matching_delimiter(
        masked: str, opening: int, opening_char: str, closing_char: str
    ) -> int:
        if opening < 0 or opening >= len(masked) or masked[opening] != opening_char:
            raise ValueError(f"Missing opening delimiter {opening_char}.")
        depth = 0
        for index in range(opening, len(masked)):
            char = masked[index]
            if char == opening_char:
                depth += 1
            elif char == closing_char:
                depth -= 1
                if depth == 0:
                    return index
        raise ValueError(f"Unclosed delimiter {opening_char}.")

    @staticmethod
    def _mask_csharp(contents: str) -> str:
        chars = list(contents)
        state = "code"
        index = 0
        while index < len(chars):
            char = chars[index]
            following = chars[index + 1] if index + 1 < len(chars) else ""
            if state == "code":
                if char == "/" and following == "/":
                    chars[index] = chars[index + 1] = " "
                    state = "line_comment"
                    index += 2
                    continue
                if char == "/" and following == "*":
                    chars[index] = chars[index + 1] = " "
                    state = "block_comment"
                    index += 2
                    continue
                if char == "@" and following == '"':
                    chars[index] = chars[index + 1] = " "
                    state = "verbatim"
                    index += 2
                    continue
                if char == '"':
                    chars[index] = " "
                    state = "string"
                elif char == "'":
                    chars[index] = " "
                    state = "char"
                index += 1
                continue
            if state == "line_comment":
                if char == "\n":
                    state = "code"
                else:
                    chars[index] = " "
                index += 1
                continue
            if state == "block_comment":
                if char == "*" and following == "/":
                    chars[index] = chars[index + 1] = " "
                    state = "code"
                    index += 2
                else:
                    if char not in "\r\n":
                        chars[index] = " "
                    index += 1
                continue
            if state in {"string", "char"}:
                quote = '"' if state == "string" else "'"
                if char == "\\":
                    chars[index] = " "
                    if index + 1 < len(chars):
                        if chars[index + 1] not in "\r\n":
                            chars[index + 1] = " "
                        index += 2
                    else:
                        index += 1
                else:
                    if char == quote:
                        state = "code"
                    if char not in "\r\n":
                        chars[index] = " "
                    index += 1
                continue
            if state == "verbatim":
                if char == '"' and following == '"':
                    chars[index] = chars[index + 1] = " "
                    index += 2
                else:
                    if char == '"':
                        state = "code"
                    if char not in "\r\n":
                        chars[index] = " "
                    index += 1
        return "".join(chars)

    @staticmethod
    def _indent_at(contents: str, position: int) -> str:
        line_start = contents.rfind("\n", 0, position) + 1
        match = re.match(r"[ \t]*", contents[line_start:])
        return match.group(0) if match else ""

    @staticmethod
    def _indent_block(text: str, indent: str) -> str:
        lines = text.splitlines()
        if not lines:
            return indent
        minimum: int | None = None
        for line in lines[1:]:
            if not line.strip():
                continue
            count = len(line) - len(line.lstrip(" \t"))
            minimum = count if minimum is None else min(minimum, count)
        trim = minimum or 0
        normalized = [lines[0].lstrip()]
        normalized.extend(line[trim:] if line.strip() else "" for line in lines[1:])
        return "\n".join(indent + line if line else "" for line in normalized)

    def _unity_docs(self, arguments: dict[str, Any]) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        version = self._unity_docs_version(arguments.get("version"))
        if action == "get_doc":
            class_name = self._required_doc_text(arguments.get("class_name"), "class_name")
            member_name = arguments.get("member_name")
            page = class_name + ("." + str(member_name) if member_name else "") + ".html"
            url = f"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{page}"
            result = self._fetch_unity_doc(url)
            if result.get("status") == 404 and member_name:
                property_page = class_name + "-" + str(member_name) + ".html"
                url = f"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{property_page}"
                result = self._fetch_unity_doc(url)
            return {"success": result.get("status") == 200, "data": result}
        if action == "get_manual":
            slug = self._required_doc_text(arguments.get("slug"), "slug")
            url = f"https://docs.unity3d.com/{version}/Documentation/Manual/{slug}.html"
            result = self._fetch_unity_doc(url)
            return {"success": result.get("status") == 200, "data": result}
        if action == "get_package_doc":
            package = self._required_doc_text(arguments.get("package"), "package")
            page = self._required_doc_text(arguments.get("page"), "page")
            package_version = self._required_doc_text(
                arguments.get("pkg_version"), "pkg_version"
            )
            url = (
                f"https://docs.unity3d.com/Packages/{package}@{package_version}/manual/"
                + page
                + ("" if page.lower().endswith(".html") else ".html")
            )
            result = self._fetch_unity_doc(url)
            return {"success": result.get("status") == 200, "data": result}
        if action == "lookup":
            raw_queries = arguments.get("queries") or arguments.get("query")
            query_text = self._required_doc_text(raw_queries, "query or queries")
            queries = [value.strip() for value in query_text.split(",") if value.strip()]
            if len(queries) > 10:
                raise ValueError("lookup supports at most 10 comma-separated queries.")
            results: list[dict[str, Any]] = []
            for query in queries:
                parts = query.rsplit(".", 1)
                page = query + ".html"
                url = f"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{page}"
                item = self._fetch_unity_doc(url)
                if item.get("status") == 404 and len(parts) == 2:
                    property_page = parts[0] + "-" + parts[1] + ".html"
                    item = self._fetch_unity_doc(
                        f"https://docs.unity3d.com/{version}/Documentation/ScriptReference/{property_page}"
                    )
                results.append({"query": query, **item})
            return {
                "success": any(item.get("status") == 200 for item in results),
                "data": {"version": version, "results": results},
            }
        raise ValueError(
            "unity_docs action must be get_doc, get_manual, get_package_doc, or lookup."
        )

    @staticmethod
    def _unity_docs_version(value: Any) -> str:
        raw = str(value or "2019.4")
        match = re.match(r"^(\d{4}|\d+)\.(\d+)", raw)
        if not match:
            raise ValueError("version must begin with Unity major.minor.")
        return match.group(1) + "." + match.group(2)

    @staticmethod
    def _required_doc_text(value: Any, field: str) -> str:
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"unity_docs requires {field}.")
        text = value.strip()
        if any(char in text for char in "\\?#") or ".." in text:
            raise ValueError(f"Invalid {field} value.")
        return text

    @staticmethod
    def _fetch_unity_doc(url: str) -> dict[str, Any]:
        request = Request(url, headers={"User-Agent": "MCPForUnity2019/0.4"})
        try:
            with urlopen(request, timeout=10) as response:
                payload = response.read(2 * 1024 * 1024 + 1)
                if len(payload) > 2 * 1024 * 1024:
                    raise ValueError("Unity documentation response exceeded 2 MiB.")
                html = payload.decode("utf-8", errors="replace")
                parser = UnityDocumentationParser()
                parser.feed(html)
                content = "\n\n".join(parser.blocks)
                return {
                    "status": int(getattr(response, "status", 200)),
                    "url": response.geturl(),
                    "title": parser.title,
                    "content": content[:100000],
                    "truncated": len(content) > 100000,
                }
        except HTTPError as error:
            return {"status": error.code, "url": url, "title": "", "content": ""}
        except URLError as error:
            raise ValueError(f"Could not reach Unity documentation: {error}") from error

    def _find_in_file(self, arguments: dict[str, Any]) -> dict[str, Any]:
        path = self._resolve_project_file(arguments.get("uri"), require_cs=False)
        contents = self._read_project_text(path)
        pattern = arguments.get("pattern")
        if not isinstance(pattern, str) or not pattern:
            raise ValueError("pattern must be a non-empty string.")
        if len(pattern) > 500:
            raise ValueError("pattern must contain at most 500 characters.")
        flags = re.IGNORECASE if self._as_bool(arguments.get("ignore_case"), False) else 0
        try:
            expression = re.compile(pattern, flags)
        except re.error as error:
            raise ValueError(f"Invalid regular expression: {error}") from error
        maximum = self._bounded_int(arguments.get("max_results"), 50, 1, 200)
        matches: list[dict[str, Any]] = []
        for line_number, line in enumerate(contents.splitlines(), 1):
            for match in expression.finditer(line):
                matches.append(
                    {
                        "line": line_number,
                        "column": match.start() + 1,
                        "match": match.group(0),
                        "excerpt": line[:500],
                    }
                )
                if len(matches) >= maximum:
                    break
            if len(matches) >= maximum:
                break
        return {
            "success": True,
            "data": {
                "path": self._asset_path(path),
                "pattern": pattern,
                "matches": matches,
                "count": len(matches),
                "truncated": len(matches) >= maximum,
            },
        }

    def _apply_text_edits(
        self, arguments: dict[str, Any], cancelled: threading.Event
    ) -> dict[str, Any]:
        path = self._resolve_project_file(arguments.get("uri"), require_cs=True)
        contents = self._read_project_text(path)
        expected_sha = arguments.get("precondition_sha256")
        current_sha = self._sha256(contents)
        if expected_sha is not None and str(expected_sha).lower() != current_sha:
            raise ValueError(
                "stale_file: precondition_sha256 does not match the current script."
            )
        edits = arguments.get("edits")
        if not isinstance(edits, list) or not edits:
            raise ValueError("edits must be a non-empty list.")
        normalized: list[tuple[int, int, str]] = []
        for index, edit in enumerate(edits):
            if not isinstance(edit, dict):
                raise ValueError(f"Edit at index {index} must be an object.")
            start = self._line_col_offset(
                contents, edit.get("startLine"), edit.get("startCol")
            )
            end = self._line_col_offset(
                contents, edit.get("endLine"), edit.get("endCol")
            )
            if end < start:
                raise ValueError(f"Edit at index {index} has an inverted range.")
            new_text = edit.get("newText", edit.get("text", ""))
            if not isinstance(new_text, str):
                raise ValueError(f"Edit at index {index} newText must be a string.")
            normalized.append((start, end, new_text))
        normalized.sort(key=lambda value: value[0], reverse=True)
        previous_start = len(contents) + 1
        for start, end, _ in normalized:
            if end > previous_start:
                raise ValueError("Text edit ranges must not overlap.")
            previous_start = start
        updated = contents
        for start, end, new_text in normalized:
            updated = updated[:start] + new_text + updated[end:]
        validation = self._validate_script_text(path, updated, "basic", True)
        if not validation["success"]:
            raise ValueError("Edited script failed structural validation.")
        self._write_text_atomic(path, updated)
        refresh = self.bridge.call("refresh_assets", {}, cancelled)
        return self._script_result("Text edits applied.", path, updated, refresh)

    def _validate_script_text(
        self,
        path: Path,
        contents: str,
        level: str,
        include_diagnostics: bool,
        cancelled: threading.Event | None = None,
    ) -> dict[str, Any]:
        if level not in {"basic", "standard"}:
            raise ValueError("level must be basic or standard.")
        diagnostics: list[dict[str, Any]] = []
        stack: list[tuple[str, int, int]] = []
        pairs = {"}": "{", ")": "(", "]": "["}
        opening = set(pairs.values())
        state = "code"
        escaped = False
        line = 1
        column = 0
        index = 0
        while index < len(contents):
            char = contents[index]
            next_char = contents[index + 1] if index + 1 < len(contents) else ""
            column += 1
            if char == "\n":
                line += 1
                column = 0
                if state == "line_comment":
                    state = "code"
                index += 1
                continue
            if state == "line_comment":
                index += 1
                continue
            if state == "block_comment":
                if char == "*" and next_char == "/":
                    state = "code"
                    index += 2
                    column += 1
                else:
                    index += 1
                continue
            if state in {"string", "char"}:
                quote = '"' if state == "string" else "'"
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == quote:
                    state = "code"
                index += 1
                continue
            if char == "/" and next_char == "/":
                state = "line_comment"
                index += 2
                column += 1
                continue
            if char == "/" and next_char == "*":
                state = "block_comment"
                index += 2
                column += 1
                continue
            if char == '"':
                state = "string"
            elif char == "'":
                state = "char"
            elif char in opening:
                stack.append((char, line, column))
            elif char in pairs:
                if not stack or stack[-1][0] != pairs[char]:
                    diagnostics.append(
                        {"severity": "error", "line": line, "column": column, "message": f"Unmatched {char}."}
                    )
                else:
                    stack.pop()
            index += 1
        for char, item_line, item_column in stack:
            diagnostics.append(
                {"severity": "error", "line": item_line, "column": item_column, "message": f"Unclosed {char}."}
            )
        if state in {"block_comment", "string", "char"}:
            diagnostics.append(
                {"severity": "error", "line": line, "column": column, "message": f"Unclosed {state.replace('_', ' ')}."}
            )
        if level == "standard" and ": MonoBehaviour" in contents and "using UnityEngine" not in contents:
            diagnostics.append(
                {"severity": "warning", "line": 1, "column": 1, "message": "MonoBehaviour script does not import UnityEngine."}
            )
        errors = [item for item in diagnostics if item["severity"] == "error"]
        if level == "standard" and not errors and cancelled is not None:
            compiled = self._normalize_upstream_tool_result(
                self.bridge.call(
                    "validate_script",
                    {
                        "AssetPath": self._asset_path(path),
                        "Contents": contents,
                        "Level": level,
                        "IncludeDiagnostics": include_diagnostics,
                    },
                    cancelled,
                )
            )
            compiled_data = compiled.setdefault("data", {})
            if isinstance(compiled_data, dict):
                compiled_data["sha256"] = self._sha256(contents)
                if not include_diagnostics:
                    compiled_data.pop("diagnostics", None)
            return compiled
        data: dict[str, Any] = {
            "path": self._asset_path(path),
            "level": level,
            "isValid": not errors,
            "errorCount": len(errors),
            "warningCount": len(diagnostics) - len(errors),
            "sha256": self._sha256(contents),
        }
        if include_diagnostics:
            data["diagnostics"] = diagnostics
        return {
            "success": not errors,
            "message": "Script validation passed." if not errors else "Script validation failed.",
            "data": data,
        }

    def _resolve_project_file(self, value: Any, require_cs: bool) -> Path:
        if not isinstance(value, str) or not value.strip():
            raise ValueError("A non-empty project file path is required.")
        raw = value.strip()
        parsed = urlparse(raw)
        if parsed.scheme == "mcpforunity" and parsed.netloc == "path":
            raw = unquote(parsed.path.lstrip("/"))
        elif parsed.scheme == "file":
            raw = unquote(parsed.path)
            if os.name == "nt" and len(raw) >= 3 and raw[0] == "/" and raw[2] == ":":
                raw = raw[1:]
        candidate = Path(raw)
        if not candidate.is_absolute():
            normalized = raw.replace("\\", "/").lstrip("/")
            if not normalized.lower().startswith("assets/"):
                raise ValueError("Project files must be addressed under Assets/.")
            candidate = self.project / Path(normalized)
        resolved = candidate.resolve()
        assets = (self.project / "Assets").resolve()
        try:
            resolved.relative_to(assets)
        except ValueError as error:
            raise ValueError("Project file path escapes the Assets directory.") from error
        if require_cs and resolved.suffix.lower() != ".cs":
            raise ValueError("Script file must use the .cs extension.")
        return resolved

    def _read_project_text(self, path: Path) -> str:
        if not path.is_file():
            raise ValueError(f"Project file does not exist: {self._asset_path(path)}")
        if path.stat().st_size > 4 * 1024 * 1024:
            raise ValueError("Project file exceeds the 4 MiB compatibility limit.")
        try:
            return path.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError) as error:
            raise ValueError(f"Could not read project file: {error}") from error

    @staticmethod
    def _write_text_atomic(path: Path, contents: str) -> None:
        encoded = contents.encode("utf-8")
        if len(encoded) > 4 * 1024 * 1024:
            raise ValueError("Script exceeds the 4 MiB compatibility limit.")
        path.parent.mkdir(parents=True, exist_ok=True)
        descriptor, temporary_name = tempfile.mkstemp(
            prefix=path.name + ".", suffix=".tmp", dir=str(path.parent)
        )
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(encoded)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary_name, path)
        finally:
            if os.path.exists(temporary_name):
                os.unlink(temporary_name)

    def _script_result(
        self, message: str, path: Path, contents: str, refresh: Any
    ) -> dict[str, Any]:
        data: dict[str, Any] = {
            "path": self._asset_path(path),
            "uri": "mcpforunity://path/" + self._asset_path(path),
            "sha256": self._sha256(contents),
            "length": len(contents),
            "contents": contents,
        }
        if refresh is not None:
            data["refresh"] = refresh
        return {"success": True, "message": message, "data": data}

    def _asset_path(self, path: Path) -> str:
        return path.resolve().relative_to(self.project).as_posix()

    @staticmethod
    def _sha256(contents: str) -> str:
        return hashlib.sha256(contents.encode("utf-8")).hexdigest()

    @staticmethod
    def _line_col_offset(contents: str, line_value: Any, column_value: Any) -> int:
        try:
            line = int(line_value)
            column = int(column_value)
        except (TypeError, ValueError) as error:
            raise ValueError("Text edit line and column values must be integers.") from error
        if line < 1 or column < 1:
            raise ValueError("Text edit line and column values are 1-based.")
        lines = contents.splitlines(keepends=True)
        if not lines:
            lines = [""]
        if line > len(lines):
            if line == len(lines) + 1 and column == 1 and contents.endswith(("\n", "\r")):
                return len(contents)
            raise ValueError("Text edit line is outside the script.")
        line_text = lines[line - 1]
        logical_length = len(line_text.rstrip("\r\n"))
        if column > logical_length + 1:
            raise ValueError("Text edit column is outside the line.")
        return sum(len(value) for value in lines[: line - 1]) + column - 1

    @staticmethod
    def _bounded_int(value: Any, default: int, minimum: int, maximum: int) -> int:
        if value is None:
            return default
        try:
            parsed = int(value)
        except (TypeError, ValueError) as error:
            raise ValueError("Expected an integer value.") from error
        return max(minimum, min(maximum, parsed))

    @staticmethod
    def _as_bool(value: Any, default: bool) -> bool:
        if value is None:
            return default
        if isinstance(value, bool):
            return value
        if isinstance(value, str) and value.lower() in {"true", "false"}:
            return value.lower() == "true"
        raise ValueError("Expected a boolean value.")

    def _read_resource(
        self, request_id: Any, params: dict[str, Any]
    ) -> dict[str, Any]:
        uri = params.get("uri")
        if not isinstance(uri, str):
            return self._rpc_error(request_id, -32602, "Resource uri is required.")

        try:
            method, arguments = self._map_resource(uri)
        except ValueError as error:
            return self._rpc_error(request_id, -32002, str(error))

        cancelled = self._register_request(request_id)
        try:
            result = self.bridge.call(method, arguments, cancelled)
            payload = (
                {"data": result}
                if uri.startswith(LEGACY_RESOURCE_PREFIX)
                else self._normalize_resource_result(uri, result)
            )
            text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True)
            return self._rpc_result(
                request_id,
                {
                    "contents": [
                        {"uri": uri, "mimeType": "application/json", "text": text}
                    ]
                },
            )
        except BridgeCancelled as error:
            return self._rpc_error(request_id, -32800, str(error))
        except BridgeError as error:
            return self._rpc_error(request_id, -32002, str(error))
        finally:
            self._unregister_request(request_id)

    def _normalize_resource_result(self, uri: str, result: Any) -> dict[str, Any]:
        if uri == "mcpforunity://editor/state" and isinstance(result, dict):
            now_ms = int(time.time() * 1000)
            compiling = bool(result.get("IsCompiling", False))
            updating = bool(result.get("IsUpdating", False))
            playing = bool(result.get("IsPlaying", False))
            paused = bool(result.get("IsPaused", False))
            ready = bool(result.get("ReadyForTools", not compiling and not updating))
            blocking: list[str] = []
            if compiling:
                blocking.append("compiling")
            if updating:
                blocking.append("asset_refresh")
            project_hash = hashlib.sha256(
                str(self.project).lower().encode("utf-8")
            ).hexdigest()[:8]
            phase = "compiling" if compiling else "asset_refresh" if updating else "play_mode" if playing else "idle"
            return {
                "success": True,
                "message": "Retrieved editor state.",
                "data": {
                    "schema_version": "unity-mcp/editor_state@2",
                    "observed_at_unix_ms": now_ms,
                    "sequence": 0,
                    "unity": {
                        "instance_id": f"{self.project.name}@{project_hash}",
                        "unity_version": result.get("UnityVersion"),
                        "project_id": project_hash,
                        "is_batch_mode": False,
                    },
                    "editor": {
                        "play_mode": {
                            "is_playing": playing,
                            "is_paused": paused,
                            "is_changing": False,
                        },
                        "active_scene": {
                            "path": result.get("ActiveScenePath"),
                            "name": result.get("ActiveSceneName"),
                        },
                    },
                    "activity": {
                        "phase": phase,
                        "since_unix_ms": now_ms,
                        "reasons": blocking,
                    },
                    "compilation": {
                        "is_compiling": compiling,
                        "is_domain_reload_pending": compiling,
                    },
                    "assets": {
                        "is_updating": updating,
                        "refresh": {"is_refresh_in_progress": updating},
                    },
                    "tests": {"is_running": False},
                    "transport": {
                        "unity_bridge_connected": True,
                        "last_message_unix_ms": now_ms,
                    },
                    "settings": {"batch_execute_max_commands": 25},
                    "advice": {
                        "ready_for_tools": ready,
                        "blocking_reasons": blocking,
                        "recommended_retry_after_ms": 0 if ready else 500,
                        "recommended_next_action": "none" if ready else "retry_later",
                    },
                    "staleness": {"age_ms": 0, "is_stale": False},
                },
            }
        normalized = self._normalize_upstream_tool_result(result)
        if "message" not in normalized:
            normalized["message"] = "Resource retrieved."
        return normalized

    def cancel_request(self, request_id: Any) -> None:
        with self._request_events_lock:
            event = self._request_events.get(request_id)
            if event is None:
                self._cancelled_before_start.add(request_id)
        if event is not None:
            event.set()

    def _register_request(self, request_id: Any) -> threading.Event:
        event = threading.Event()
        with self._request_events_lock:
            self._request_events[request_id] = event
            if request_id in self._cancelled_before_start:
                self._cancelled_before_start.remove(request_id)
                event.set()
        return event

    def _unregister_request(self, request_id: Any) -> None:
        with self._request_events_lock:
            self._request_events.pop(request_id, None)
            self._cancelled_before_start.discard(request_id)

    @staticmethod
    def _map_tool(name: str, arguments: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        if name == "execute_menu_item":
            menu_path = arguments.get("menu_path")
            if not isinstance(menu_path, str) or not menu_path.strip():
                raise ValueError("menu_path must be a non-empty string.")
            return "execute_menu_item", {"MenuPath": menu_path.strip()}
        if name == "read_console":
            action = str(arguments.get("action") or "get").lower()
            if action == "clear":
                return "clear_console", {}
            if action != "get":
                raise ValueError("read_console action must be get or clear.")
            raw_types = arguments.get("types", [])
            if isinstance(raw_types, str):
                try:
                    parsed_types = json.loads(raw_types)
                    raw_types = parsed_types if isinstance(parsed_types, list) else [raw_types]
                except json.JSONDecodeError:
                    raw_types = [raw_types]
            if not isinstance(raw_types, list):
                raise ValueError("types must be a list or JSON list string.")
            type_map = {
                "log": "Log",
                "warning": "Warning",
                "error": "Error",
                "assert": "Assert",
                "exception": "Exception",
            }
            types = [] if "all" in [str(value).lower() for value in raw_types] else [
                type_map.get(str(value).lower(), str(value)) for value in raw_types
            ]
            limit = arguments.get("count")
            if arguments.get("page_size") is not None:
                limit = arguments.get("page_size")
            return "read_console", {
                "Limit": McpServer._bounded_int(limit, 10, 1, 200),
                "Types": types,
                "FilterText": arguments.get("filter_text") or "",
                "Offset": McpServer._bounded_int(arguments.get("cursor"), 0, 0, 1000000),
                "IncludeStackTrace": McpServer._as_bool(
                    arguments.get("include_stacktrace"), False
                ),
                "Format": arguments.get("format") or "plain",
            }
        if name == "find_gameobjects":
            search_term = arguments.get("search_term")
            if not isinstance(search_term, str) or not search_term.strip():
                raise ValueError("search_term must be a non-empty string.")
            search_method = str(arguments.get("search_method") or "by_name").lower()
            if search_method not in {
                "by_name", "by_tag", "by_layer", "by_component", "by_path", "by_id"
            }:
                raise ValueError("search_method is not supported.")
            return "find_gameobjects", {
                "Query": search_term.strip(),
                "SearchMethod": search_method,
                "IncludeInactive": McpServer._as_bool(
                    arguments.get("include_inactive"), True
                ),
                "Offset": McpServer._parse_cursor(arguments.get("cursor")),
                "PageSize": McpServer._bounded_int(
                    arguments.get("page_size"), 100, 1, 500
                ),
            }
        if name == "refresh_unity":
            return "refresh_assets", {
                "Mode": arguments.get("mode") or "if_dirty",
                "Scope": arguments.get("scope") or "all",
                "Compile": arguments.get("compile") or "none",
            }
        if name == "manage_editor":
            action = str(arguments.get("action") or "").lower()
            if action in {"play", "pause", "stop"}:
                return "play_mode", {"Action": action}
            if action in {"undo", "redo"}:
                return "undo_redo", {"Action": action}
            return "manage_editor", {
                "Action": action,
                "ToolName": arguments.get("tool_name") or "",
                "TagName": arguments.get("tag_name") or "",
                "LayerName": arguments.get("layer_name") or "",
            }
        if name == "manage_scene":
            mapped: dict[str, Any] = {
                "Action": str(arguments.get("action") or "").lower(),
                "Name": arguments.get("name") or "",
                "Path": arguments.get("path") or "",
                "BuildIndex": McpServer._bounded_int(
                    arguments.get("build_index"), -1, -1, 100000
                ),
                "PageSize": McpServer._bounded_int(
                    arguments.get("page_size"), 100, 1, 500
                ),
                "Cursor": McpServer._parse_cursor(arguments.get("cursor")),
                "MaxDepth": McpServer._bounded_int(
                    arguments.get("max_depth"), 10, 0, 20
                ),
                "SceneName": arguments.get("scene_name") or "",
                "ScenePath": arguments.get("scene_path") or "",
                "RemoveScene": McpServer._as_bool(
                    arguments.get("remove_scene"), False
                ),
                "Additive": McpServer._as_bool(arguments.get("additive"), False),
                "Template": arguments.get("template") or "",
                "AutoRepair": McpServer._as_bool(
                    arguments.get("auto_repair"), False
                ),
            }
            scene_view_target = arguments.get("scene_view_target")
            if isinstance(scene_view_target, int) and not isinstance(
                scene_view_target, bool
            ):
                mapped["SceneViewTargetInstanceId"] = scene_view_target
            elif scene_view_target is not None:
                mapped["SceneViewTarget"] = str(scene_view_target)
            target = arguments.get("target")
            if isinstance(target, int) and not isinstance(target, bool):
                mapped["TargetInstanceId"] = target
            elif target is not None:
                mapped["Target"] = str(target)
            return "manage_scene", mapped
        if name == "unity_reflect":
            action = str(arguments.get("action") or "").lower()
            if action not in {"get_type", "get_member", "search"}:
                raise ValueError("unity_reflect action must be get_type, get_member, or search.")
            scope = str(arguments.get("scope") or "all").lower()
            if scope not in {"unity", "packages", "project", "all"}:
                raise ValueError("unity_reflect scope must be unity, packages, project, or all.")
            return "unity_reflect", {
                "Action": action,
                "ClassName": arguments.get("class_name") or "",
                "MemberName": arguments.get("member_name") or "",
                "Query": arguments.get("query") or "",
                "Scope": scope,
            }
        if name == "execute_code":
            action = str(arguments.get("action") or "").lower()
            if action not in {"execute", "get_history", "replay", "clear_history"}:
                raise ValueError(
                    "execute_code action must be execute, get_history, replay, or clear_history."
                )
            compiler = str(arguments.get("compiler") or "auto").lower()
            if compiler == "roslyn":
                raise ValueError(
                    "Unity 2019 compatibility mode supports compiler=auto or codedom; Roslyn is not bundled."
                )
            return "execute_code", {
                "Action": action,
                "Code": arguments.get("code") or "",
                "SafetyChecks": McpServer._as_bool(
                    arguments.get("safety_checks"), True
                ),
                "Index": McpServer._bounded_int(arguments.get("index"), -1, -1, 100000),
                "Limit": McpServer._bounded_int(arguments.get("limit"), 10, 1, 50),
                "Compiler": compiler,
            }
        if name == "execute_custom_tool":
            tool_name = arguments.get("tool_name")
            if not isinstance(tool_name, str) or not tool_name.strip():
                raise ValueError("tool_name must be a non-empty string.")
            parameters = arguments.get("parameters") or {}
            if not isinstance(parameters, dict):
                raise ValueError("parameters must be an object.")
            return "execute_custom_tool", {
                "ToolName": tool_name.strip(),
                "ParametersJson": json.dumps(
                    parameters, ensure_ascii=False, separators=(",", ":")
                ),
            }
        if name == "manage_asset":
            action = str(arguments.get("action") or "").lower()
            allowed = {
                "import", "create", "modify", "delete", "duplicate", "move",
                "rename", "search", "get_info", "create_folder", "get_components",
            }
            if action not in allowed:
                raise ValueError("Unsupported manage_asset action.")
            path = arguments.get("path")
            if not isinstance(path, str) or not path.strip():
                raise ValueError("manage_asset path must be a non-empty string.")
            return "manage_asset", {
                "Action": action,
                "Path": path.strip(),
                "AssetType": arguments.get("asset_type") or "",
                "Destination": arguments.get("destination") or "",
                "SearchPattern": arguments.get("search_pattern") or "",
                "FilterType": arguments.get("filter_type") or "",
                "PageSize": McpServer._bounded_int(
                    arguments.get("page_size"), 25, 1, 500
                ),
                "PageNumber": McpServer._bounded_int(
                    arguments.get("page_number"), 1, 1, 100000
                ),
                "GeneratePreview": McpServer._as_bool(
                    arguments.get("generate_preview"), False
                ),
                "Properties": McpServer._serialized_patches(
                    arguments.get("properties")
                ),
            }
        if name == "manage_material":
            action = str(arguments.get("action") or "").lower()
            target = arguments.get("target")
            mapped = {
                "Action": action,
                "MaterialPath": arguments.get("material_path") or "",
                "Property": arguments.get("property") or "",
                "Shader": arguments.get("shader") or "Standard",
                "Properties": McpServer._serialized_patches(
                    arguments.get("properties")
                ),
                "Slot": McpServer._bounded_int(arguments.get("slot"), 0, 0, 128),
                "Mode": arguments.get("mode") or "shared",
                "SearchMethod": arguments.get("search_method") or "by_name",
            }
            if isinstance(target, int) and not isinstance(target, bool):
                mapped["TargetInstanceId"] = target
            elif target is not None:
                mapped["Target"] = str(target)
            value = arguments.get("value")
            if value is not None:
                mapped["Value"] = McpServer._serialized_patch(
                    str(arguments.get("property") or "value"), value
                )
            color = arguments.get("color")
            if color is not None:
                mapped["Color"] = McpServer._serialized_patch("color", color)
            return "manage_material", mapped
        if name == "manage_texture":
            action = str(arguments.get("action") or "").lower()
            allowed = {
                "create", "modify", "delete", "create_sprite", "apply_pattern",
                "apply_gradient", "apply_noise", "set_import_settings",
            }
            if action not in allowed:
                raise ValueError("Unsupported manage_texture action.")
            path = arguments.get("path")
            if not isinstance(path, str) or not path.strip():
                raise ValueError("manage_texture path must be a non-empty string.")
            palette = arguments.get("palette") or []
            if isinstance(palette, str):
                try:
                    palette = json.loads(palette)
                except json.JSONDecodeError as error:
                    raise ValueError("palette must contain valid JSON.") from error
            if not isinstance(palette, list):
                raise ValueError("palette must be a list.")
            mapped = {
                "Action": action,
                "Path": path.strip(),
                "Width": McpServer._bounded_int(arguments.get("width"), 64, 1, 4096),
                "Height": McpServer._bounded_int(arguments.get("height"), 64, 1, 4096),
                "FillColor": McpServer._serialized_patch(
                    "fill_color", arguments.get("fill_color") or [1, 1, 1, 1]
                ),
                "Pattern": arguments.get("pattern") or "checkerboard",
                "Palette": [
                    McpServer._serialized_patch("palette", color) for color in palette[:32]
                ],
                "PatternSize": McpServer._bounded_int(
                    arguments.get("pattern_size"), 8, 1, 1024
                ),
                "ImagePath": arguments.get("image_path") or "",
                "GradientType": arguments.get("gradient_type") or "linear",
                "GradientAngle": float(arguments.get("gradient_angle") or 0.0),
                "NoiseScale": float(arguments.get("noise_scale") or 0.1),
                "Octaves": McpServer._bounded_int(arguments.get("octaves"), 1, 1, 8),
                "ImportSettings": McpServer._serialized_patches(
                    arguments.get("import_settings")
                ),
            }
            pixels = arguments.get("pixels")
            if pixels is not None:
                mapped["PixelsBase64"] = McpServer._pixel_payload(pixels)
            region = arguments.get("set_pixels")
            if region is not None:
                if not isinstance(region, dict):
                    raise ValueError("set_pixels must be an object.")
                mapped["RegionX"] = McpServer._bounded_int(region.get("x"), 0, 0, 4095)
                mapped["RegionY"] = McpServer._bounded_int(region.get("y"), 0, 0, 4095)
                mapped["RegionWidth"] = McpServer._bounded_int(
                    region.get("width"), 1, 1, 4096
                )
                mapped["RegionHeight"] = McpServer._bounded_int(
                    region.get("height"), 1, 1, 4096
                )
                if "color" in region:
                    mapped["RegionColor"] = McpServer._serialized_patch(
                        "color", region["color"]
                    )
                if "pixels" in region:
                    mapped["RegionPixelsBase64"] = McpServer._pixel_payload(
                        region["pixels"]
                    )
            sprite = arguments.get("as_sprite")
            if isinstance(sprite, bool):
                mapped["AsSprite"] = sprite
            elif isinstance(sprite, dict):
                mapped["AsSprite"] = True
                mapped["SpritePixelsPerUnit"] = float(
                    sprite.get("pixels_per_unit", 100.0)
                )
                pivot = sprite.get("pivot", [0.5, 0.5])
                mapped["SpritePivot"] = McpServer._serialized_patch("pivot", pivot)
            return "manage_texture", mapped
        if name == "manage_prefabs":
            action = str(arguments.get("action") or "").lower()
            allowed = {
                "create_from_gameobject", "get_info", "get_hierarchy", "modify_contents",
                "open_prefab_stage", "save_prefab_stage", "close_prefab_stage",
            }
            if action not in allowed:
                raise ValueError("Unsupported manage_prefabs action.")
            target = arguments.get("target")
            mapped = {
                "Action": action,
                "PrefabPath": arguments.get("prefab_path") or "",
                "AllowOverwrite": McpServer._as_bool(
                    arguments.get("allow_overwrite"), False
                ),
                "SearchInactive": McpServer._as_bool(
                    arguments.get("search_inactive"), True
                ),
                "UnlinkIfInstance": McpServer._as_bool(
                    arguments.get("unlink_if_instance"), False
                ),
                "ComponentsToAdd": McpServer._string_list(
                    arguments.get("components_to_add")
                ),
                "ComponentsToRemove": McpServer._string_list(
                    arguments.get("components_to_remove")
                ),
                "DeleteChildren": McpServer._string_list(
                    arguments.get("delete_child")
                ),
                "Parent": arguments.get("parent") or "",
                "Name": arguments.get("name") or "",
                "Tag": arguments.get("tag") or "",
                "Layer": arguments.get("layer") or "",
            }
            if isinstance(target, int) and not isinstance(target, bool):
                mapped["TargetInstanceId"] = target
            elif target is not None:
                mapped["Target"] = str(target)
            for source, target_name, flag in [
                ("position", "Position", "HasPosition"),
                ("rotation", "Rotation", "HasRotation"),
                ("scale", "Scale", "HasScale"),
            ]:
                if source in arguments:
                    value = arguments[source]
                    if isinstance(value, str):
                        try:
                            value = json.loads(value)
                        except json.JSONDecodeError as error:
                            raise ValueError(f"{source} must contain valid JSON.") from error
                    vector = McpServer._vector_object(value, source, 3)
                    mapped[target_name] = [vector["x"], vector["y"], vector["z"]]
                    mapped[flag] = True
            if "set_active" in arguments:
                mapped["SetActive"] = McpServer._as_bool(
                    arguments.get("set_active"), True
                )
                mapped["HasSetActive"] = True
            children = arguments.get("create_child")
            if isinstance(children, dict):
                children = [children]
            if children is None:
                children = []
            if not isinstance(children, list):
                raise ValueError("create_child must be an object or list.")
            mapped["CreateChildren"] = [
                McpServer._prefab_child(child) for child in children
            ]
            component_properties = arguments.get("component_properties") or {}
            if not isinstance(component_properties, dict):
                raise ValueError("component_properties must be an object.")
            component_patches = []
            for component_type, properties in component_properties.items():
                for patch in McpServer._serialized_patches(properties):
                    component_patches.append(
                        {"ComponentType": str(component_type), "Property": patch}
                    )
            mapped["ComponentPatches"] = component_patches
            return "manage_prefabs", mapped
        if name == "manage_animation":
            return "manage_animation", McpServer._map_animation_arguments(arguments)
        if name == "manage_camera":
            return "manage_camera", McpServer._map_camera_arguments(arguments)
        if name == "manage_physics":
            return "manage_physics", McpServer._map_physics_arguments(arguments)
        if name == "manage_graphics":
            return "manage_graphics", McpServer._map_graphics_arguments(arguments)
        if name == "manage_profiler":
            return "manage_profiler", McpServer._map_profiler_arguments(arguments)
        if name == "manage_ui":
            return "manage_ui", McpServer._map_ui_arguments(arguments)
        if name == "manage_vfx":
            return "manage_vfx", McpServer._map_vfx_arguments(arguments)
        if name == "manage_probuilder":
            return "manage_probuilder", McpServer._map_probuilder_arguments(arguments)
        if name == "run_tests":
            mode = str(arguments.get("mode") or "EditMode")
            if mode.lower() not in {"editmode", "playmode"}:
                raise ValueError("run_tests mode must be EditMode or PlayMode.")
            return "run_tests", {
                "Mode": "PlayMode" if mode.lower() == "playmode" else "EditMode",
                "TestNames": McpServer._string_list(arguments.get("test_names")),
                "GroupNames": McpServer._string_list(arguments.get("group_names")),
                "CategoryNames": McpServer._string_list(
                    arguments.get("category_names")
                ),
                "AssemblyNames": McpServer._string_list(
                    arguments.get("assembly_names")
                ),
                "IncludeFailedTests": McpServer._as_bool(
                    arguments.get("include_failed_tests"), False
                ),
                "IncludeDetails": McpServer._as_bool(
                    arguments.get("include_details"), False
                ),
                "ClearStuck": McpServer._as_bool(
                    arguments.get("clear_stuck"), False
                ),
            }
        if name == "manage_gameobject":
            return "manage_gameobject", McpServer._map_upstream_gameobject(arguments)
        if name == "manage_components":
            return "manage_component", McpServer._map_upstream_component(arguments)
        if name == "unity2019_ping":
            return "ping", {}
        if name == "unity2019_editor_state":
            return "editor_state", {}
        if name == "unity2019_read_console":
            return "read_console", {
                "Limit": arguments.get("limit", 50),
                "Types": arguments.get("types", []),
            }
        if name == "unity2019_get_hierarchy":
            return "get_hierarchy", {
                "MaxDepth": arguments.get("max_depth", 4),
                "Offset": McpServer._parse_cursor(arguments.get("cursor")),
                "PageSize": arguments.get(
                    "page_size", arguments.get("max_items", 200)
                ),
            }
        if name == "unity2019_find_gameobjects":
            query = arguments.get("query")
            if not isinstance(query, str) or not query.strip():
                raise ValueError("query must be a non-empty string.")
            return "find_gameobjects", {
                "Query": query,
                "IncludeInactive": arguments.get("include_inactive", True),
                "Offset": McpServer._parse_cursor(arguments.get("cursor")),
                "PageSize": arguments.get(
                    "page_size", arguments.get("max_results", 100)
                ),
            }
        if name == "unity2019_refresh_assets":
            return "refresh_assets", {}
        if name == "unity2019_play_mode":
            action = arguments.get("action")
            if action not in {"play", "stop", "pause", "resume"}:
                raise ValueError("action must be play, stop, pause, or resume.")
            return "play_mode", {"Action": action}
        if name == "unity2019_undo_redo":
            action = arguments.get("action")
            if action not in {"undo", "redo"}:
                raise ValueError("action must be undo or redo.")
            return "undo_redo", {"Action": action}
        if name == "unity2019_reload_active_scene":
            confirmed = arguments.get("confirm_discard_changes")
            if confirmed is not True:
                raise ValueError(
                    "reload_active_scene requires confirm_discard_changes=true."
                )
            return "reload_active_scene", {"ConfirmDiscardChanges": True}
        if name == "unity2019_manage_gameobject":
            return "manage_gameobject", McpServer._map_gameobject_arguments(arguments)
        if name == "unity2019_manage_component":
            return "manage_component", McpServer._map_component_arguments(arguments)
        raise ValueError(f"Unknown tool: {name}")

    @staticmethod
    def _map_upstream_gameobject(arguments: dict[str, Any]) -> dict[str, Any]:
        action = str(arguments.get("action") or "").lower()
        if action not in {
            "create", "modify", "delete", "duplicate", "move_relative", "look_at"
        }:
            raise ValueError(
                "action must be create, modify, delete, duplicate, move_relative, or look_at."
            )
        mapped: dict[str, Any] = {
            "Action": action,
            "Confirm": action == "delete",
        }
        if action == "create":
            mapped["Name"] = str(arguments.get("name") or "GameObject")
            mapped["Primitive"] = str(arguments.get("primitive_type") or "empty")
        else:
            target = arguments.get("target")
            if isinstance(target, int) and not isinstance(target, bool):
                mapped["TargetInstanceId"] = target
            elif target is not None:
                mapped["Target"] = str(target)
            mapped["SearchMethod"] = str(arguments.get("search_method") or "")

        if "name" in arguments and action != "create":
            mapped["HasName"] = True
            mapped["Name"] = str(arguments["name"])
        if "set_active" in arguments:
            mapped["HasActive"] = True
            mapped["Active"] = McpServer._as_bool(arguments.get("set_active"), False)
        if "tag" in arguments:
            mapped["HasTag"] = True
            mapped["Tag"] = str(arguments.get("tag") or "")
        if "layer" in arguments:
            mapped["HasLayer"] = True
            mapped["Layer"] = str(arguments.get("layer") or "")
        if "is_static" in arguments:
            mapped["HasStatic"] = True
            mapped["IsStatic"] = McpServer._as_bool(arguments.get("is_static"), False)
        if "parent" in arguments:
            mapped["HasParent"] = True
            parent = arguments.get("parent")
            if isinstance(parent, int) and not isinstance(parent, bool):
                mapped["ParentInstanceId"] = parent
            elif parent not in {None, ""}:
                mapped["ParentTarget"] = str(parent)

        for source, target_name, flag in (
            ("position", "Position", "HasPosition"),
            ("rotation", "RotationEuler", "HasRotation"),
            ("scale", "Scale", "HasScale"),
            ("offset", "Offset", "HasOffset"),
            ("look_at_up", "LookAtUp", "HasLookAtUp"),
        ):
            if source in arguments and arguments[source] is not None:
                mapped[flag] = True
                mapped[target_name] = McpServer._vector_object(
                    arguments[source], source, 3
                )

        components_to_add = arguments.get("components_to_add")
        if components_to_add is not None:
            mapped["ComponentsToAdd"] = McpServer._component_mutations(
                components_to_add
            )
        components_to_remove = arguments.get("components_to_remove")
        if components_to_remove is not None:
            mapped["ComponentsToRemove"] = McpServer._string_list(
                components_to_remove
            )
        component_properties = arguments.get("component_properties")
        if component_properties is not None:
            if isinstance(component_properties, str):
                try:
                    component_properties = json.loads(component_properties)
                except json.JSONDecodeError as error:
                    raise ValueError(
                        "component_properties must contain valid JSON."
                    ) from error
            if not isinstance(component_properties, dict):
                raise ValueError("component_properties must be an object.")
            mapped["ComponentProperties"] = [
                {
                    "TypeName": str(component_type),
                    "Properties": McpServer._serialized_patches(properties),
                }
                for component_type, properties in component_properties.items()
            ]

        if "save_as_prefab" in arguments:
            mapped["SaveAsPrefab"] = McpServer._as_bool(
                arguments.get("save_as_prefab"), False
            )
        mapped["PrefabPath"] = str(arguments.get("prefab_path") or "")
        mapped["PrefabFolder"] = str(arguments.get("prefab_folder") or "")
        mapped["NewName"] = str(arguments.get("new_name") or "")
        mapped["Direction"] = str(arguments.get("direction") or "")
        if "distance" in arguments and arguments.get("distance") is not None:
            mapped["Distance"] = float(arguments["distance"])
            mapped["HasDistance"] = True
        mapped["WorldSpace"] = McpServer._as_bool(
            arguments.get("world_space"), True
        )

        reference = arguments.get("reference_object")
        if reference is not None:
            if isinstance(reference, int) and not isinstance(reference, bool):
                mapped["ReferenceInstanceId"] = reference
            else:
                mapped["ReferenceTarget"] = str(reference)

        look_at = arguments.get("look_at_target")
        if look_at is not None:
            vector_value: Any = look_at
            if isinstance(look_at, str):
                try:
                    parsed = json.loads(look_at)
                    vector_value = parsed if isinstance(parsed, (list, dict)) else None
                except json.JSONDecodeError:
                    vector_value = None
            if isinstance(vector_value, (list, dict)):
                mapped["HasLookAtPosition"] = True
                mapped["LookAtPosition"] = McpServer._vector_object(
                    vector_value, "look_at_target", 3
                )
            elif isinstance(look_at, int) and not isinstance(look_at, bool):
                mapped["LookAtInstanceId"] = look_at
            else:
                mapped["LookAtTarget"] = str(look_at)
        return mapped

    @staticmethod
    def _map_upstream_component(arguments: dict[str, Any]) -> dict[str, Any]:
        target = arguments.get("target")
        action = arguments.get("action")
        mapped: dict[str, Any] = {
            "Action": action,
            "ComponentType": arguments.get("component_type"),
            "ComponentIndex": arguments.get("component_index", 0),
            "Confirm": action == "remove",
            "SearchMethod": arguments.get("search_method") or "",
        }
        if isinstance(target, int) and not isinstance(target, bool):
            mapped["InstanceId"] = target
        elif target is not None:
            mapped["Target"] = str(target)
        if action == "set_property":
            properties = arguments.get("properties")
            if isinstance(properties, str):
                try:
                    properties = json.loads(properties)
                except json.JSONDecodeError as error:
                    raise ValueError("properties must contain valid JSON.") from error
            if properties is not None and not isinstance(properties, dict):
                raise ValueError("properties must be an object or JSON object string.")
            patches = McpServer._serialized_patches(properties)
            property_name = arguments.get("property")
            if property_name is not None:
                if "value" not in arguments:
                    raise ValueError("property requires value for set_property.")
                patches.append(
                    McpServer._serialized_patch(str(property_name), arguments["value"])
                )
            if not patches:
                raise ValueError("set_property requires property/value or properties.")
            mapped["Properties"] = patches
        elif action == "add" and arguments.get("properties") is not None:
            mapped["Properties"] = McpServer._serialized_patches(
                arguments.get("properties")
            )
        return mapped

    @staticmethod
    def _map_resource(uri: str) -> tuple[str, dict[str, Any]]:
        if uri in {"unity2019://editor/state", "mcpforunity://editor/state"}:
            return "editor_state", {}

        exact_kinds = {
            "mcpforunity://custom-tools": "custom_tools",
            "mcpforunity://editor/active-tool": "editor_active_tool",
            "mcpforunity://editor/prefab-stage": "editor_prefab_stage",
            "mcpforunity://editor/selection": "editor_selection",
            "mcpforunity://editor/windows": "editor_windows",
            "mcpforunity://instances": "instances",
            "mcpforunity://menu-items": "menu_items",
            "mcpforunity://pipeline/renderer-features": "renderer_features",
            "mcpforunity://prefab-api": "prefab_api",
            "mcpforunity://project/info": "project_info",
            "mcpforunity://project/layers": "project_layers",
            "mcpforunity://project/tags": "project_tags",
            "mcpforunity://rendering/stats": "rendering_stats",
            "mcpforunity://scene/cameras": "scene_cameras",
            "mcpforunity://scene/gameobject-api": "gameobject_api",
            "mcpforunity://scene/volumes": "scene_volumes",
            "mcpforunity://tests": "tests",
            "mcpforunity://tool-groups": "tool_groups",
        }
        if uri in exact_kinds:
            return "resource_snapshot", {"Kind": exact_kinds[uri]}

        parsed = urlparse(uri)
        if parsed.scheme in {"unity2019", "mcpforunity"} and parsed.netloc == "scene":
            parts = [part for part in parsed.path.split("/") if part]
            if len(parts) == 2 and parts[0] == "gameobject":
                try:
                    instance_id = int(parts[1])
                except ValueError as error:
                    raise ValueError(
                        "GameObject resource instance_id must be an integer."
                    ) from error
                if instance_id == 0:
                    raise ValueError("GameObject resource instance_id cannot be zero.")
                return "get_gameobject", {"InstanceId": instance_id}
            if len(parts) == 3 and parts[0] == "gameobject" and parts[2] == "components":
                try:
                    instance_id = int(parts[1])
                except ValueError as error:
                    raise ValueError(
                        "GameObject resource instance_id must be an integer."
                    ) from error
                return "resource_snapshot", {
                    "Kind": "gameobject_components",
                    "InstanceId": instance_id,
                }
            if len(parts) == 4 and parts[0] == "gameobject" and parts[2] == "component":
                try:
                    instance_id = int(parts[1])
                except ValueError as error:
                    raise ValueError(
                        "GameObject resource instance_id must be an integer."
                    ) from error
                return "resource_snapshot", {
                    "Kind": "gameobject_component",
                    "InstanceId": instance_id,
                    "ComponentName": unquote(parts[3]),
                }

        if parsed.scheme == "mcpforunity" and parsed.netloc == "prefab":
            raw_path = parsed.path.lstrip("/")
            hierarchy = raw_path.endswith("/hierarchy")
            if hierarchy:
                raw_path = raw_path[: -len("/hierarchy")]
            asset_path = unquote(raw_path)
            if not asset_path:
                raise ValueError("Prefab resource requires an encoded Assets path.")
            return "resource_snapshot", {
                "Kind": "prefab_hierarchy" if hierarchy else "prefab_info",
                "AssetPath": asset_path,
            }

        if parsed.scheme == "mcpforunity" and parsed.netloc == "tests":
            mode = parsed.path.strip("/").lower()
            if mode not in {"editmode", "playmode"}:
                raise ValueError("Test resource mode must be editmode or playmode.")
            return "resource_snapshot", {"Kind": "tests", "Mode": mode}

        raise ValueError(f"Unknown Unity 2019 resource URI: {uri}")

    @staticmethod
    def _map_gameobject_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        action = arguments.get("action")
        if action not in {"create", "modify", "delete"}:
            raise ValueError("action must be create, modify, or delete.")

        mapped: dict[str, Any] = {
            "Action": action,
            "Confirm": arguments.get("confirm", False),
        }
        if action == "create":
            name = arguments.get("name")
            if not isinstance(name, str) or not name.strip():
                raise ValueError("create requires a non-empty name.")
            if len(name.strip()) > 200:
                raise ValueError("name must contain at most 200 characters.")
            primitive = arguments.get("primitive", "empty")
            if primitive not in {
                "empty",
                "Cube",
                "Sphere",
                "Capsule",
                "Cylinder",
                "Plane",
                "Quad",
            }:
                raise ValueError("primitive is not supported.")
            mapped["Name"] = name
            mapped["Primitive"] = primitive
        else:
            mapped["InstanceId"] = McpServer._require_instance_id(arguments)

        optional_flags = {
            "name": ("HasName", "Name"),
            "parent_instance_id": ("HasParent", "ParentInstanceId"),
            "active": ("HasActive", "Active"),
            "position": ("HasPosition", "Position"),
            "rotation_euler": ("HasRotation", "RotationEuler"),
            "scale": ("HasScale", "Scale"),
        }
        for source, (flag, target) in optional_flags.items():
            if source in arguments:
                mapped[flag] = True
                value = arguments[source]
                if source in {"position", "rotation_euler", "scale"}:
                    value = McpServer._vector_object(value, source, 3)
                elif source == "parent_instance_id" and (
                    not isinstance(value, int) or isinstance(value, bool)
                ):
                    raise ValueError("parent_instance_id must be an integer.")
                elif source == "active" and not isinstance(value, bool):
                    raise ValueError("active must be a boolean.")
                mapped[target] = value

        changed_fields = [field for field in optional_flags if field in arguments]
        if action == "modify" and not changed_fields:
            raise ValueError("modify requires at least one changed field.")
        if action == "delete" and changed_fields:
            raise ValueError("delete does not accept modification fields.")
        if action == "delete" and not mapped["Confirm"]:
            raise ValueError("delete requires confirm=true.")
        return mapped

    @staticmethod
    def _map_component_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        action = arguments.get("action")
        if action not in {"add", "remove", "set_property"}:
            raise ValueError("action must be add, remove, or set_property.")
        component_type = arguments.get("component_type")
        if not isinstance(component_type, str) or not component_type.strip():
            raise ValueError("component_type must be a non-empty string.")

        component_index = arguments.get("component_index", 0)
        if (
            not isinstance(component_index, int)
            or isinstance(component_index, bool)
            or component_index < 0
        ):
            raise ValueError("component_index must be a non-negative integer.")

        mapped: dict[str, Any] = {
            "Action": action,
            "InstanceId": McpServer._require_instance_id(arguments),
            "ComponentType": component_type,
            "ComponentIndex": component_index,
            "Confirm": arguments.get("confirm", False),
        }
        if action == "remove" and not mapped["Confirm"]:
            raise ValueError("remove requires confirm=true.")
        if action != "set_property":
            return mapped

        property_path = arguments.get("property_path")
        if not isinstance(property_path, str) or not property_path.strip():
            raise ValueError("set_property requires property_path.")
        if "value" not in arguments:
            raise ValueError("set_property requires value.")
        mapped["PropertyPath"] = property_path

        value = arguments["value"]
        if isinstance(value, bool):
            mapped.update({"ValueKind": "boolean", "BoolValue": value})
        elif isinstance(value, int):
            mapped.update({"ValueKind": "integer", "IntValue": value})
        elif isinstance(value, float):
            mapped.update({"ValueKind": "number", "FloatValue": value})
        elif isinstance(value, str):
            mapped.update({"ValueKind": "string", "StringValue": value})
        elif isinstance(value, list) and 2 <= len(value) <= 4:
            if not all(
                isinstance(item, (int, float)) and not isinstance(item, bool)
                for item in value
            ):
                raise ValueError("vector value elements must be numbers.")
            mapped.update(
                {
                    "ValueKind": "vector",
                    "VectorLength": len(value),
                    "VectorValue": McpServer._vector_object(value, "value", 4),
                }
            )
        else:
            raise ValueError(
                "value must be a boolean, number, string, or numeric array of length 2 to 4."
            )
        return mapped

    @staticmethod
    def _require_instance_id(arguments: dict[str, Any]) -> int:
        instance_id = arguments.get("instance_id")
        if not isinstance(instance_id, int) or isinstance(instance_id, bool) or instance_id == 0:
            raise ValueError("instance_id must be a non-zero integer.")
        return instance_id

    @staticmethod
    def _parse_cursor(cursor: Any) -> int:
        if cursor is None or cursor == "":
            return 0
        if isinstance(cursor, int) and not isinstance(cursor, bool) and cursor >= 0:
            return cursor
        if not isinstance(cursor, str) or not cursor.isdigit():
            raise ValueError("cursor must contain only a non-negative decimal integer.")
        return int(cursor)

    @staticmethod
    def _vector_object(value: Any, field: str, target_length: int) -> dict[str, float]:
        if isinstance(value, str):
            try:
                value = json.loads(value)
            except json.JSONDecodeError as error:
                raise ValueError(
                    f"{field} must be a numeric array or vector object."
                ) from error
        if isinstance(value, dict):
            keys = [key for key in ("x", "y", "z", "w") if key in value]
            if not keys:
                keys = [key for key in ("r", "g", "b", "a") if key in value]
            value = [value[key] for key in keys]
        if not isinstance(value, (list, tuple)):
            raise ValueError(f"{field} must be a numeric array or vector object.")
        if field != "value" and len(value) != target_length:
            raise ValueError(f"{field} must contain exactly {target_length} numbers.")
        if not all(
            isinstance(item, (int, float)) and not isinstance(item, bool)
            for item in value
        ):
            raise ValueError(f"{field} elements must be numbers.")

        padded = [float(item) for item in value] + [0.0] * (target_length - len(value))
        keys = ["x", "y", "z", "w"]
        return {keys[index]: padded[index] for index in range(target_length)}

    @staticmethod
    def _component_mutations(value: Any) -> list[dict[str, Any]]:
        if isinstance(value, str):
            stripped = value.strip()
            if stripped.startswith("[") or stripped.startswith("{"):
                try:
                    value = json.loads(stripped)
                except json.JSONDecodeError as error:
                    raise ValueError("components_to_add contains invalid JSON.") from error
            else:
                value = [value]
        if isinstance(value, dict):
            value = [value]
        if not isinstance(value, (list, tuple)):
            raise ValueError("components_to_add must be a string, object, or array.")
        result: list[dict[str, Any]] = []
        for index, item in enumerate(value):
            if isinstance(item, str):
                type_name = item.strip()
                properties = None
            elif isinstance(item, dict):
                type_name = str(
                    item.get("typeName") or item.get("type_name") or ""
                ).strip()
                properties = item.get("properties")
            else:
                raise ValueError(
                    f"components_to_add[{index}] must be a string or object."
                )
            if not type_name:
                raise ValueError(
                    f"components_to_add[{index}] requires typeName."
                )
            result.append(
                {
                    "TypeName": type_name,
                    "Properties": McpServer._serialized_patches(properties),
                }
            )
        return result

    @staticmethod
    def _serialized_patches(value: Any) -> list[dict[str, Any]]:
        if value is None:
            return []
        if isinstance(value, str):
            try:
                value = json.loads(value)
            except json.JSONDecodeError as error:
                raise ValueError("properties must contain a valid JSON object.") from error
        if not isinstance(value, dict):
            raise ValueError("properties must be an object or JSON object string.")
        return [
            McpServer._serialized_patch(str(path), item)
            for path, item in value.items()
        ]

    @staticmethod
    def _serialized_patch(path: str, value: Any) -> dict[str, Any]:
        patch: dict[str, Any] = {"Path": path}
        if value is None:
            patch["Kind"] = "null"
        elif isinstance(value, bool):
            patch.update({"Kind": "bool", "BoolValue": value})
        elif isinstance(value, int):
            patch.update({"Kind": "int", "IntValue": value, "FloatValue": float(value)})
        elif isinstance(value, float):
            patch.update({"Kind": "float", "FloatValue": value})
        elif isinstance(value, str):
            patch.update({"Kind": "string", "StringValue": value})
        elif isinstance(value, list) and 2 <= len(value) <= 4 and all(
            isinstance(item, (int, float)) and not isinstance(item, bool)
            for item in value
        ):
            patch.update(
                {
                    "Kind": "vector",
                    "VectorLength": len(value),
                    "FloatValues": [float(item) for item in value],
                }
            )
        elif isinstance(value, dict):
            if "path" in value or "guid" in value:
                patch.update(
                    {
                        "Kind": "reference",
                        "ReferencePath": str(value.get("path") or ""),
                        "ReferenceGuid": str(value.get("guid") or ""),
                    }
                )
            else:
                keys = (
                    [key for key in ("x", "y", "z", "w") if key in value]
                    or [key for key in ("r", "g", "b", "a") if key in value]
                )
                if len(keys) not in {2, 3, 4} or not all(
                    isinstance(value[key], (int, float)) and not isinstance(value[key], bool)
                    for key in keys
                ):
                    raise ValueError(f"Unsupported serialized value for property {path}.")
                patch.update(
                    {
                        "Kind": "vector",
                        "VectorLength": len(keys),
                        "FloatValues": [float(value[key]) for key in keys],
                    }
                )
        else:
            raise ValueError(f"Unsupported serialized value for property {path}.")
        return patch

    @staticmethod
    def _pixel_payload(value: Any) -> str:
        if isinstance(value, str):
            try:
                decoded = base64.b64decode(value, validate=True)
            except (ValueError, TypeError) as error:
                raise ValueError("pixels string must be valid base64 RGBA bytes.") from error
            if len(decoded) > 64 * 1024 * 1024:
                raise ValueError("pixels payload exceeds 64 MiB.")
            return value
        if not isinstance(value, list):
            raise ValueError("pixels must be a list of RGBA arrays or base64 string.")
        raw = bytearray()
        for index, pixel in enumerate(value):
            if not isinstance(pixel, list) or len(pixel) not in {3, 4}:
                raise ValueError(f"Pixel {index} must contain 3 or 4 numeric channels.")
            channels = list(pixel) + ([255] if len(pixel) == 3 else [])
            for channel in channels:
                if not isinstance(channel, (int, float)) or isinstance(channel, bool):
                    raise ValueError(f"Pixel {index} contains a non-numeric channel.")
                normalized = float(channel)
                if 0.0 <= normalized <= 1.0 and isinstance(channel, float):
                    normalized *= 255.0
                raw.append(max(0, min(255, int(round(normalized)))))
            if len(raw) > 64 * 1024 * 1024:
                raise ValueError("pixels payload exceeds 64 MiB.")
        return base64.b64encode(bytes(raw)).decode("ascii")

    @staticmethod
    def _string_list(value: Any) -> list[str]:
        if value is None:
            return []
        if isinstance(value, str):
            try:
                parsed = json.loads(value)
                value = parsed if isinstance(parsed, list) else [value]
            except json.JSONDecodeError:
                value = [value]
        if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
            raise ValueError("Expected a string or list of strings.")
        return [item for item in value if item.strip()]

    @staticmethod
    def _prefab_child(value: Any) -> dict[str, Any]:
        if not isinstance(value, dict):
            raise ValueError("Each create_child entry must be an object.")
        name = value.get("name")
        if not isinstance(name, str) or not name.strip():
            raise ValueError("Each create_child entry requires name.")
        result: dict[str, Any] = {
            "Name": name.strip(),
            "Parent": value.get("parent") or "",
            "SourcePrefabPath": value.get("source_prefab_path") or "",
            "PrimitiveType": value.get("primitive_type") or "",
            "ComponentsToAdd": McpServer._string_list(value.get("components_to_add")),
            "Tag": value.get("tag") or "",
            "Layer": value.get("layer") or "",
            "SetActive": McpServer._as_bool(value.get("set_active"), True),
        }
        for source, target in [
            ("position", "Position"),
            ("rotation", "Rotation"),
            ("scale", "Scale"),
        ]:
            raw = value.get(source)
            if raw is None:
                raw = [1, 1, 1] if source == "scale" else [0, 0, 0]
            vector = McpServer._vector_object(raw, source, 3)
            result[target] = [vector["x"], vector["y"], vector["z"]]
        return result

    @staticmethod
    def _map_animation_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "animator_get_info", "animator_get_parameter", "animator_play",
            "animator_crossfade", "animator_set_parameter", "animator_set_speed",
            "animator_set_enabled", "controller_create", "controller_add_state",
            "controller_add_transition", "controller_add_parameter",
            "controller_get_info", "controller_assign", "controller_add_layer",
            "controller_remove_layer", "controller_set_layer_weight",
            "controller_create_blend_tree_1d", "controller_create_blend_tree_2d",
            "controller_add_blend_tree_child", "clip_create", "clip_get_info",
            "clip_add_curve", "clip_set_curve", "clip_set_vector_curve",
            "clip_create_preset", "clip_assign", "clip_add_event",
            "clip_remove_event",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError(
                "Unknown manage_animation action. Use animator_*, controller_*, or clip_* actions."
            )

        raw_properties = arguments.get("properties")
        if isinstance(raw_properties, str):
            try:
                raw_properties = json.loads(raw_properties)
            except json.JSONDecodeError as error:
                raise ValueError(f"properties must contain a JSON object: {error}") from error
        if raw_properties is None:
            raw_properties = {}
        if not isinstance(raw_properties, dict):
            raise ValueError("properties must be an object or a JSON object string.")

        source = dict(raw_properties)
        for key in ("target", "search_method", "clip_path", "controller_path"):
            if arguments.get(key) is not None:
                source[key] = arguments[key]

        mapped: dict[str, Any] = {"Action": action}
        aliases = {
            "target": "Target", "search_method": "SearchMethod",
            "searchmethod": "SearchMethod", "clip_path": "ClipPath",
            "clippath": "ClipPath", "controller_path": "ControllerPath",
            "controllerpath": "ControllerPath", "state_name": "StateName",
            "statename": "StateName", "from_state": "FromState",
            "fromstate": "FromState", "to_state": "ToState", "tostate": "ToState",
            "parameter_name": "ParameterName", "parametername": "ParameterName",
            "parameter_type": "ParameterType", "parametertype": "ParameterType",
            "property_path": "PropertyPath", "propertypath": "PropertyPath",
            "relative_path": "RelativePath", "relativepath": "RelativePath",
            "layer_name": "LayerName", "layername": "LayerName",
            "blending_mode": "BlendingMode", "blendingmode": "BlendingMode",
            "blend_parameter": "BlendParameter", "blendparameter": "BlendParameter",
            "blend_parameter_x": "BlendParameterX", "blendparameterx": "BlendParameterX",
            "blend_parameter_y": "BlendParameterY", "blendparametery": "BlendParameterY",
            "blend_type": "BlendType", "blendtype": "BlendType",
            "function_name": "FunctionName", "functionname": "FunctionName",
            "string_parameter": "StringParameter", "stringparameter": "StringParameter",
            "frame_rate": "FrameRate", "framerate": "FrameRate",
            "has_exit_time": "HasExitTime", "hasexittime": "HasExitTime",
            "exit_time": "ExitTime", "exittime": "ExitTime",
            "is_default": "IsDefault", "isdefault": "IsDefault",
            "float_parameter": "FloatParameter", "floatparameter": "FloatParameter",
            "int_parameter": "IntParameter", "intparameter": "IntParameter",
            "event_index": "EventIndex", "eventindex": "EventIndex",
            "layer_index": "LayerIndex", "layerindex": "LayerIndex",
        }

        strings = {
            "Name", "StateName", "FromState", "ToState", "ParameterName",
            "ParameterType", "PropertyPath", "Property", "Type", "RelativePath",
            "LayerName", "BlendingMode", "BlendParameter", "BlendParameterX",
            "BlendParameterY", "BlendType", "FunctionName", "StringParameter",
            "Preset",
        }
        integers = {"Layer", "LayerIndex", "IntParameter", "EventIndex"}
        numbers = {
            "Duration", "Speed", "Length", "FrameRate", "ExitTime", "Weight",
            "Threshold", "Time", "FloatParameter", "Amplitude",
        }
        booleans = {"Enabled", "Loop", "IsDefault", "HasExitTime"}

        def field_name(key: Any) -> str:
            text_key = str(key).strip()
            compact = text_key.replace("-", "_")
            lowered = compact.lower()
            if lowered in aliases:
                return aliases[lowered]
            if "_" in compact:
                return "".join(part[:1].upper() + part[1:] for part in compact.split("_") if part)
            return compact[:1].upper() + compact[1:]

        for key, value in source.items():
            field = field_name(key)
            if value is None:
                continue
            if field == "Target":
                mapped[field] = str(value)
            elif field == "SearchMethod":
                mapped[field] = str(value).strip().lower()
            elif field in {"ClipPath", "ControllerPath"}:
                mapped[field] = str(value).replace("\\", "/")
            elif field in strings:
                mapped[field] = str(value)
            elif field in integers:
                mapped[field] = int(value)
                mapped["Has" + field] = True
            elif field in numbers:
                mapped[field] = float(value)
                mapped["HasExitTimeValue" if field == "ExitTime" else "Has" + field] = True
            elif field in booleans:
                mapped[field] = McpServer._as_bool(value, False)
                mapped["Has" + field] = True
            elif field in {"Offset", "Position"}:
                vector = McpServer._vector_object(value, field.lower(), 3 if field == "Offset" else 2)
                mapped[field] = [vector[axis] for axis in (("x", "y", "z") if field == "Offset" else ("x", "y"))]
                mapped["Has" + field] = True
            elif field == "Conditions":
                if not isinstance(value, list):
                    raise ValueError("properties.conditions must be an array.")
                conditions = []
                for item in value:
                    if not isinstance(item, dict):
                        raise ValueError("Each animation transition condition must be an object.")
                    conditions.append({
                        "Parameter": str(item.get("parameter") or ""),
                        "Mode": str(item.get("mode") or "greater"),
                        "Threshold": float(item.get("threshold") or 0.0),
                    })
                mapped[field] = conditions
            elif field == "Keys":
                if not isinstance(value, list):
                    raise ValueError("properties.keys must be an array.")
                keys = []
                for item in value:
                    if isinstance(item, (list, tuple)) and len(item) >= 2:
                        item = {"time": item[0], "value": item[1]}
                    if not isinstance(item, dict):
                        raise ValueError("Each animation key must be an object or [time, value].")
                    key_value = item.get("value", 0.0)
                    encoded: dict[str, Any] = {"Time": float(item.get("time") or 0.0)}
                    if isinstance(key_value, (list, tuple)):
                        if len(key_value) < 3:
                            raise ValueError("Vector animation key values require [x, y, z].")
                        encoded["VectorValue"] = [float(key_value[0]), float(key_value[1]), float(key_value[2])]
                        encoded["IsVector"] = True
                    else:
                        encoded["Value"] = float(key_value)
                    for source_name, target_name in (
                        ("inTangent", "InTangent"), ("in_tangent", "InTangent"),
                        ("outTangent", "OutTangent"), ("out_tangent", "OutTangent"),
                        ("inWeight", "InWeight"), ("in_weight", "InWeight"),
                        ("outWeight", "OutWeight"), ("out_weight", "OutWeight"),
                    ):
                        if source_name in item:
                            encoded[target_name] = float(item[source_name])
                            encoded["Has" + target_name] = True
                    keys.append(encoded)
                mapped[field] = keys
            elif field in {"Value", "DefaultValue"}:
                prefix = field
                mapped["Has" + prefix] = True
                if isinstance(value, bool):
                    mapped[prefix + "Kind"] = "bool"
                    mapped[prefix + "Bool"] = value
                elif isinstance(value, int):
                    mapped[prefix + "Kind"] = "int"
                    mapped[prefix + "Int"] = value
                    mapped[prefix + "Float"] = float(value)
                elif isinstance(value, float):
                    mapped[prefix + "Kind"] = "float"
                    mapped[prefix + "Float"] = value
                else:
                    mapped[prefix + "Kind"] = "string"
                    mapped[prefix + "String"] = str(value)

        return mapped

    @staticmethod
    def _map_camera_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "ensure_brain", "get_brain_status", "create_camera",
            "set_target", "set_priority", "set_lens", "set_body", "set_aim",
            "set_noise", "add_extension", "remove_extension", "set_blend",
            "force_camera", "release_override", "list_cameras", "screenshot",
            "screenshot_multiview",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_camera action.")
        properties = arguments.get("properties")
        if isinstance(properties, str):
            try:
                properties = json.loads(properties)
            except json.JSONDecodeError as error:
                raise ValueError(f"properties must contain a JSON object: {error}") from error
        if properties is None:
            properties = {}
        if not isinstance(properties, dict):
            raise ValueError("properties must be an object or JSON object string.")
        source = dict(properties)
        for source_name, property_name in {
            "screenshot_file_name": "file_name", "screenshot_super_size": "super_size",
            "camera": "camera", "include_image": "include_image",
            "max_resolution": "max_resolution", "capture_source": "capture_source",
            "batch": "batch", "view_target": "view_target",
            "view_position": "view_position", "view_rotation": "view_rotation",
            "orbit_angles": "orbit_angles", "orbit_elevations": "orbit_elevations",
            "orbit_distance": "orbit_distance", "orbit_fov": "orbit_fov",
            "output_folder": "output_folder",
        }.items():
            if arguments.get(source_name) is not None:
                source[property_name] = arguments[source_name]
        mapped: dict[str, Any] = {
            "Action": action,
            "Target": str(arguments.get("target") or ""),
            "SearchMethod": str(arguments.get("search_method") or "by_name"),
        }
        aliases = {
            "field_of_view": "FieldOfView", "fieldofview": "FieldOfView",
            "near_clip_plane": "NearClipPlane", "nearclipplane": "NearClipPlane",
            "far_clip_plane": "FarClipPlane", "farclipplane": "FarClipPlane",
            "orthographic_size": "OrthographicSize", "orthographicsize": "OrthographicSize",
            "look_at": "LookAt", "lookat": "LookAt", "extension_type": "ExtensionType",
            "file_name": "FileName", "super_size": "SuperSize", "include_image": "IncludeImage",
            "max_resolution": "MaxResolution", "capture_source": "CaptureSource",
            "view_target": "ViewTarget", "view_position": "ViewPosition",
            "view_rotation": "ViewRotation", "orbit_angles": "OrbitAngles",
            "orbit_elevations": "OrbitElevations", "orbit_distance": "OrbitDistance",
            "orbit_fov": "OrbitFov", "output_folder": "OutputFolder",
            "body_type": "BodyType", "aim_type": "AimType",
        }
        numbers = {"FieldOfView", "NearClipPlane", "FarClipPlane", "OrthographicSize", "Priority", "Duration", "Dutch", "OrbitDistance", "OrbitFov"}
        integers = {"SuperSize", "MaxResolution", "OrbitAngles"}
        booleans = {"Orthographic", "IncludeImage"}
        strings = {"Name", "Preset", "Follow", "LookAt", "ExtensionType", "Style", "Camera", "CaptureSource", "Batch", "ViewTarget", "OutputFolder", "BodyType", "AimType", "FileName"}
        for key, value in source.items():
            if value is None:
                continue
            raw = str(key).replace("-", "_")
            field = aliases.get(raw.lower())
            if field is None:
                field = "".join(part[:1].upper() + part[1:] for part in raw.split("_") if part)
            if field in numbers:
                mapped[field] = float(value); mapped["Has" + field] = True
            elif field in integers:
                mapped[field] = int(value); mapped["Has" + field] = True
            elif field in booleans:
                mapped[field] = McpServer._as_bool(value, False); mapped["Has" + field] = True
            elif field in {"Position", "Rotation", "ViewPosition", "ViewRotation"}:
                vector = McpServer._vector_object(value, field.lower(), 3)
                mapped[field] = [vector[axis] for axis in ("x", "y", "z")]
                mapped["Has" + field] = True
            elif field == "OrbitElevations":
                if not isinstance(value, list): raise ValueError("orbit_elevations must be an array.")
                mapped[field] = [float(item) for item in value]
            elif field in strings:
                mapped[field] = str(value)
        mapped["ComponentProperties"] = McpServer._serialized_patches(properties)
        return mapped

    @staticmethod
    def _map_physics_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "get_settings", "set_settings", "get_collision_matrix",
            "set_collision_matrix", "create_physics_material",
            "configure_physics_material", "assign_physics_material", "add_joint",
            "configure_joint", "remove_joint", "raycast", "raycast_all",
            "linecast", "shapecast", "overlap", "validate", "simulate_step",
            "apply_force", "get_rigidbody", "configure_rigidbody",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_physics action.")
        mapped: dict[str, Any] = {
            "Action": action,
            "Dimension": str(arguments.get("dimension") or "3d").lower(),
        }
        if mapped["Dimension"] not in {"2d", "3d"}:
            raise ValueError("dimension must be 2d or 3d.")
        aliases = {
            "layer_a": "LayerA", "layer_b": "LayerB", "dynamic_friction": "DynamicFriction",
            "static_friction": "StaticFriction", "friction_combine": "FrictionCombine",
            "bounce_combine": "BounceCombine", "material_path": "MaterialPath",
            "collider_type": "ColliderType", "search_method": "SearchMethod",
            "joint_type": "JointType", "connected_body": "ConnectedBody",
            "max_distance": "MaxDistance", "layer_mask": "LayerMask",
            "query_trigger_interaction": "QueryTriggerInteraction",
            "point1": "Point1", "point2": "Point2", "capsule_direction": "CapsuleDirection",
            "force_mode": "ForceMode", "force_type": "ForceType",
            "explosion_position": "ExplosionPosition", "explosion_radius": "ExplosionRadius",
            "explosion_force": "ExplosionForce", "upwards_modifier": "UpwardsModifier",
            "step_size": "StepSize", "page_size": "PageSize", "component_index": "ComponentIndex",
        }
        strings = {
            "LayerA", "LayerB", "Name", "Path", "FrictionCombine", "BounceCombine",
            "MaterialPath", "Target", "ColliderType", "SearchMethod", "JointType",
            "ConnectedBody", "LayerMask", "QueryTriggerInteraction", "Shape",
            "ForceMode", "ForceType",
        }
        numbers = {
            "DynamicFriction", "StaticFriction", "Bounciness", "Friction",
            "MaxDistance", "Height", "Angle", "ExplosionRadius", "ExplosionForce",
            "UpwardsModifier", "StepSize",
        }
        integers = {"CapsuleDirection", "Steps", "PageSize", "Cursor", "ComponentIndex"}
        vectors = {"Origin", "Direction", "Position", "Start", "End", "Point1", "Point2", "Force", "Torque", "ExplosionPosition"}
        for key, value in arguments.items():
            if key in {"action", "dimension", "settings", "properties", "motor", "limits", "spring", "drive"} or value is None:
                continue
            field = aliases.get(key)
            if field is None:
                field = "".join(part[:1].upper() + part[1:] for part in key.split("_") if part)
            if field in strings:
                mapped[field] = str(value)
            elif field in numbers:
                mapped[field] = float(value); mapped["Has" + field] = True
            elif field in integers:
                mapped[field] = int(value); mapped["Has" + field] = True
            elif field == "Collide":
                mapped[field] = McpServer._as_bool(value, True); mapped["HasCollide"] = True
            elif field in vectors:
                if not isinstance(value, list) or len(value) not in {1, 2, 3} or not all(
                    isinstance(item, (int, float)) and not isinstance(item, bool) for item in value
                ):
                    raise ValueError(f"{key} must contain one to three numeric values.")
                mapped[field] = [float(item) for item in value]
                mapped["Has" + field] = True
            elif field == "Size":
                if isinstance(value, (int, float)) and not isinstance(value, bool):
                    mapped["SizeScalar"] = float(value); mapped["HasSize"] = True
                elif isinstance(value, list) and len(value) in {2, 3}:
                    mapped["Size"] = [float(item) for item in value]; mapped["HasSize"] = True
                else:
                    raise ValueError("size must be a number or 2/3-element array.")
            elif field == "Positions":
                if not isinstance(value, list): raise ValueError("positions must be an array.")
                mapped[field] = [{"Values": [float(number) for number in item]} for item in value if isinstance(item, list)]
        for source, target in (
            ("settings", "Settings"), ("properties", "Properties"),
            ("motor", "Motor"), ("limits", "Limits"), ("spring", "Spring"), ("drive", "Drive"),
        ):
            mapped[target] = McpServer._serialized_patches(arguments.get(source))
        return mapped

    @staticmethod
    def _map_graphics_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "volume_create", "volume_add_effect", "volume_set_effect",
            "volume_remove_effect", "volume_get_info", "volume_set_properties",
            "volume_list_effects", "volume_create_profile", "bake_start",
            "bake_cancel", "bake_status", "bake_clear", "bake_reflection_probe",
            "bake_get_settings", "bake_set_settings", "bake_create_light_probe_group",
            "bake_create_reflection_probe", "bake_set_probe_positions", "stats_get",
            "stats_list_counters", "stats_set_scene_debug", "stats_get_memory",
            "pipeline_get_info", "pipeline_set_quality", "pipeline_get_settings",
            "pipeline_set_settings", "feature_list", "feature_add", "feature_remove",
            "feature_configure", "feature_toggle", "feature_reorder", "skybox_get",
            "skybox_set_material", "skybox_set_properties", "skybox_set_ambient",
            "skybox_set_fog", "skybox_set_reflection", "skybox_set_sun",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_graphics action.")
        mapped: dict[str, Any] = {"Action": action}
        aliases = {
            "is_global": "IsGlobal", "profile_path": "ProfilePath", "grid_size": "GridSize",
            "box_projection": "BoxProjection", "feature_type": "FeatureType",
            "ambient_mode": "AmbientMode", "equator_color": "EquatorColor",
            "ground_color": "GroundColor", "fog_enabled": "FogEnabled",
            "fog_mode": "FogMode", "fog_color": "FogColor", "fog_density": "FogDensity",
            "fog_start": "FogStart", "fog_end": "FogEnd", "reflection_mode": "ReflectionMode",
            "async_bake": "AsyncBake",
        }
        strings = {
            "Target", "Effect", "Name", "ProfilePath", "Path", "Level", "Mode",
            "FeatureType", "Material", "AmbientMode", "FogMode", "ReflectionMode",
        }
        booleans = {"IsGlobal", "Hdr", "BoxProjection", "Active", "AsyncBake", "FogEnabled"}
        floats = {"Weight", "Priority", "Spacing", "Intensity", "FogDensity", "FogStart", "FogEnd"}
        integers = {"Resolution", "Index", "Bounces"}
        vectors = {"Position", "Size", "Color", "EquatorColor", "GroundColor", "FogColor"}
        ignored = {"action", "parameters", "properties", "settings", "effects", "positions", "order", "grid_size"}
        for key, value in arguments.items():
            if key in ignored or value is None:
                continue
            field = aliases.get(key)
            if field is None:
                field = "".join(part[:1].upper() + part[1:] for part in key.split("_") if part)
            if field in strings:
                mapped[field] = str(value)
            elif field in booleans:
                mapped[field] = McpServer._as_bool(value, False)
                mapped["Has" + field] = True
            elif field in floats:
                mapped[field] = float(value)
                mapped["Has" + field] = True
            elif field in integers:
                mapped[field] = int(value)
                mapped["Has" + field] = True
            elif field in vectors:
                if not isinstance(value, list) or len(value) not in {2, 3, 4}:
                    raise ValueError(f"{key} must contain two to four numeric values.")
                mapped[field] = [float(item) for item in value]
                mapped["Has" + field] = True
        grid_size = arguments.get("grid_size")
        if grid_size is not None:
            if not isinstance(grid_size, list) or len(grid_size) != 3:
                raise ValueError("grid_size must contain three integers.")
            mapped["GridSize"] = [int(item) for item in grid_size]
            mapped["HasGridSize"] = True
        positions = arguments.get("positions")
        if positions is not None:
            if not isinstance(positions, list):
                raise ValueError("positions must be an array of vectors.")
            mapped["Positions"] = [
                {"Values": [float(number) for number in item]}
                for item in positions
                if isinstance(item, list) and len(item) == 3
            ]
        order = arguments.get("order")
        if order is not None:
            if not isinstance(order, list):
                raise ValueError("order must be an integer array.")
            mapped["Order"] = [int(item) for item in order]
        mapped["Parameters"] = McpServer._serialized_patches(arguments.get("parameters"))
        mapped["Properties"] = McpServer._serialized_patches(arguments.get("properties"))
        mapped["Settings"] = McpServer._serialized_patches(arguments.get("settings"))
        effects = arguments.get("effects")
        if effects is not None:
            if not isinstance(effects, list):
                raise ValueError("effects must be an array.")
            encoded_effects = []
            for index, item in enumerate(effects):
                if not isinstance(item, dict):
                    raise ValueError(f"effects[{index}] must be an object.")
                effect_name = item.get("effect") or item.get("type") or item.get("name")
                if not isinstance(effect_name, str) or not effect_name:
                    raise ValueError(f"effects[{index}] requires effect or type.")
                effect_parameters = item.get("parameters") or item.get("properties") or {}
                encoded_effects.append({
                    "Effect": effect_name,
                    "Active": McpServer._as_bool(item.get("active"), True),
                    "Parameters": McpServer._serialized_patches(effect_parameters),
                })
            mapped["Effects"] = encoded_effects
        return mapped

    @staticmethod
    def _map_profiler_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "profiler_start", "profiler_stop", "profiler_status",
            "profiler_set_areas", "get_frame_timing", "get_counters",
            "get_object_memory", "memory_take_snapshot", "memory_list_snapshots",
            "memory_compare_snapshots", "frame_debugger_enable",
            "frame_debugger_disable", "frame_debugger_get_events",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_profiler action.")
        mapped: dict[str, Any] = {"Action": action}
        aliases = {
            "object_path": "ObjectPath", "log_file": "LogFile",
            "enable_callstacks": "EnableCallstacks", "snapshot_path": "SnapshotPath",
            "search_path": "SearchPath", "snapshot_a": "SnapshotA",
            "snapshot_b": "SnapshotB", "page_size": "PageSize",
        }
        for key, value in arguments.items():
            if key in {"action", "areas"} or value is None:
                continue
            field = aliases.get(key)
            if field is None:
                field = "".join(part[:1].upper() + part[1:] for part in key.split("_") if part)
            if field in {"Category", "ObjectPath", "LogFile", "SnapshotPath", "SearchPath", "SnapshotA", "SnapshotB"}:
                mapped[field] = str(value)
            elif field == "Counters":
                mapped[field] = McpServer._string_list(value)
            elif field == "EnableCallstacks":
                mapped[field] = McpServer._as_bool(value, False)
                mapped["HasEnableCallstacks"] = True
            elif field in {"PageSize", "Cursor"}:
                mapped[field] = int(value)
                mapped["Has" + field] = True
        areas = arguments.get("areas")
        if areas is not None:
            if not isinstance(areas, dict):
                raise ValueError("areas must be an object mapping ProfilerArea names to booleans.")
            mapped["Areas"] = [
                {"Name": str(name), "Enabled": McpServer._as_bool(enabled, False)}
                for name, enabled in areas.items()
            ]
        return mapped

    @staticmethod
    def _map_ui_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "create", "read", "update", "delete", "attach_ui_document",
            "detach_ui_document", "create_panel_settings", "update_panel_settings",
            "get_visual_tree", "render_ui", "link_stylesheet", "list",
            "modify_visual_element",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_ui action.")
        path = arguments.get("path")
        if action in {"create", "read", "update", "delete"} and path is not None:
            if not isinstance(path, str):
                raise ValueError("path must be a string.")
            normalized = path.replace("\\", "/")
            if not normalized.lower().startswith("assets/") or ".." in normalized.split("/"):
                raise ValueError("path must be under Assets and cannot contain traversal sequences.")
            if Path(normalized).suffix.lower() not in {".uxml", ".uss"}:
                raise ValueError("path must use the .uxml or .uss extension.")
        mapped: dict[str, Any] = {"Action": action}
        aliases = {
            "source_asset": "SourceAsset", "panel_settings": "PanelSettings",
            "sort_order": "SortOrder", "scale_mode": "ScaleMode",
            "max_depth": "MaxDepth", "include_image": "IncludeImage",
            "max_resolution": "MaxResolution", "screenshot_file_name": "FileName",
            "output_folder": "OutputFolder", "filter_type": "FilterType",
            "page_size": "PageSize", "page_number": "PageNumber",
            "element_name": "ElementName", "add_classes": "AddClasses",
            "remove_classes": "RemoveClasses", "toggle_classes": "ToggleClasses",
        }
        strings = {"Path", "Target", "SourceAsset", "PanelSettings", "ScaleMode", "FileName", "OutputFolder", "Stylesheet", "FilterType", "ElementName", "Text", "Tooltip"}
        integers = {"SortOrder", "MaxDepth", "Width", "Height", "MaxResolution", "PageSize", "PageNumber"}
        booleans = {"IncludeImage", "Enabled", "Visible"}
        string_arrays = {"AddClasses", "RemoveClasses", "ToggleClasses"}
        ignored = {"action", "contents", "settings", "style", "reference_resolution"}
        for key, value in arguments.items():
            if key in ignored or value is None:
                continue
            field = aliases.get(key)
            if field is None:
                field = "".join(part[:1].upper() + part[1:] for part in key.split("_") if part)
            if field in strings:
                mapped[field] = str(value)
            elif field in integers:
                mapped[field] = int(value); mapped["Has" + field] = True
            elif field in booleans:
                mapped[field] = McpServer._as_bool(value, False); mapped["Has" + field] = True
            elif field in string_arrays:
                mapped[field] = McpServer._string_list(value)
        contents = arguments.get("contents")
        if contents is not None:
            if not isinstance(contents, str):
                raise ValueError("contents must be a string.")
            mapped["EncodedContents"] = base64.b64encode(contents.encode("utf-8")).decode("ascii")
            mapped["ContentsEncoded"] = True
        resolution = arguments.get("reference_resolution")
        if resolution is not None:
            if not isinstance(resolution, dict):
                raise ValueError("reference_resolution must be an object.")
            mapped["ReferenceResolution"] = [float(resolution.get("width", 1920)), float(resolution.get("height", 1080))]
            mapped["HasReferenceResolution"] = True
        mapped["Settings"] = McpServer._serialized_patches(arguments.get("settings"))
        mapped["Style"] = McpServer._serialized_patches(arguments.get("style"))
        return mapped

    @staticmethod
    def _map_vfx_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "particle_create", "particle_get_info", "particle_set_main",
            "particle_set_emission", "particle_set_shape", "particle_set_color_over_lifetime",
            "particle_set_size_over_lifetime", "particle_set_velocity_over_lifetime",
            "particle_set_noise", "particle_set_renderer", "particle_enable_module",
            "particle_play", "particle_stop", "particle_pause", "particle_restart",
            "particle_clear", "particle_add_burst", "particle_clear_bursts",
            "vfx_create_asset", "vfx_assign_asset", "vfx_list_templates", "vfx_list_assets",
            "vfx_get_info", "vfx_set_float", "vfx_set_int", "vfx_set_bool",
            "vfx_set_vector2", "vfx_set_vector3", "vfx_set_vector4", "vfx_set_color",
            "vfx_set_gradient", "vfx_set_texture", "vfx_set_mesh", "vfx_set_curve",
            "vfx_send_event", "vfx_play", "vfx_stop", "vfx_pause", "vfx_reinit",
            "vfx_set_playback_speed", "vfx_set_seed", "line_get_info",
            "line_set_positions", "line_add_position", "line_set_position", "line_set_width",
            "line_set_color", "line_set_material", "line_set_properties", "line_clear",
            "line_create_line", "line_create_circle", "line_create_arc", "line_create_bezier",
            "trail_get_info", "trail_set_time", "trail_set_width", "trail_set_color",
            "trail_set_material", "trail_set_properties", "trail_clear", "trail_emit",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_vfx action.")
        target = arguments.get("target")
        mapped: dict[str, Any] = {"Action": action}
        if target is not None:
            mapped["Target"] = str(target)
        if arguments.get("search_method") is not None:
            mapped["SearchMethod"] = str(arguments["search_method"])
        if arguments.get("component_index") is not None:
            mapped["ComponentIndex"] = int(arguments["component_index"])
            mapped["HasComponentIndex"] = True
        properties = arguments.get("properties")
        if isinstance(properties, str):
            try:
                properties = json.loads(properties)
            except json.JSONDecodeError as error:
                raise ValueError("properties must contain valid JSON.") from error
        if properties is None:
            properties = {}
        if not isinstance(properties, dict):
            raise ValueError("properties must be an object or JSON object string.")
        aliases = {
            "size_over_lifetime": "size", "start_color_line": "startColor",
            "sorting_layer_id": "sortingLayerID", "material": "materialPath",
        }

        def camel(name: str) -> str:
            if name in aliases:
                return aliases[name]
            parts = name.split("_")
            return parts[0] + "".join(part[:1].upper() + part[1:] for part in parts[1:])

        def encode(name: str, value: Any) -> dict[str, Any]:
            record: dict[str, Any] = {"Name": camel(name)}
            if value is None:
                record["Kind"] = "null"
            elif isinstance(value, bool):
                record.update({"Kind": "bool", "BoolValue": value})
            elif isinstance(value, int):
                record.update({"Kind": "int", "IntValue": value, "NumberValue": float(value)})
            elif isinstance(value, float):
                record.update({"Kind": "number", "NumberValue": value})
            elif isinstance(value, str):
                record.update({"Kind": "string", "StringValue": value})
            elif isinstance(value, dict):
                record.update({"Kind": "object", "Children": [encode(str(k), v) for k, v in value.items()]})
            elif isinstance(value, list):
                if all(isinstance(item, (int, float)) and not isinstance(item, bool) for item in value):
                    record.update({"Kind": "numbers", "Numbers": [float(item) for item in value]})
                else:
                    record.update({"Kind": "array", "Items": [encode(str(index), item) for index, item in enumerate(value)]})
            else:
                raise ValueError(f"Unsupported VFX property value for {name}.")
            return record

        mapped["Properties"] = [encode(str(key), value) for key, value in properties.items()]
        return mapped

    @staticmethod
    def _map_probuilder_arguments(arguments: dict[str, Any]) -> dict[str, Any]:
        actions = {
            "ping", "create_shape", "create_poly_shape", "extrude_faces", "extrude_edges",
            "bevel_edges", "subdivide", "delete_faces", "bridge_edges", "connect_elements",
            "detach_faces", "flip_normals", "merge_faces", "combine_meshes", "merge_objects",
            "duplicate_and_flip", "create_polygon", "merge_vertices", "weld_vertices",
            "split_vertices", "move_vertices", "insert_vertex", "append_vertices_to_edge",
            "select_faces", "set_face_material", "set_face_color", "set_face_uvs",
            "get_mesh_info", "convert_to_probuilder", "set_smoothing", "auto_smooth",
            "center_pivot", "freeze_transform", "set_pivot", "validate_mesh", "repair_mesh",
        }
        action = str(arguments.get("action") or "").strip().lower()
        if action not in actions:
            raise ValueError("Unknown manage_probuilder action.")
        mapped: dict[str, Any] = {"Action": action}
        if arguments.get("target") is not None:
            mapped["Target"] = str(arguments["target"])
        if arguments.get("search_method") is not None:
            mapped["SearchMethod"] = str(arguments["search_method"])
        properties = arguments.get("properties")
        if isinstance(properties, str):
            try:
                properties = json.loads(properties)
            except json.JSONDecodeError as error:
                raise ValueError("properties must contain valid JSON.") from error
        if properties is None:
            properties = {}
        if not isinstance(properties, dict):
            raise ValueError("properties must be an object or JSON object string.")

        def camel(name: str) -> str:
            parts = name.split("_")
            return parts[0] + "".join(part[:1].upper() + part[1:] for part in parts[1:])

        def encode(name: str, value: Any) -> dict[str, Any]:
            record: dict[str, Any] = {"Name": camel(name)}
            if value is None:
                record["Kind"] = "null"
            elif isinstance(value, bool):
                record.update({"Kind": "bool", "BoolValue": value})
            elif isinstance(value, int):
                record.update({"Kind": "int", "IntValue": value, "NumberValue": float(value)})
            elif isinstance(value, float):
                record.update({"Kind": "number", "NumberValue": value})
            elif isinstance(value, str):
                record.update({"Kind": "string", "StringValue": value})
            elif isinstance(value, dict):
                record.update({"Kind": "object", "Children": [encode(str(k), v) for k, v in value.items()]})
            elif isinstance(value, list):
                if all(isinstance(item, (int, float)) and not isinstance(item, bool) for item in value):
                    record.update({"Kind": "numbers", "Numbers": [float(item) for item in value]})
                else:
                    record.update({"Kind": "array", "Items": [encode(str(i), item) for i, item in enumerate(value)]})
            else:
                raise ValueError(f"Unsupported ProBuilder property value for {name}.")
            return record

        mapped["Properties"] = [encode(str(key), value) for key, value in properties.items()]
        return mapped

    @staticmethod
    def _rpc_result(request_id: Any, result: Any) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": request_id, "result": result}

    @staticmethod
    def _rpc_error(
        request_id: Any, code: int, message: str
    ) -> dict[str, Any]:
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "error": {"code": code, "message": message},
        }


def serve_stdio(server: McpServer) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", newline="\n")

    output_lock = threading.Lock()

    def write_response(response: dict[str, Any] | None) -> None:
        if response is None:
            return
        encoded = json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n"
        with output_lock:
            sys.stdout.write(encoded)
            sys.stdout.flush()

    def safe_handle(message: dict[str, Any]) -> dict[str, Any] | None:
        try:
            return server.handle(message)
        except Exception as error:  # Keep the stdio server alive on unexpected failures.
            print(f"Unexpected MCP server error: {error}", file=sys.stderr, flush=True)
            return {
                "jsonrpc": "2.0",
                "id": message.get("id"),
                "error": {"code": -32603, "message": "Internal MCP server error."},
            }

    def finish(future: concurrent.futures.Future[dict[str, Any] | None]) -> None:
        write_response(future.result())

    with concurrent.futures.ThreadPoolExecutor(
        max_workers=4, thread_name_prefix="unity2019-mcp"
    ) as executor:
        for raw_line in sys.stdin.buffer:
            if not raw_line.strip():
                continue

            try:
                message = json.loads(raw_line.decode("utf-8-sig"))
                if not isinstance(message, dict):
                    raise ValueError("JSON-RPC message must be an object")
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
                write_response(
                    {
                        "jsonrpc": "2.0",
                        "id": None,
                        "error": {"code": -32700, "message": f"Parse error: {error}"},
                    }
                )
                continue

            method = message.get("method")
            if method in {"tools/call", "resources/read"} and message.get("id") is not None:
                future = executor.submit(safe_handle, message)
                future.add_done_callback(finish)
            else:
                write_response(safe_handle(message))

    return 0


def _origin_is_allowed(origin: str | None) -> bool:
    if origin is None:
        return True
    try:
        parsed = urlparse(origin)
    except ValueError:
        return False
    return parsed.scheme in {"http", "https"} and parsed.hostname in LOOPBACK_HOSTS


def _write_http_metadata(
    metadata_path: Path,
    project: Path,
    host: str,
    port: int,
    mcp_path: str,
    control_token: str,
) -> dict[str, Any]:
    base_url = f"http://{host}:{port}"
    metadata = {
        "Version": 1,
        "Pid": os.getpid(),
        "Host": host,
        "Port": port,
        "Url": base_url + mcp_path,
        "HealthUrl": base_url + "/health",
        "ControlUrl": base_url + "/control/shutdown",
        "ControlToken": control_token,
        "ProjectPath": str(project),
        "StartedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "ServerVersion": SERVER_VERSION,
        "ProtocolVersion": LATEST_PROTOCOL_VERSION,
    }
    metadata_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = metadata_path.with_suffix(metadata_path.suffix + ".tmp")
    temporary_path.write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    temporary_path.replace(metadata_path)
    return metadata


def _remove_http_metadata(metadata_path: Path, expected_pid: int) -> None:
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
        if int(metadata.get("Pid", -1)) == expected_pid:
            metadata_path.unlink(missing_ok=True)
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        return


def serve_http(
    server: McpServer,
    host: str,
    port: int,
    mcp_path: str,
    metadata_path: Path,
) -> int:
    control_token = secrets.token_urlsafe(32)
    httpd: http.server.ThreadingHTTPServer

    class McpHttpHandler(http.server.BaseHTTPRequestHandler):
        server_version = "Unity2019MCP/" + SERVER_VERSION

        def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
            request_path = urlparse(self.path).path
            if request_path == "/control/shutdown":
                self._handle_shutdown()
                return
            if request_path != mcp_path:
                self._send_empty(404)
                return
            if not _origin_is_allowed(self.headers.get("Origin")):
                self._send_json(403, {"error": "Origin is not allowed."})
                return

            protocol_version = self.headers.get("MCP-Protocol-Version")
            if (
                protocol_version is not None
                and protocol_version not in SUPPORTED_PROTOCOL_VERSIONS
            ):
                self._send_json(400, {"error": "Unsupported MCP protocol version."})
                return

            content_type = self.headers.get("Content-Type", "")
            if content_type.split(";", 1)[0].strip().lower() != "application/json":
                self._send_json(415, {"error": "Content-Type must be application/json."})
                return

            try:
                content_length = int(self.headers.get("Content-Length", "0"))
            except ValueError:
                content_length = -1
            if content_length <= 0 or content_length > MAX_HTTP_REQUEST_BYTES:
                self._send_json(413, {"error": "Invalid or oversized request body."})
                return

            try:
                raw_body = self.rfile.read(content_length)
                message = json.loads(raw_body.decode("utf-8-sig"))
                if not isinstance(message, dict):
                    raise ValueError("JSON-RPC message must be an object")
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
                self._send_json(
                    400,
                    {
                        "jsonrpc": "2.0",
                        "id": None,
                        "error": {"code": -32700, "message": f"Parse error: {error}"},
                    },
                )
                return

            try:
                response = server.handle(message)
            except Exception as error:  # Keep the HTTP server alive on unexpected failures.
                print(f"Unexpected MCP server error: {error}", file=sys.stderr, flush=True)
                response = {
                    "jsonrpc": "2.0",
                    "id": message.get("id"),
                    "error": {"code": -32603, "message": "Internal MCP server error."},
                }

            if response is None:
                self._send_empty(202)
            else:
                self._send_json(200, response)

        def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
            request_path = urlparse(self.path).path
            if request_path == "/health":
                self._send_json(
                    200,
                    {
                        "ok": True,
                        "server": SERVER_NAME,
                        "version": SERVER_VERSION,
                        "protocol": LATEST_PROTOCOL_VERSION,
                        "project": str(server.project),
                    },
                )
                return
            if request_path == mcp_path:
                self.send_response(405)
                self.send_header("Allow", "POST")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            self._send_empty(404)

        def do_DELETE(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
            self._send_empty(405)

        def _handle_shutdown(self) -> None:
            if self.client_address[0] not in LOOPBACK_HOSTS:
                self._send_empty(403)
                return
            supplied_token = self.headers.get("X-Unity-MCP-Control-Token", "")
            if not secrets.compare_digest(supplied_token, control_token):
                self._send_empty(403)
                return
            self._send_json(200, {"ok": True, "message": "Shutdown requested."})
            threading.Thread(
                target=httpd.shutdown,
                name="unity2019-mcp-shutdown",
                daemon=True,
            ).start()

        def _send_empty(self, status: int) -> None:
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def _send_json(self, status: int, payload: dict[str, Any]) -> None:
            encoded = json.dumps(
                payload, ensure_ascii=False, separators=(",", ":")
            ).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

        def log_message(self, format_string: str, *args: Any) -> None:
            print(
                "%s - - [%s] %s"
                % (self.address_string(), self.log_date_time_string(), format_string % args),
                file=sys.stderr,
                flush=True,
            )

    try:
        httpd = http.server.ThreadingHTTPServer((host, port), McpHttpHandler)
    except OSError as error:
        print(f"Could not start HTTP MCP server on {host}:{port}: {error}", file=sys.stderr)
        return 1

    httpd.daemon_threads = True
    metadata = _write_http_metadata(
        metadata_path, server.project, host, port, mcp_path, control_token
    )
    print(
        f"Unity 2019 MCP HTTP server ready at {metadata['Url']} (PID {metadata['Pid']}).",
        file=sys.stderr,
        flush=True,
    )
    try:
        httpd.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
        _remove_http_metadata(metadata_path, os.getpid())
    return 0


def run_protocol_self_test(server: McpServer) -> int:
    initialized = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {"protocolVersion": LATEST_PROTOCOL_VERSION},
        }
    )
    listed = server.handle(
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
    )
    resources = server.handle(
        {"jsonrpc": "2.0", "id": 3, "method": "resources/list", "params": {}}
    )
    templates = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 4,
            "method": "resources/templates/list",
            "params": {},
        }
    )

    assert initialized is not None
    assert initialized["result"]["protocolVersion"] == LATEST_PROTOCOL_VERSION
    assert listed is not None
    assert listed["result"]["tools"] == server._visible_tools()
    assert len({tool["name"] for tool in TOOLS}) == len(TOOLS)
    assert resources is not None
    assert resources["result"]["resources"] == server._visible_resources()
    assert templates is not None
    assert templates["result"]["resourceTemplates"] == server._visible_resource_templates()
    if not SHOW_LEGACY_ALIASES:
        assert len(listed["result"]["tools"]) == 48
        assert len(resources["result"]["resources"]) == 19
        assert len(templates["result"]["resourceTemplates"]) == 6
    assert server._map_resource("unity2019://scene/gameobject/-123") == (
        "get_gameobject",
        {"InstanceId": -123},
    )
    assert server._map_tool(
        "unity2019_get_hierarchy", {"max_depth": 2, "cursor": "10", "page_size": 25}
    ) == ("get_hierarchy", {"MaxDepth": 2, "Offset": 10, "PageSize": 25})
    component_mapping = server._map_component_arguments(
        {
            "action": "set_property",
            "instance_id": -123,
            "component_type": "UnityEngine.BoxCollider",
            "property_path": "m_Center",
            "value": [1, 2, 3],
        }
    )
    assert component_mapping["ValueKind"] == "vector"
    assert component_mapping["VectorLength"] == 3
    server.cancel_request("cancel-before-start")
    cancelled = server._register_request("cancel-before-start")
    assert cancelled.is_set()
    server._unregister_request("cancel-before-start")
    assert _origin_is_allowed(None)
    assert _origin_is_allowed("http://127.0.0.1:6500")
    assert _origin_is_allowed("http://localhost:6500")
    assert not _origin_is_allowed("null")
    assert not _origin_is_allowed("https://example.com")

    print(
        json.dumps(
            {
                "ok": True,
                "server": SERVER_NAME,
                "version": SERVER_VERSION,
                "protocol": LATEST_PROTOCOL_VERSION,
                "tool_count": len(server._visible_tools()),
                "resource_count": len(server._visible_resources()),
                "resource_template_count": len(server._visible_resource_templates()),
                "legacy_aliases_visible": SHOW_LEGACY_ALIASES,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


def manage_claude_desktop_config(
    action: str, config_path: Path, project: Path
) -> int:
    """Configure the stdio entry used by Claude Desktop without losing other entries."""
    data: dict[str, Any] = {}
    if config_path.is_file():
        try:
            loaded = json.loads(config_path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
            print(f"Could not read Claude Desktop config: {error}", file=sys.stderr)
            return 1
        if not isinstance(loaded, dict):
            print("Claude Desktop config root must be a JSON object.", file=sys.stderr)
            return 1
        data = loaded

    servers = data.get("mcpServers")
    if servers is None:
        servers = {}
        data["mcpServers"] = servers
    if not isinstance(servers, dict):
        print("Claude Desktop mcpServers must be a JSON object.", file=sys.stderr)
        return 1

    server_name = "UnityMCP"
    expected_args = [str(Path(__file__).resolve()), "--project", str(project)]
    desired = {
        "command": str(Path(sys.executable).resolve()),
        "args": expected_args,
    }
    existing = servers.get(server_name)
    configured = (
        isinstance(existing, dict)
        and isinstance(existing.get("command"), str)
        and bool(existing.get("command"))
        and existing.get("args") == expected_args
    )

    if action == "status":
        print(
            json.dumps(
                {
                    "configured": configured,
                    "name": server_name,
                    "transport": "stdio",
                    "config": str(config_path),
                    "entry": existing,
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 0 if configured else 1

    if action == "remove":
        if existing is None:
            print(f"Claude Desktop MCP entry {server_name} is already absent.")
            return 0
        del servers[server_name]
        message = f"Removed Claude Desktop MCP entry {server_name}."
    else:
        if existing == desired:
            print(f"Claude Desktop MCP entry {server_name} is already configured.")
            return 0
        servers[server_name] = desired
        message = f"Configured Claude Desktop MCP entry {server_name}."

    try:
        config_path.parent.mkdir(parents=True, exist_ok=True)
        temporary_path = config_path.with_name(
            config_path.name + "." + uuid.uuid4().hex + ".tmp"
        )
        try:
            temporary_path.write_text(
                json.dumps(data, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            os.replace(str(temporary_path), str(config_path))
        finally:
            if temporary_path.exists():
                temporary_path.unlink()
    except OSError as error:
        print(f"Could not update Claude Desktop config: {error}", file=sys.stderr)
        return 1

    print(message)
    print(str(config_path))
    return 0


CLIENT_CONFIG_SPECS: dict[str, dict[str, Any]] = {
    "antigravity": {
        "label": "Antigravity",
        "path": lambda: Path.home() / ".gemini" / "config" / "mcp_config.json",
        "container": "mcpServers",
        "url_property": "serverUrl",
        "extra": {"disabled": False},
    },
    "cline": {
        "label": "Cline",
        "path": lambda: Path(os.environ.get("APPDATA") or Path.home())
        / "Code" / "User" / "globalStorage" / "saoudrizwan.claude-dev"
        / "settings" / "cline_mcp_settings.json",
        "container": "mcpServers",
        "url_property": "url",
        "type": "streamableHttp",
        "extra": {"disabled": False, "autoApprove": []},
    },
    "cursor": {
        "label": "Cursor",
        "path": lambda: Path.home() / ".cursor" / "mcp.json",
        "container": "mcpServers",
        "url_property": "url",
    },
    "gemini cli": {
        "label": "Gemini CLI",
        "path": lambda: Path.home() / ".gemini" / "settings.json",
        "container": "mcpServers",
        "url_property": "httpUrl",
    },
    "github copilot cli": {
        "label": "GitHub Copilot CLI",
        "path": lambda: Path.home() / ".copilot" / "mcp-config.json",
        "container": "mcpServers",
        "url_property": "url",
    },
    "qwen code": {
        "label": "Qwen Code",
        "path": lambda: Path.home() / ".qwen" / "settings.json",
        "container": "mcpServers",
        "url_property": "url",
    },
    "vs code copilot": {
        "label": "VS Code Copilot",
        "path": lambda: Path(os.environ.get("APPDATA") or Path.home())
        / "Code" / "User" / "mcp.json",
        "container": "servers",
        "url_property": "url",
    },
    "windsurf": {
        "label": "Windsurf",
        "path": lambda: Path.home() / ".codeium" / "windsurf" / "mcp_config.json",
        "container": "mcpServers",
        "url_property": "serverUrl",
        "extra": {"disabled": False},
    },
}


def _write_json_atomic(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(
            json.dumps(data, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        os.replace(str(temporary), str(path))
    finally:
        if temporary.exists():
            temporary.unlink()


def _load_client_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        return {}
    loaded = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(loaded, dict):
        raise ValueError("Client config root must be a JSON object.")
    return loaded


def _openclaw_entry(url: str) -> dict[str, Any]:
    return {
        "enabled": True,
        "url": url,
        "transport": "http",
        "toolPrefix": "unityMCP",
        "requestTimeoutMs": 30000,
    }


def manage_json_client_config(
    action: str,
    client_name: str,
    url: str,
    config_override: Path | None = None,
) -> int:
    normalized = " ".join(client_name.strip().lower().split())
    if normalized == "openclaw":
        spec: dict[str, Any] = {
            "label": "OpenClaw",
            "path": lambda: Path.home() / ".openclaw" / "openclaw.json",
        }
    else:
        spec = CLIENT_CONFIG_SPECS.get(normalized) or {}
    if not spec:
        print(
            "JSON client must be one of: "
            + ", ".join(sorted(item["label"] for item in CLIENT_CONFIG_SPECS.values()))
            + ", OpenClaw.",
            file=sys.stderr,
        )
        return 2
    if not re.fullmatch(r"https?://[^\s]+", url):
        print("--client-url must be an HTTP(S) URL.", file=sys.stderr)
        return 2

    path = config_override.resolve() if config_override else spec["path"]().resolve()
    try:
        data = _load_client_json(path)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        print(f"Could not read {spec['label']} config: {error}", file=sys.stderr)
        return 1

    if normalized == "openclaw":
        plugins = data.get("plugins")
        if not isinstance(plugins, dict):
            plugins = {}
            data["plugins"] = plugins
        entries = plugins.get("entries")
        if not isinstance(entries, dict):
            entries = {}
            plugins["entries"] = entries
        plugin = entries.get("openclaw-mcp-bridge")
        if not isinstance(plugin, dict):
            plugin = {}
            entries["openclaw-mcp-bridge"] = plugin
        plugin_config = plugin.get("config")
        if not isinstance(plugin_config, dict):
            plugin_config = {}
            plugin["config"] = plugin_config
        servers = plugin_config.get("servers")
        if not isinstance(servers, dict):
            servers = {}
            plugin_config["servers"] = servers
        existing = servers.get("unityMCP")
        desired = _openclaw_entry(url)
        configured = (
            plugin.get("enabled") is not False
            and isinstance(existing, dict)
            and existing.get("enabled") is not False
            and existing.get("url") == url
            and existing.get("transport") == "http"
        )
        if action == "remove":
            servers.pop("unityMCP", None)
        elif action == "configure":
            plugin["enabled"] = True
            servers["unityMCP"] = desired
    else:
        container_name = str(spec["container"])
        servers = data.get(container_name)
        if not isinstance(servers, dict):
            servers = {}
            data[container_name] = servers
        existing = servers.get("unityMCP")
        url_property = str(spec["url_property"])
        desired = {
            url_property: url,
            "type": str(spec.get("type") or "http"),
        }
        desired.update(dict(spec.get("extra") or {}))
        configured = (
            isinstance(existing, dict)
            and existing.get(url_property) == url
            and existing.get("type") == desired["type"]
        )
        if action == "remove":
            servers.pop("unityMCP", None)
        elif action == "configure":
            preserved = dict(existing) if isinstance(existing, dict) else {}
            for stale in ("url", "serverUrl", "httpUrl", "command", "args"):
                preserved.pop(stale, None)
            preserved.update(desired)
            servers["unityMCP"] = preserved

    if action == "status":
        print(json.dumps({
            "configured": configured,
            "name": "unityMCP",
            "client": spec["label"],
            "transport": "http",
            "config": str(path),
            "entry": existing,
        }, ensure_ascii=False, indent=2))
        return 0 if configured else 1
    if action == "snippet":
        if normalized == "openclaw":
            snippet = {
                "plugins": {"entries": {"openclaw-mcp-bridge": {
                    "enabled": True,
                    "config": {"servers": {"unityMCP": desired}},
                }}}
            }
        else:
            snippet = {str(spec["container"]): {"unityMCP": desired}}
        print(json.dumps(snippet, ensure_ascii=False, indent=2))
        return 0
    try:
        _write_json_atomic(path, data)
    except OSError as error:
        print(f"Could not update {spec['label']} config: {error}", file=sys.stderr)
        return 1
    verb = "Removed" if action == "remove" else "Configured"
    print(f"{verb} {spec['label']} MCP entry unityMCP.")
    print(str(path))
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="stdio or Streamable HTTP MCP server for the Unity 2019.4 bridge"
    )
    parser.add_argument(
        "--project",
        default=str(Path.cwd()),
        help="Unity project root containing Assets and ProjectSettings",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=30.0,
        help="Unity bridge socket timeout in seconds (default: 30)",
    )
    parser.add_argument(
        "--transport",
        choices=("stdio", "http"),
        default="stdio",
        help="MCP transport (default: stdio)",
    )
    parser.add_argument(
        "--http-host",
        default="127.0.0.1",
        help="HTTP bind host; only loopback hosts are accepted (default: 127.0.0.1)",
    )
    parser.add_argument(
        "--http-port",
        type=int,
        default=6500,
        help="HTTP bind port (default: 6500)",
    )
    parser.add_argument(
        "--http-path",
        default="/mcp",
        help="Streamable HTTP MCP endpoint path (default: /mcp)",
    )
    parser.add_argument(
        "--http-metadata",
        help=(
            "Runtime metadata JSON path "
            f"(default: Library/{RUNTIME_DIRECTORY_NAME}/http-server.json)"
        ),
    )
    parser.add_argument(
        "--http-log",
        help="Append stdout/stderr to this log file in HTTP mode",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Validate MCP initialization and tool catalog without contacting Unity",
    )
    parser.add_argument(
        "--bridge-smoke",
        action="store_true",
        help="Call the Unity bridge ping endpoint once and exit",
    )
    parser.add_argument(
        "--claude-desktop-action",
        choices=("configure", "status", "remove"),
        help="Configure, inspect, or remove the Claude Desktop stdio entry",
    )
    parser.add_argument(
        "--claude-desktop-config",
        help="Claude Desktop JSON config path",
    )
    parser.add_argument(
        "--client-action",
        choices=("configure", "status", "remove", "snippet"),
        help="Configure, inspect, remove, or print a JSON-based MCP client entry",
    )
    parser.add_argument(
        "--client-name",
        help="JSON client name for --client-action",
    )
    parser.add_argument(
        "--client-url",
        help="Streamable HTTP endpoint for --client-action",
    )
    parser.add_argument(
        "--client-config",
        help="Optional config path override, intended for validation and advanced setups",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    log_stream = None
    if args.transport == "http" and args.http_log:
        log_path = Path(args.http_log).resolve()
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_stream = log_path.open("a", encoding="utf-8", buffering=1)
        sys.stdout = log_stream
        sys.stderr = log_stream

    project = Path(args.project).resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        print(f"Not a Unity project root: {project}", file=sys.stderr)
        return 2
    if args.timeout <= 0:
        print("--timeout must be greater than zero.", file=sys.stderr)
        return 2
    if args.http_host not in LOOPBACK_HOSTS:
        print("--http-host must be a loopback host.", file=sys.stderr)
        return 2
    if args.http_port < 1024 or args.http_port > 65535:
        print("--http-port must be between 1024 and 65535.", file=sys.stderr)
        return 2
    if (
        not args.http_path.startswith("/")
        or "?" in args.http_path
        or "#" in args.http_path
    ):
        print("--http-path must be an absolute URL path without query or fragment.", file=sys.stderr)
        return 2

    if args.claude_desktop_action:
        if not args.claude_desktop_config:
            print(
                "--claude-desktop-config is required with --claude-desktop-action.",
                file=sys.stderr,
            )
            return 2
        return manage_claude_desktop_config(
            args.claude_desktop_action,
            Path(args.claude_desktop_config).expanduser().resolve(),
            project,
        )

    if args.client_action:
        if not args.client_name or not args.client_url:
            print(
                "--client-name and --client-url are required with --client-action.",
                file=sys.stderr,
            )
            return 2
        return manage_json_client_config(
            args.client_action,
            args.client_name,
            args.client_url,
            Path(args.client_config).expanduser() if args.client_config else None,
        )

    server = McpServer(project, args.timeout)
    if args.self_test:
        return run_protocol_self_test(server)
    if args.bridge_smoke:
        try:
            result = server.bridge.call("ping", {})
        except BridgeError as error:
            print(str(error), file=sys.stderr)
            return 1
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
        return 0
    if args.transport == "http":
        metadata_path = (
            Path(args.http_metadata).resolve()
            if args.http_metadata
            else project / "Library" / RUNTIME_DIRECTORY_NAME / "http-server.json"
        )
        return serve_http(
            server, args.http_host, args.http_port, args.http_path, metadata_path
        )
    return serve_stdio(server)


if __name__ == "__main__":
    raise SystemExit(main())
