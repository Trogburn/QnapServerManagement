# PhotoManager tests

This xUnit project validates the WPF application's non-UI safety boundaries.
It uses only temporary directories and does not invoke Czkawka or modify
repository media. One optional orientation test reads FinePix samples from the
lab share when that share is mounted; it does not write those files.

Run it from the repository root:

```powershell
dotnet test .\src\PhotoManager.Tests\PhotoManager.Tests.csproj -c Release
```

The command exits with code `0` only when every test passes. It covers:

- UNC-only production scan-root validation.
- Artifact-root traversal rejection.
- Atomic artifact creation and cleanup of temporary files.
- Existing-artifact immutability.
- SHA-256 payload tamper detection.
- Workflow transition, failure, and snapshot-confirmation safeguards.
- Date apply-candidate eligibility.

The test project targets `net10.0-windows10.0.19041.0` because it references the WPF
application project and Windows OCR APIs. The checks are intentionally fast enough to run before
every local change and after publishing a new build.

The separate `PhotoManager.UiTests` project contains FlaUI desktop
automation. Those tests open the WPF app and therefore are tagged `UI`, run
serially, and force-close each launched instance. Run them explicitly:

```powershell
dotnet test .\src\PhotoManager.UiTests\PhotoManager.UiTests.csproj -c Release
```
