# Contributing

## Repository setup

Clone with submodules and keep the two remotes distinct:

```powershell
git clone --recurse-submodules git@github.com:Terry577/Arca-ryzen-smu-cli.git
cd Arca-ryzen-smu-cli
git remote add upstream https://github.com/rawhide-kobayashi/ryzen-smu-cli.git
git remote set-url --push upstream DISABLED
```

`origin` is this fork. `upstream` is read-only by convention. Develop on a
topic branch and do not commit generated `bin`, `obj`, `artifacts`, or test
result directories.

After pulling a commit that changes the submodule pointer, run:

```powershell
git submodule update --init --recursive
```

## Required checks

From Windows PowerShell:

```powershell
.\scripts\Test.ps1
```

From WSL:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File "$(wslpath -w "$PWD/scripts/Test.ps1")"
```

The helper restores packages, builds Release, runs all tests, and creates the
framework-dependent win-x64 publish directory under `artifacts`.

## Test budget

The automated suite is a closed allowlist of 13 critical `[Fact]` scenarios.
Read the root `AGENTS.md` before changing tests. A feature, bug fix, refactor,
documentation change, or coverage request does not authorize a new test.
Without explicit user approval, do not add a test file, method, case, data row,
`[Theory]`, or `[InlineData]`. Even when approved, merge or replace an existing
critical scenario so the total remains 13 unless the user separately approves
raising the budget.

Release archives can be reproduced locally with:

```powershell
.\scripts\PackageRelease.ps1
```

Before opening a pull request:

1. Confirm the closed 13-scenario allowlist remains intact and no unapproved
   test entry or parameter matrix was added.
2. Run `dotnet format --verify-no-changes`.
3. Run `git diff --check`.
4. Confirm that `inpoutx64.dll` is present beside the published executable and
   has the documented SHA-256.
5. When release packaging changes, confirm the with-PawnIO archive contains
   only the verified official installer and that no installation is automatic.
6. If hardware behavior changed, run the read-only smoke test from an
   Administrator PowerShell and record the CPU, motherboard, BIOS/AGESA, and
   Windows versions in the pull request.

## Hardware safety

Automated tests must use `IRyzenController` test doubles. CI must never invoke
real SMU or AMD ACPI writes. `scripts/HardwareSmokeTest.ps1` deliberately
contains only these reads:

- PBO scalar
- Curve Optimizer offsets
- factory-fused core slots
- current enabled-core mapping
- maximum boost-frequency limit (FMax)

Any manual write test must be explicitly reviewed, limited to known-safe
values, and performed with recoverable BIOS settings.

## Documentation and releases

- Update `README.md` when installation, usage, requirements, or output change.
- Add every user-visible change to the `Unreleased` section of
  `CHANGELOG.md`.
- Update `docs/ARCHITECTURE.md` when component boundaries or hardware flows
  change.
- Follow `docs/RELEASING.md`; releases are created only by the tag-driven
  GitHub Actions workflow.
- Keep version-specific release notes under `docs/releases/`.
- Never move or recreate a tag after its GitHub Release is public.
