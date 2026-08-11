# Release process

Releases follow Semantic Versioning and are created from annotated `vX.Y.Z`
tags. GitHub Actions is the only supported publisher.

## Prepare

1. Update `<Version>` in `ryzen-smu-cli/ryzen-smu-cli.csproj`.
2. Move changelog entries from `Unreleased` into a dated version section.
3. Add `docs/releases/vX.Y.Z.md`.
4. Update dependency versions and third-party hashes when applicable. If the
   PawnIO installer changes, update its version, pinned URL, SHA-256, notices,
   and installation documentation together.
5. Run from Windows PowerShell:

   ```powershell
   .\scripts\Test.ps1
   .\scripts\PackageRelease.ps1 -Version X.Y.Z
   ```

   `Test.ps1` runs only parser-safe binary smoke checks (`--help` and
   `--version`) after the normal 13-scenario suite and publish. Packaging also
   verifies that the supplied version equals the project version and that the
   shipped README/changelog contain the schema-2 system-telemetry contract.
6. Run the elevated read-only smoke test on supported AMD hardware. For v0.5.0
   or later, separately verify schema 2 against the live Windows logical
   topology; do not add it to unattended CI because it reads privileged MSRs.
7. Verify both archives on a clean Windows installation when practical.

## Publish

Commit the release, then create and push an annotated tag:

```powershell
git tag -a vX.Y.Z -m "ryzen-smu-cli vX.Y.Z"
git push origin master
git push origin vX.Y.Z
```

The `Release` workflow:

1. verifies that the tag matches the project version;
2. restores, builds, and tests the solution;
3. creates framework-dependent, self-contained, self-contained-with-PawnIO,
   and symbols ZIP archives;
4. writes `checksums-sha256.txt`;
5. uploads the archives as a workflow artifact;
6. creates the GitHub Release using the matching file under `docs/releases`.

If a release workflow fails, fix the workflow on `master`, delete the
unpublished remote tag only when no public release references it, recreate the
tag on the corrected commit, and rerun the process. Never move a published
release tag.

## Verify

- The GitHub Release targets the intended commit and is marked Latest.
- All five assets are present: four ZIP archives and the checksum file.
- The SHA-256 file matches locally downloaded archives.
- The framework-dependent package reports a clear missing-runtime error when
  .NET 8 is absent.
- The self-contained package starts without a preinstalled .NET runtime.
- The with-PawnIO archive contains the hash- and signature-verified official
  installer and does not run it automatically.
- `--help` and `--version` work without elevation.
- The read-only hardware smoke test passes when elevated.

After verification, change the `Unreleased` comparison link in
`CHANGELOG.md` to start at the new tag.
