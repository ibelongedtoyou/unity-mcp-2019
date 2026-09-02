# Changelog

All notable changes are documented here. Versions follow Semantic Versioning.

## [0.3.0] - 2026-08-28

### Added

- Unity 2019.4-compatible MCP bridge with a dependency-free Python server.
- Streamable HTTP and stdio transports.
- Compatibility surface for 48 tools and 25 resources/resource templates.
- Client setup flows for Codex, Claude, Cursor, Cline, VS Code, Windsurf,
  Gemini CLI, Qwen Code, Antigravity, OpenClaw, and GitHub Copilot CLI.
- UPM package layout, release validation, package tests, security guidance,
  troubleshooting, and upstream attribution.
- Bilingual feature overview, complete 48-tool and 25-resource references,
  Unity 2019 support matrix, and automated documentation coverage checks.

### Security

- Unity bridge and MCP HTTP listener bind to loopback only.
- Unity bridge authentication and HTTP shutdown use random per-session tokens.
- Provider credentials are read from environment variables or, on Windows,
  Windows Credential Manager; secrets are not stored in project files.
