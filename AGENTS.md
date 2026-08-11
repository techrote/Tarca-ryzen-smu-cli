# Agent instructions

These instructions apply to the entire repository.

## Closed automated-test allowlist

The xUnit suite is intentionally fixed at exactly 13 top-level `[Fact]`
scenarios. The allowed boundaries are:

1. invalid CLI input cannot initialize hardware;
2. a Curve Optimizer write is read back from the same selector;
3. compact mapping remains correct when an entire CCD is disabled;
4. terse offsets remain one selector-ordered CSV line;
5. combined offset commands preserve verified receipts and execution order;
6. FMax validation and controller wire behavior remain stable;
7. one-shot Vcore output remains invariant with six decimal places;
8. Vcore streaming remains a five-column protocol;
9. telemetry streaming remains an eight-field JSON line;
10. telemetry read failures never emit a partial sample;
11. PM-table mapping requires the full CPU/table identity and preserves the
    Granite Ridge live-rail regression;
12. unknown or sentinel SVI and temperature data fails closed; and
13. stream cadence advances monotonically to a future interval boundary.

Do not add a test file, test method, test case, `[Theory]`, `[InlineData]`, or
parameter matrix unless the user explicitly authorizes that test addition.
A request to implement a feature, fix a bug, refactor code, change UI/text, or
raise coverage is not authorization to add tests.

Even with explicit approval, replace or merge an existing scenario first. The
suite must remain at 13 top-level `[Fact]` scenarios unless the user separately
and explicitly approves increasing the budget. Multiple representative values
belong inside one existing fact, not in xUnit data rows.

Do not restore tests for UI/DOM details, documentation, CRUD plumbing, simple
mappings, exact prose, implementation call counts, property counts, snapshots,
or permutations of race conditions. Historical release notes and old design
records may describe larger suites; preserve those records rather than treating
them as current policy.

## Hardware and release safety

Automated tests and CI must never access real SMU/AMD ACPI hardware or perform
hardware writes. Keep `scripts/HardwareSmokeTest.ps1` read-only. Preserve the
package-content, license/provenance, native-binary hash, publish, and release
archive checks when changing build or packaging behavior.
