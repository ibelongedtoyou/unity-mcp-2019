# Compatibility

The release baseline is Unity `2019.4.28f1`. The package is an API and behavior
compatibility implementation for a newer MCP for Unity protocol surface; it is
not binary-compatible with packages that require Unity `2021.3` or later.

Known boundaries:

- Unity 2019 has no native `UIDocument` or `PanelSettings` runtime types.
- ProBuilder, Cinemachine, Visual Effect Graph, URP/HDRP, GLTF/GLB import, and
  similar features depend on compatible optional packages in the project.
- Build Profiles are unavailable in Unity 2019.
- Console capture begins when the bridge loads and is not a replacement for
  Unity's complete historical Editor log.
- Windows Credential Manager integration is Windows-only; environment variables
  are supported on every platform Python supports.
