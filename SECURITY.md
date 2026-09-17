# Security Policy

ALAZ OPSDECK is intended to keep credentials and private machine state out of source control.

## Never commit
- API tokens, API keys, passwords, private certificates or private keys.
- `.env` files or machine-local credential/config exports.
- DPAPI-protected local credential blobs.
- Runtime logs, captures, backups, flash evidence or local build artifacts.

Use least-privilege, read-only credentials wherever possible. Keep secrets in the operating system's local secure storage or another dedicated secret store.

## Reporting a security issue
Do not open a public issue containing credentials, tokens, private logs or exploit details that would expose a live system. Contact the maintainers privately first and provide a minimal reproduction with secrets removed.
