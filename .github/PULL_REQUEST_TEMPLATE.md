## Summary

Describe the user-visible change and why it is needed.

## Validation

- [ ] `scripts/Test.ps1` passes.
- [ ] `dotnet format --verify-no-changes` passes.
- [ ] `git diff --check` passes.
- [ ] The closed 13-test allowlist is unchanged, or the PR links the user's
      explicit approval for any replacement/addition and budget change.
- [ ] No `[Theory]`, `[InlineData]`, or unapproved test parameter matrix was
      introduced.
- [ ] No real hardware writes are executed by tests or CI.
- [ ] Documentation and `CHANGELOG.md` are updated when behavior changes.
- [ ] Native file provenance, license, and SHA-256 are updated when applicable.
- [ ] Hardware changes were checked with the read-only smoke test where
      applicable.

## Hardware context

If relevant, list CPU, motherboard, BIOS/AGESA, Windows version, and whether
the test was read-only or involved a reviewed write.
