# Features

MCP for Unity 2019 exposes the Unity `2019.4` Editor to trusted MCP clients
through 48 public tools and 25 public resources/resource templates. It supports
both stdio and Streamable HTTP transports and keeps all Unity API work on the
Editor main thread.

[简体中文](FEATURES.zh-CN.md)

## At a glance

| Area | Implemented capabilities |
| --- | --- |
| Editor workflow | Readiness state, Play/Pause/Stop, Undo/Redo, console access, menu execution, AssetDatabase refresh, tool visibility, and multi-instance selection. |
| Scenes and objects | Scene creation/loading/saving/validation, hierarchy reads, GameObject CRUD, transforms, parenting, component add/remove/property edits, and Prefab workflows. |
| Assets and content | Asset CRUD/import, materials, shaders, procedural textures, sprites, ScriptableObjects, Animator/AnimationClip editing, and package management. |
| Code | C# create/read/delete, SHA preconditions, regex search, atomic text edits, structured edits, validation, reflection, custom tools, and optional in-memory execution. |
| Rendering and simulation | Camera and screenshots, 2D/3D physics, graphics settings, light baking, render statistics, Profiler data, particles, line/trail renderers, and optional pipeline-specific features. |
| Build and test | Build targets/settings/scenes, asynchronous builds, EditMode/PlayMode tests, and asynchronous test result polling. |
| Generated/imported content | Local model import plus opt-in image, audio, 3D model, and Sketchfab provider workflows. |
| MCP resources | Editor/project state, tags/layers, selection/windows, menu items, cameras, volumes, renderer features, tests, Prefabs, GameObjects, and serialized component data. |

## Recommended workflow

1. Read `mcpforunity://editor/state` and wait for `readyForTools=true`.
2. Read the relevant capability resource or use `find_gameobjects`.
3. Apply the smallest mutation with an instance ID or asset path.
4. Re-read the target resource and inspect `read_console`.
5. Use Undo or reload the scene when a validation operation must be cleaned up.

Scene mutations are registered with Unity Undo and are rejected during Play
Mode where an EditMode operation is required. Destructive operations expose
explicit confirmation fields where appropriate.

## Compatibility levels

- **Core:** implemented directly against Unity 2019.4 Editor APIs.
- **Conditional:** implemented, but requires a compatible optional Unity
  package or render pipeline.
- **External:** implemented, but requires network access and user-supplied
  provider credentials.
- **Unavailable:** newer Unity concepts that do not exist in Unity 2019.

See the [Unity 2019 support matrix](Documentation~/support-matrix.md) for the
exact boundaries.

## Complete references

- [48-tool reference](Documentation~/tools-reference.md)
- [25-resource reference](Documentation~/resources-reference.md)
- [Unity 2019 support matrix](Documentation~/support-matrix.md)
- [Security model](SECURITY.md)

The runtime schemas returned by `tools/list` remain authoritative for exact
parameter types, required fields, and annotations.
