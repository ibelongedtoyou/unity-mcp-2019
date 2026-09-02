# Security guidance

Only run the package in a trusted local development environment. Do not expose
the MCP endpoint through port forwarding, a reverse proxy, a public tunnel, or
a container bridge reachable by untrusted hosts.

Review every client approval for tools that modify assets, manage packages,
change builds, execute menu items, edit client configuration, or execute C#.
Keep the project under version control and preserve unsaved work before using
destructive operations.

Do not paste API keys into MCP prompts. Configure them through environment
variables or Windows Credential Manager and rotate any credential that appears
in logs, screenshots, issues, or chat transcripts.
