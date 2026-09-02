# Unity 2019 support matrix

The release baseline is Unity `2019.4.28f1`. "Implemented" means the MCP route
exists and returns explicit success, data, or a capability error; it does not
claim that a newer Unity-only API exists in 2019.

| Capability | Unity 2019 status | Requirement or boundary |
| --- | --- | --- |
| MCP stdio and Streamable HTTP | Core | Python `3.10+`; loopback HTTP only. |
| Editor readiness and authenticated bridge | Core | Bridge metadata and logs live under the consuming project's `Library/UnityMcp2019`. |
| Scenes, GameObjects, Components, Prefabs | Core | Mutations require EditMode where applicable and register Undo. |
| Asset, material, shader, texture, ScriptableObject | Core | Paths remain under the consuming Unity project. |
| C# source editing and validation | Core | Runtime JSON Schema and SHA preconditions protect structured edits. |
| In-memory C# execution | Core, high risk | Trusted local clients only; safety checks are guardrails, not a sandbox. |
| Animator, controllers, clips, curves, events | Core | Uses Unity 2019 animation/editor APIs. |
| Package Manager | Core | Registry/network operations still depend on Unity Package Manager connectivity. |
| Build target/settings/scenes/player build | Core | Installed platform modules determine available targets. |
| Build Profiles | Unavailable | Build Profiles are a newer Unity feature and do not exist in 2019. |
| Unity Camera and screenshots | Core | Scene View capture requires an available Editor view. |
| Cinemachine | Conditional | Requires a Unity-2019-compatible Cinemachine package. |
| 2D/3D physics | Core | Uses built-in physics modules declared by the package. |
| Built-in graphics, skybox, baking, stats | Core | Some bake operations are EditMode-only. |
| URP/HDRP volumes and renderer features | Conditional | Requires a compatible SRP package and configured pipeline assets. |
| ParticleSystem, LineRenderer, TrailRenderer | Core | Built-in Unity components. |
| Visual Effect Graph | Conditional | Requires a compatible Visual Effect Graph package. |
| ProBuilder | Conditional | Requires `com.unity.probuilder`. |
| UXML/USS file CRUD | Core | Text asset operations work without runtime UIDocument types. |
| UIDocument and PanelSettings live operations | Limited | Native runtime types are absent in Unity 2019; tool returns a capability error when unavailable. |
| Profiler frames/counters/basic memory | Core | Uses APIs present in Unity 2019. |
| Memory snapshots and Frame Debugger details | Conditional | Availability varies by installed packages and exposed Editor APIs. |
| EditMode/PlayMode tests | Core | Requires Unity Test Framework, declared as a package dependency. |
| FBX/OBJ local import | Core | Unity built-in importers. |
| GLB/glTF local import | Conditional | Requires a compatible glTF/GLB importer in the consuming project. |
| Safe ZIP model bundles | Core + conditional contents | ZIP extraction is guarded; contained formats still need importers. |
| Image generation/background removal | External | User-configured fal.ai or OpenRouter credentials and network access. |
| Audio generation | External | User-configured fal.ai credentials and network access. |
| 3D generation | External | User-configured Tripo or Meshy credentials and network access. |
| Sketchfab search/import | External | User-configured Sketchfab token and network access. |
| Official Unity documentation lookup | External | Network access to official Unity documentation. |
| Remote network exposure | Not supported | Bridge and MCP HTTP listeners intentionally bind only to loopback. |
| Upstream binary compatibility | Not claimed | This is API/behavior compatibility, not binary compatibility with Unity 2021.3+ packages. |

Optional capabilities are detected at runtime. Missing packages/providers must
produce an explicit availability or configuration error rather than silent
success.
