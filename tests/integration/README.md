# SSH protocol regression tests

This developer-only harness runs the actual SSH.NET client against four local Paramiko SSH servers. Listeners bind only to `127.0.0.1` on dynamically allocated ports. All responses are synthetic; the servers count commands without running a shell.

Run these commands in PowerShell 7 on Windows or Linux, with .NET 10 and a Python environment that has Paramiko 4.0.0 installed. Use `python -m pip install paramiko==4.0.0` on Windows. On Linux you can install the fixture dependency in a virtual environment:

```bash
python3 -m venv .tools/protocol-venv
.tools/protocol-venv/bin/python -m pip install paramiko==4.0.0
```

```powershell
# Source build on Windows (use -DotnetPath ./.tools/dotnet/dotnet.exe for the repository-local SDK)
./tests/integration/run-protocol-smoke.ps1
# Source build on Linux using the virtual environment above
./tests/integration/run-protocol-smoke.ps1 -PythonPath ./.tools/protocol-venv/bin/python
```

To verify the **extracted portable package** on either native platform, use the extraction directory recorded by the build script's desktop smoke test. For example, after building version 1.2.0 with native smoke enabled:

```powershell
$rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$manifest = Get-Content "artifacts/v1.2.0/manifest-$rid.json" -Raw | ConvertFrom-Json
$python = if ($IsWindows) { 'python' } else { './.tools/protocol-venv/bin/python' }
./tests/integration/run-protocol-smoke.ps1 -PythonPath $python -PublishedDirectory $manifest.smoke.Extracted -OutputDirectory "artifacts/protocol-$rid-$([guid]::NewGuid().ToString('N'))"
```

Development requires .NET 10, Python and Paramiko. None of these test dependencies ship with the app. `result.json` contains client assertions; `server-counters.json` independently records authentication rounds, forwards and query counts.

Both platforms exercise the same SSH scenarios:

- Three keyboard-interactive rounds per hop, including a non-fixed Duo choice number and a zero-prompt approval round.
- Real endpoint/port fingerprint verification behind the gateway; changed pins block credentials.
- Read-only queries reuse authenticated sessions; disconnect does not trigger authentication.
- Opt-in ordinary-password sharing keeps each host's Duo responses separate.
- Calder-first, then Aire with both authentication methods advertised and **password partial success before keyboard-interactive**. The first session stays usable while the second connects and explicitly reconnects.
- Remembered gateway and per-target passwords still require each host's Duo. Fixture credentials stay in an in-memory store.

Windows additionally checks an isolated Unicode password roundtrip through Credential Manager using a unique `.invalid` endpoint and unconditional cleanup.

The fourth server reproduces the v0.5.0 compatibility failure: trying keyboard-interactive before password can mark that method failed inside SSH.NET, preventing it from being revisited after password partial success. The old client then emits the same shared-password error as the reported screenshot, after only gateway Duo completes. This reproduces a compatible failure mechanism; it does not establish the university's private SSH configuration.

`-PublishedDirectory` creates an isolated copy of the final portable directory and replaces **only** its `HPCPeek.dll` desktop entry point with the test harness. The published apphost, runtime configuration, dependency manifest, SSH/Core/crypto/native libraries and framework runtime remain byte-identical and are recorded in `published-runtime-verification.json`. Launching the copied `HPCPeek` (Linux) or `HPCPeek.exe` (Windows) exercises authentication against that package's trimmed runtime. The separate desktop smoke test launches the real UI entry point.

These checks do not verify university accounts, actual Duo policies, VPN access, Slurm visibility or macOS Keychain. Production sign-in still requires a real account check.
