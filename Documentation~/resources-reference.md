# MCP resource reference

The default catalog exposes 19 fixed resources and 6 resource templates: 25
public entries in total. Every entry returns `application/json`. Read resources
before mutations so the client has current IDs, readiness, and capability
information.

## Fixed resources (19)

| URI | Returned information |
| --- | --- |
| `mcpforunity://editor/state` | Canonical readiness, compile/update/play state, active scene, and Unity version. |
| `mcpforunity://custom-tools` | Project-scoped custom tool registrations. |
| `mcpforunity://editor/active-tool` | Current Unity transform/editor tool. |
| `mcpforunity://editor/prefab-stage` | Current Prefab isolation-stage state. |
| `mcpforunity://editor/selection` | Selected Unity objects and instance IDs. |
| `mcpforunity://editor/windows` | Open Editor windows. |
| `mcpforunity://instances` | Unity Editor instances available to the server. |
| `mcpforunity://menu-items` | Executable Editor menu paths. |
| `mcpforunity://pipeline/renderer-features` | Active render pipeline and renderer-feature summary. |
| `mcpforunity://prefab-api` | Prefab capability and action summary. |
| `mcpforunity://project/info` | Project path/version and detected package/runtime capabilities. |
| `mcpforunity://project/layers` | Configured Unity layers. |
| `mcpforunity://project/tags` | Configured Unity tags. |
| `mcpforunity://rendering/stats` | Current rendering counters and statistics. |
| `mcpforunity://scene/cameras` | Cameras in loaded scenes and available camera capabilities. |
| `mcpforunity://scene/gameobject-api` | GameObject management capability summary. |
| `mcpforunity://scene/volumes` | Detected volume-like scene components. |
| `mcpforunity://tests` | Discovered EditMode and PlayMode tests. |
| `mcpforunity://tool-groups` | Known tool groups and their active/available state. |

## Resource templates (6)

| URI template | Returned information |
| --- | --- |
| `mcpforunity://prefab/{encoded_path}` | Prefab metadata for an encoded `Assets/...` path. |
| `mcpforunity://prefab/{encoded_path}/hierarchy` | Prefab asset hierarchy. |
| `mcpforunity://scene/gameobject/{instance_id}` | Loaded GameObject identity, scene/path, transform, parent/children, and component list. |
| `mcpforunity://scene/gameobject/{instance_id}/component/{component_name}` | Serialized properties for one component type on a loaded GameObject. |
| `mcpforunity://scene/gameobject/{instance_id}/components` | Component summary for a loaded GameObject. |
| `mcpforunity://tests/{mode}` | Tests filtered to `editmode` or `playmode`. |

`encoded_path` is the URL-encoded Unity asset path. `instance_id` must come
from the current Editor session; IDs are not stable across domain reloads or
Editor restarts.

## Backward-compatible resource aliases

Two Unity-2019-specific aliases remain readable for older integrations but are
not included in the default 25-entry count:

- `unity2019://editor/state`
- `unity2019://scene/gameobject/{instance_id}`

## Resource-first examples

Before a write:

```text
read mcpforunity://editor/state
find_gameobjects(search_term="Player", search_method="by_name")
read mcpforunity://scene/gameobject/{instance_id}
manage_components(...)
read mcpforunity://scene/gameobject/{instance_id}/components
```

After a domain reload, re-read `mcpforunity://editor/state`, reacquire object
IDs, and then continue.
