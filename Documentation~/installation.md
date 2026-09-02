# Installation

## Git URL

Use **Window > Package Manager > Add package from git URL...**:

```text
https://github.com/ibelongedtoyou/unity-mcp-2019.git#v0.3.0
```

For maximum Unity 2019.4 compatibility, `package.json` remains at the Git
repository root. Git and Python `3.10+` must be available on `PATH`.

## Local development

Use **Add package from disk...** and select `package.json`, or copy the package
folder under the consuming project's `Packages/` directory as an embedded
package.

After Unity finishes importing, open **Window > MCP for Unity 2019** and run
the diagnostics before configuring a client.
