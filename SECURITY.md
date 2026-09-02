# Security policy

## Supported versions

Security fixes are maintained for the latest `0.3.x` release until a newer
minor release is published.

## Trust model

This package is a developer tool with intentionally powerful access to the
Unity Editor. A connected MCP client can read project data and, when a tool is
invoked, modify scenes, assets, packages, build settings, and local client
configuration. The `execute_code` tool can compile and execute in-memory C#.
Its safety checks are a guardrail, not a security sandbox, and clients can ask
to disable them.

The Unity bridge and HTTP MCP endpoint bind only to loopback. The Unity bridge
uses a random token regenerated after domain reload; the HTTP shutdown endpoint
uses a separate random control token. The regular MCP HTTP endpoint does not
authenticate individual local clients, so untrusted local processes remain in
scope as a risk.

## Credentials and external services

No API keys are included. Provider credentials are read from environment
variables or Windows Credential Manager. They are not written to project files,
MCP client configuration, logs, or process arguments. On non-Windows systems,
use environment variables.

Provider-backed generation can transmit prompts, images, or model inputs to
the configured provider. Review that provider's terms before invoking a tool.

## Reporting a vulnerability

After the repository is hosted, use GitHub's private security advisory feature.
Until then, contact the maintainer privately. Do not include live credentials,
private project data, or exploitable details in a public issue.
