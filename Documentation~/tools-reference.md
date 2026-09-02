# MCP tool reference

This release exposes 48 public tools by default. The tables describe their
concrete purpose and Unity 2019 support level. Call `tools/list` for the
authoritative JSON Schema, required parameters, annotations, and defaults.

Status values: **Core** works with the declared package dependencies;
**Conditional** requires a compatible optional Unity package or project
feature; **External** requires a configured third-party service.

## Editor, scene, and object workflow

| Tool | Concrete operations | Status |
| --- | --- | --- |
| `batch_execute` | Execute up to the configured batch limit, with deterministic results, fail-fast behavior, and optional parallel scheduling. | Core |
| `execute_menu_item` | Invoke a Unity Editor menu path. | Core |
| `find_gameobjects` | Search loaded objects by name, tag, layer, component, hierarchy path, or ID with pagination. | Core |
| `manage_gameobject` | Create, modify, delete, duplicate, move-relative, or orient GameObjects; set transform, parent, activity, tag, layer, and components. | Core |
| `manage_components` | Add/remove a component or set supported serialized properties on a selected component instance. | Core |
| `manage_editor` | Play, pause, stop, Undo/Redo, active tool, tags/layers, telemetry state, and package deploy/restore controls. | Core |
| `manage_scene` | Create/load/save/close scenes, read hierarchy/build state, set active scene, move objects, frame Scene View, and validate scenes. | Core |
| `read_console` | Page, filter, format, or clear Editor console messages captured after bridge startup. | Core |
| `refresh_unity` | Request AssetDatabase refresh and optional script compilation, with readiness waiting. | Core |
| `debug_request_context` | Inspect the current MCP request ID, cancellation, timeout, and client context. | Core |
| `set_active_instance` | Select the Unity Editor instance used by subsequent server calls. | Core |
| `manage_tools` | List, activate, deactivate, synchronize, or reset visible MCP tool groups. | Core |
| `execute_custom_tool` | Invoke a project-scoped custom tool registered with the bridge. | Conditional: project registration |

UI text examples using the existing compatible tools:

```text
manage_components(
    action="set_property",
    target=12345,
    component_type="UnityEngine.UI.Text",
    property="m_Text",
    value="New title"
)
manage_components(
    action="set_property",
    target="Canvas/Score",
    component_type="TMPro.TextMeshProUGUI",
    property="m_text",
    value="42"
)
manage_ui(
    action="modify_visual_element",
    target="HudDocument",
    element_name="StatusLabel",
    text="Ready"
)
```

Read the component resource first to confirm the exact serialized property path.
The `manage_components` forms persist scene-component changes and register Unity
Undo. The UI Toolkit form changes a live visual element and remains subject to
the Unity 2019 `UIDocument` limitations in the support matrix.

## C# scripts, reflection, and documentation

| Tool | Concrete operations | Status |
| --- | --- | --- |
| `create_script` | Create a C# file under `Assets/` and request import/compilation. | Core |
| `delete_script` | Delete a C# script by Assets path or MCP path URI. | Core |
| `get_sha` | Read script metadata and SHA-256 for optimistic edit preconditions. | Core |
| `find_in_file` | Search a project text file with a regular expression and bounded results. | Core |
| `apply_text_edits` | Apply atomic 1-based line/column replacements guarded by an optional SHA. | Core |
| `validate_script` | Run basic or standard C# validation and return diagnostics. | Core |
| `manage_script` | Compatibility route for script create, read, and delete. | Core |
| `manage_script_capabilities` | Report supported edit operations, size limits, and safety guards. | Core |
| `script_apply_edits` | Apply structured class, method, and anchor edits to a C# source file. | Core |
| `execute_code` | Compile and execute an in-memory C# method body; inspect, replay, or clear history. Safety checks are not a sandbox. | Core, trusted clients only |
| `unity_reflect` | Search live Unity/project types and inspect type or member signatures. | Core |
| `unity_docs` | Query official Unity ScriptReference, Manual, and package documentation. | External: internet access |

