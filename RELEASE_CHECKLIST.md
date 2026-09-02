# Release checklist

## Local release gate

- [x] `package.json` is at the repository root.
- [x] Package name is `com.ibelongedtoyou.unity-mcp-2019`.
- [x] Package, Python server, and Unity bridge versions are `0.3.0`.
- [x] Minimum Unity version is `2019.4`.
- [x] MIT license and upstream attribution are included.
- [x] No project-specific paths, identities, or credentials are bundled.
- [x] Static release validation and Python contract tests pass.
- [x] Strict CoplayDev protocol parity audit passes.
- [x] Every public tool and resource URI is present in the reference docs.
- [x] A clean Unity `2019.4.28f1` project installs and compiles the package.
- [x] Unity EditMode package tests pass.
- [x] Live MCP initialize, resource read, mutation, property readback, undo,
      delete, and cleanup checks pass.

## Hosting steps

These are intentionally performed only when publishing:

1. Confirm the existing GitHub repository remote is
   `https://github.com/ibelongedtoyou/unity-mcp-2019.git`.
2. Confirm installation URLs point to
   `https://github.com/ibelongedtoyou/unity-mcp-2019.git`.
3. Push the default branch.
4. Create the annotated tag `v0.3.0` and a release from `CHANGELOG.md`.
5. In a separate Unity `2019.4.28f1` project, install the exact tagged Git URL
   and rerun the diagnostics.

Do not publish `Library`, `Temp`, provider output, generated credentials,
client configuration, or the external validation project.
