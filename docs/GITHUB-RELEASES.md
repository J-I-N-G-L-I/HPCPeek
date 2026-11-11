# Publish HPCPeek on GitHub

Source code belongs in the repository. Runnable ZIPs and `SHA256SUMS.txt` belong in **Releases → Assets**. The included `.gitignore` excludes build caches, local credentials, portable settings, release output and development screenshot archives; the curated images under `docs/images` stay in Git.

## 1. Push the source

Create an empty GitHub repository named **HPCPeek**. Do not initialize it with another README or `.gitignore`. Authenticate Git using GitHub Desktop, Git Credential Manager or your usual SSH setup. These steps follow [GitHub's local-source guide](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github).

In PowerShell, replace `YOUR-ACCOUNT` below:

```powershell
Set-Location D:\Projects\Aire_Peek
git init -b main
git add .
git status --short
git diff --cached --stat
```

Review the staged list before continuing. It should contain source, tests, scripts, documentation and `.github/workflows`; it must not include `portable-data`, `.tools`, `.nuget`, `artifacts`, passwords or signing keys. Ignore rules do not remove files already tracked in an existing repository. [GitHub's ignore-file guide](https://docs.github.com/en/get-started/git-basics/ignoring-files)

```powershell
git commit -m "Release HPCPeek v1.0.0"
git remote add origin https://github.com/YOUR-ACCOUNT/HPCPeek.git
git push -u origin main
```

The commands above are for the first push. For later updates, use `git add`, `git commit` and `git push`; do not repeat `git init` or add a second `origin`.

## 2. Let Actions build and publish

Open the repository's **Actions** tab and confirm the ordinary-commit checks pass. Then tag the tested commit:

```powershell
git tag -a v1.0.0 -m "HPCPeek v1.0.0"
git push origin v1.0.0
```

The included workflow tests the code, builds Windows x64 and both Mac architectures on native runners, extracts each ZIP, runs offline UI checks and captures performance. Only after all required jobs pass does it upload **three ZIPs and `SHA256SUMS.txt`** to the `v1.0.0` GitHub Release. Actions artifacts hold test evidence; users download the runnable packages from **Releases**, not from Actions or the repository's **Code → Download ZIP**.

The release job requests `contents: write` for the repository's temporary `GITHUB_TOKEN`; no HPC password or manually created GitHub token is needed. If an organization policy blocks the workflow's Actions or token permissions, resolve that in repository/organization settings before retrying. Creating a Release manually from a browser is also supported by [GitHub's release interface](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

Do not move a published version tag to different code. Fixes should increment the project version and use a new tag, for example `v1.0.1`.

## 3. macOS signing

Without signing credentials, Mac packages include `-unsigned-test` in their names and the automated Release is marked **pre-release**. This is intentional; it does not establish Gatekeeper acceptance.

For signed and notarized Mac packages, add all six **Settings → Secrets and variables → Actions → Repository secrets**:

| Secret | Value |
| --- | --- |
| `MACOS_CERTIFICATE_BASE64` | Base64-encoded Developer ID Application `.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | Password for that `.p12` |
| `MACOS_SIGNING_IDENTITY` | Full Developer ID Application signing identity |
| `APPLE_ID` | Apple account used for notarization |
| `APPLE_TEAM_ID` | Apple Developer team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific password for notarization |

All six are required together. Store them only as Actions secrets, never in Git or in the download ZIP. The native Mac workflow signs, notarizes, staples and verifies before producing packages without the test suffix. See [build and signing details](BUILD-CROSSPLATFORM.md).

## Upload the existing local packages instead

If you want to distribute this workspace's locally built files, use a **draft Release**, attach the three ZIPs from `artifacts/v1.0.0/` plus that directory's `SHA256SUMS.txt`, then publish. State exactly which platforms were tested; cross-compiling a Mac ZIP on Windows is not a Mac execution test. Keep the `-unsigned-test` suffix and pre-release status when applicable.

Choose this route instead of the automated tag publication, so the workflow does not replace the same assets. Disable the release workflow temporarily before pushing the tag, upload/publish the draft, then re-enable it for future versions. Do not run `publish-release.ps1` manually for local-only builds: its notes describe the native CI checks.

## Keep future releases small

Commit source changes and curated documentation; keep the full `artifacts` directory local. Upload only the architecture ZIPs and checksum file as Release Assets. Build logs, extracted test folders, benchmark samples and raw screenshots belong in Actions evidence artifacts, not the download package. The checksum must be regenerated whenever a ZIP changes.

## Tell existing users how to upgrade

Include these steps in the release notes: quit HPCPeek, extract the new Windows ZIP into a new writable folder, and copy the previous `portable-data` folder beside the new `HPCPeek.exe`. This retains editable account settings, preferences and trusted host fingerprints. On macOS, replace the whole `.app`; settings remain under `~/Library/Application Support/HPCPeek`.

Passwords saved by **Remember on this device** remain in the same operating-system user's Credential Manager / Keychain, not in `portable-data`. A new computer needs its own sign-in and explicit password-storage choice. Never ask users to attach their configuration folder or system credentials to a GitHub issue.
