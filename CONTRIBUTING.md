# Contributing

Contributions should preserve Unity `2019.4` compatibility and keep the Python
server dependency-free.

## Development workflow

1. Install the package as an embedded or local package in a clean Unity
   `2019.4.28f1` project.
2. Keep production Unity code in `Editor/`; do not add project-specific code.
3. Add or update tests for behavior changes.
4. Run `python "Tools~/validate_release.py"`.
5. Run the protocol self-test and bridge smoke test from `README.md`.
6. Confirm the Unity Console has no new errors or warnings attributable to the
   package.
7. Update `CHANGELOG.md` for user-visible changes.

Do not commit credentials, generated `Library` data, provider output, client
configuration, or files copied from private Unity projects.

Version changes follow Semantic Versioning. The package name is a permanent
identifier and must not be renamed after publication.
