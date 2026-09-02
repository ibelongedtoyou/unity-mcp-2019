# Configuration

The default MCP HTTP endpoint is `http://127.0.0.1:6500/mcp`. The port and
Python executable are stored in user-scoped Unity `EditorPrefs`; they are not
committed to the project.

Client configuration can be created, inspected, and removed from the Clients
tab. Configuration updates merge only the package's MCP entry and preserve
unrelated entries. Always review the target path and generated snippet before
applying a change.

Optional provider credentials can be supplied through these environment
variables:

- `FAL_KEY`
- `OPENROUTER_API_KEY`
- `TRIPO_API_KEY`
- `MESHY_API_KEY`
- `SKETCHFAB_API_TOKEN`

Windows users may instead use the control window to store keys in Windows
Credential Manager. Secret values are never displayed by provider status APIs.
