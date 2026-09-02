# Troubleshooting

## Server script not found

Confirm the package is registered and contains `Tools~/server.py`. Reinstall
the package if the Package Cache is incomplete.

## Python process does not start

Run `python --version` in a terminal. Python `3.10+` is required. Set an
absolute Python executable path in **Connect > Advanced** if `python` is not on
`PATH`.

## Bridge timeout

Wait for Unity compilation and asset import to finish. Confirm the Editor is
not blocked by a modal dialog, then run **Restart Bridge** and the bridge smoke
test.

## Port 6500 is unavailable

Choose another loopback port in the control window and update the MCP client
entry. Public or non-loopback bind addresses are intentionally rejected.

## Optional feature unavailable

Install a Unity 2019-compatible version of the required optional package. The
tool response identifies the missing package or API where possible.
