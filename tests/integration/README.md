# SSH protocol regression tests

This developer-only harness runs the actual SSH.NET client against four local Paramiko SSH servers. Listeners bind only to `127.0.0.1` on dynamically allocated ports. All responses are synthetic; the servers count commands without running a shell.

```powershell
python -m pip install paramiko==4.0.0
./tests/integration/run-protocol-smoke.ps1
# With the repository-local SDK:
./tests/integration/run-protocol-smoke.ps1 -DotnetPath ./.tools/dotnet/dotnet.exe
# Verify the exact libraries and runtime from a published Windows package:
./tests/integration/run-protocol-smoke.ps1 -PublishedDirectory '<extracted portable directory>' -OutputDirectory artifacts/protocol-published
```

Development requires .NET 10, Python and Paramiko. None of these test dependencies ship with the app. `result.json` contains client assertions; `server-counters.json` independently records authentication rounds, forwards and query counts.

The Windows harness performs **35 checks**:

- Three keyboard-interactive rounds per hop, including a non-fixed Duo choice number and a zero-prompt approval round.
- Real endpoint/port fingerprint verification behind the gateway; changed pins block credentials.
- Read-only queries reuse authenticated sessions; disconnect does not trigger authentication.
- Opt-in ordinary-password sharing keeps each host's Duo responses separate.
- Calder-first, then Aire with both authentication methods advertised and **password partial success before keyboard-interactive**. The first session stays usable while the second connects and explicitly reconnects.
- Remembered gateway and per-target passwords still require each host's Duo. Fixture credentials stay in an in-memory store.
- An isolated Unicode password roundtrip through Windows Credential Manager, using a unique `.invalid` endpoint and unconditional cleanup. Existing credentials are never read or modified.

The fourth server reproduces the v0.5.0 compatibility failure: trying keyboard-interactive before password can mark that method failed inside SSH.NET, preventing it from being revisited after password partial success. The old client then emits the same shared-password error as the reported screenshot, after only gateway Duo completes. This reproduces a compatible failure mechanism; it does not establish the university's private SSH configuration.

`-PublishedDirectory` creates an isolated copy of the final portable directory and replaces **only** its `HPCPeek.dll` desktop entry point with the test harness. The published apphost, runtime configuration, dependency manifest, SSH/Core/crypto/native libraries and framework runtime remain byte-identical and are recorded in `published-runtime-verification.json`. The source package is untouched. This verifies authentication and Credential Manager against the trimmed runtime; the separate desktop smoke test verifies the real UI entry point.

These checks do not verify university accounts, actual Duo policies, VPN access, Slurm visibility or macOS Keychain. Production sign-in still requires a real account check.
