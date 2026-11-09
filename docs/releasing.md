# Releasing Deming

A release is a git tag `vX.Y.Z` (or `vX.Y.Z-rc.1`, published as a GitHub pre-release).
The tag is the version: the workflow passes `-p:Version=X.Y.Z` and overrides the default in
`Directory.Build.props`.

## What the tag produces

`.github/workflows/release.yml`:

1. `test`: `dotnet build` + `dotnet test` (must pass, nothing is published otherwise).
2. `binaries`: self-contained single-file `deming` for `win-x64` (zip), `linux-x64` and
   `osx-arm64` (tar.gz), each built on a native runner and smoke-tested
   (`deming help`, `deming install claude --dir <temp>` and `deming install agents` in a
   temporary repository, both from the embedded agent instructions).
3. `tool-package`: `dotnet pack` creates the .NET tool: the pointer package `Deming` plus the
   self-contained per-platform packages `Deming.win-x64`, `Deming.linux-x64`,
   `Deming.osx-arm64`. `dotnet tool install -g Deming` picks the matching one; no .NET runtime
   needed beyond the SDK that runs the install.
4. `github-release`: GitHub Release with the three archives and `SHA256SUMS`.
5. `nuget`: pushes the platform packages first, the pointer package last (`--skip-duplicate`).

`.github/workflows/ci.yml` builds and tests on Linux, Windows and macOS for every pull request
and push to `main`.

## One-time setup (repository owner)

- NuGet: publishing uses Trusted Publishing (GitHub OIDC), no stored API key. On nuget.org, under
  your user name > Trusted Publishing, create a policy: repository owner and repository of this
  repo, workflow file `release.yml` (file name only), environment `nuget`. The first successful
  push creates the package IDs (`PackageId` in `src/Deming/Deming.csproj` plus `Deming.win-x64`
  etc.) under the policy owner. A policy for a private repository is only temporarily active for
  7 days until the first publish; restart the window if it lapses.
- GitHub: an environment named `nuget` with the secret `NUGET_USER` set to the nuget.org profile
  name, not the e-mail address (add required reviewers to the environment if a manual approval
  before the push is wanted).
- GitHub: allow `GITHUB_TOKEN` to create releases (the job requests `contents: write` itself).
- Optional: protect tags matching `v*` so only maintainers can create them.

## Cutting a release

    git tag v0.1.0
    git push origin v0.1.0

## Linux: libicu

Deming runs with ICU globalization (`InvariantGlobalization=false`, chosen for culture-correct behaviour). On Linux the single-file binary and the tool need the
system ICU library: `libicu` (Debian/Ubuntu: `apt install libicu-dev` or the versioned
`libicuNN`; Fedora: `libicu`; Alpine: `icu-libs`). Without it .NET aborts at startup with
"Couldn't find a valid ICU package". Windows and macOS need nothing extra. The README must list
this under Linux install. A bundled app-local ICU was not chosen: it adds ~30 MB per platform and
a version-matching knob to maintain.

## HTTPS and CI

`deming serve` listens on `https://127.0.0.1:5001` only, with no http fallback, and
generates its own self-signed certificate on first start: `deming-https.pfx` and
`deming-https.crt` beside the database, fingerprint in the config file as `certificateSha256`. The
CLI trusts that certificate by its fingerprint; nothing is added to a trust store unless the user
runs `deming trust`. So neither users nor CI need the .NET SDK or a trust step.

- Tests that start a real Kestrel use `TestCertificate` (the same generator, held in memory) and
  clients that trust only that certificate. Tests that exercise generation write the certificate
  and `config.json` to a temporary directory; `deming trust` is tested against a fake store, never
  the real one.
- The release workflow smoke-tests each binary: `deming help`, `deming install claude`, `deming install agents`, and
  `deming serve` on `https://127.0.0.1:5099` with a temporary `DEMING_CONFIG` and
  `--Deming:DatabasePath`: it waits for `/health` with `curl --cacert deming-https.crt` (that
  certificate only, never `-k`), then runs `deming projects` (TLS by the fingerprint plus the API
  key the server wrote, no trust step) and checks that the fingerprint was recorded.
- Real trust-store writes (Windows confirmation dialog, macOS keychain) are verified by hand.

## Verify locally

    dotnet pack src/Deming -c Release -o artifacts/pack
    dotnet tool install Deming --tool-path artifacts/tool --add-source artifacts/pack
    dotnet publish src/Deming -c Release -r win-x64 -o artifacts/win-x64