## Assets and authored content

| Tool | Concrete operations | Status |
| --- | --- | --- |
| `manage_asset` | Import, create, modify, delete, duplicate, move, rename, search, inspect, create folders, and list asset components. | Core |
| `manage_material` | Create materials, inspect them, set shader properties/colors, and assign materials or renderer colors. | Core |
| `manage_texture` | Create/delete textures and sprites, edit pixels, generate patterns/gradients/noise, and configure import settings. | Core |
| `manage_shader` | Create, read, update, or delete Shader source assets. | Core |
| `manage_scriptable_object` | Create and patch ScriptableObject assets using SerializedObject property paths. | Core |
| `manage_prefabs` | Create Prefabs from objects, inspect hierarchy, modify contents, and control Prefab Stage. | Core |
| `manage_animation` | Control Animator playback and parameters; create/edit controllers, states, clips, curves, events, masks, and legacy Animation data. | Core |

## Specialized Unity systems

| Tool | Concrete operations | Status |
| --- | --- | --- |
| `manage_packages` | List/search/inspect/add/remove/embed/resolve packages, poll jobs, and manage scoped registries. | Core |
| `manage_build` | Inspect/switch targets, manage PlayerSettings and build scenes, start/poll builds, and run batch builds. Build Profiles are unavailable. | Core with documented limitation |
| `manage_camera` | Create/list/configure Cameras, target/orbit views, configure lens/priority, and capture screenshots; adds Cinemachine controls when installed. | Core + conditional Cinemachine |
| `manage_physics` | Edit 2D/3D settings and collision layers; manage materials, colliders, rigidbodies, joints, queries, forces, and validation. | Core |
| `manage_graphics` | Manage skybox/environment, baking, rendering statistics, pipeline settings, volumes, and renderer features. | Core + conditional SRP features |
| `manage_profiler` | Start/stop profiling, inspect frames/counters/object memory, manage snapshots, and inspect the Frame Debugger where APIs exist. | Core + conditional package APIs |
| `manage_ui` | CRUD/list UXML and USS files. Live UIDocument, PanelSettings, visual-tree mutation, and render capture require compatible UI Toolkit runtime APIs. | Limited/conditional on Unity 2019 |
| `manage_vfx` | Manage ParticleSystem, LineRenderer, and TrailRenderer; use Visual Effect Graph operations when its package is installed. | Core + conditional VFX Graph |
| `manage_probuilder` | Create/edit ProBuilder shapes and mesh vertices/faces/UVs/smoothing/selection. | Conditional: `com.unity.probuilder` |

## Providers, model import, and tests

| Tool | Concrete operations | Status |
| --- | --- | --- |
| `generate_image` | Generate images or remove backgrounds through configured fal.ai/OpenRouter providers, then import results. | External |
| `generate_audio` | Generate sound effects or music through fal.ai and import an AudioClip. | External |
| `generate_model` | Generate and import 3D models through Tripo or Meshy. | External |
| `import_model` | Search, preview, import, poll, or cancel downloadable Sketchfab model jobs. | External |
| `import_model_file` | Safely copy/import local FBX, OBJ, GLB, glTF, or ZIP bundles and optionally configure animation/scale. | Core + conditional format importer |
| `run_tests` | Start asynchronous Unity EditMode or PlayMode tests with name/group/category/assembly filters. | Core: Unity Test Framework |
| `get_test_job` | Poll a test job, optionally waiting and returning failed-test details. | Core: Unity Test Framework |

## Backward-compatible tool aliases

The following 11 `unity2019_*` aliases remain callable for older client
configurations but are hidden from the default public catalog:

`unity2019_ping`, `unity2019_editor_state`, `unity2019_read_console`,
`unity2019_get_hierarchy`, `unity2019_find_gameobjects`,
`unity2019_manage_gameobject`, `unity2019_manage_component`,
`unity2019_refresh_assets`, `unity2019_play_mode`,
`unity2019_undo_redo`, and `unity2019_reload_active_scene`.
