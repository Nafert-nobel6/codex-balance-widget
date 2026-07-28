# Security Policy

## Supported versions

Only the latest GitHub Release is supported. Older releases should be upgraded
before reporting a security issue.

## Reporting a vulnerability

Do not open a public issue containing credentials, account data, raw logs,
personal avatars, or exploit details. Use the repository's **Security** tab and
select **Report a vulnerability** to create a private security advisory. Include:

- affected version and Windows version;
- a minimal reproduction that uses test data;
- expected and observed behavior;
- whether the issue can modify files, start processes, expose data, or bypass
  the signed-runtime check.

Remove usernames, email addresses, access tokens, cookies, API keys, account
identifiers, and original diagnostic logs before attaching evidence.

## Security boundaries

- The widget is a read-only client for quota and reset-card status.
- It does not contain a reset-card consumption method.
- It does not read Codex credential or session files.
- It starts only a private `codex app-server --listen stdio://` process copied
  from an installed Microsoft Store Codex package.
- The copied runtime must pass package-path validation, Authenticode validation
  for `OpenAI OpCo, LLC`, and SHA-256 verification before use.
- App-server JSONL messages are bounded to 1 MiB and parsed without dynamic
  type resolution or code execution.
- The application contains no HTTP client, downloader, plugin loader, script
  evaluator, or remote update mechanism.
- Installation is per-user under `%LOCALAPPDATA%` and writes only one HKCU Run
  value.

The release ZIP does **not** contain OpenAI's `codex.exe`, user settings,
avatars, logs, credentials, or diagnostic reports.

## Verifying a release

Download both the ZIP and its `.sha256` file from the same GitHub Release.
Verify before extraction:

```powershell
$actual = (Get-FileHash .\CodexBalanceWidget-v1.0.0.zip -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ((Get-Content .\CodexBalanceWidget-v1.0.0.zip.sha256) -split '\s+')[0]
if ($actual -ne $expected) { throw 'Release ZIP checksum mismatch.' }
```

The application binaries are not currently Authenticode-signed by this
project, so Windows may show an unknown-publisher warning. Never disable
SmartScreen globally; verify the release checksum and inspect the source or
build locally instead.
