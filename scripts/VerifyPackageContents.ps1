[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FrameworkDependentArchive,

    [Parameter(Mandatory)]
    [string]$SelfContainedArchive,

    [Parameter(Mandatory)]
    [string]$PawnIoBundleArchive,

    [Parameter(Mandatory)]
    [string]$SymbolsArchive,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ZenStatesLicensePath
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ArchiveEntryNames {
    param([Parameter(Mandatory)][string]$ArchivePath)

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        $duplicate = $names |
            Group-Object -CaseSensitive |
            Where-Object { $_.Count -gt 1 } |
            Select-Object -First 1
        if ($null -ne $duplicate) {
            throw "Archive '$ArchivePath' contains duplicate entry '$($duplicate.Name)'."
        }

        return $names
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ContainsEntries {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string[]]$Actual,
        [Parameter(Mandatory)][string[]]$Required
    )

    foreach ($entry in $Required) {
        if ($Actual -cnotcontains $entry) {
            throw "Archive '$ArchivePath' is missing required entry '$entry'."
        }
    }
}

function Assert-ExcludesEntries {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string[]]$Actual,
        [Parameter(Mandatory)][string[]]$Forbidden
    )

    foreach ($entry in $Forbidden) {
        if ($Actual -ccontains $entry) {
            throw "Archive '$ArchivePath' contains forbidden entry '$entry'."
        }
    }
}

function Assert-ExactEntries {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string[]]$Actual,
        [Parameter(Mandatory)][string[]]$Expected
    )

    $unexpected = @($Actual | Where-Object { $Expected -cnotcontains $_ })
    $missing = @($Expected | Where-Object { $Actual -cnotcontains $_ })
    if ($unexpected.Count -ne 0 -or $missing.Count -ne 0) {
        throw (
            "Archive '$ArchivePath' has unexpected contents. Missing: " +
            "[$($missing -join ', ')]. Unexpected: " +
            "[$($unexpected -join ', ')].")
    }
}

function Get-ArchiveEntrySha256 {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$EntryName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive '$ArchivePath' has no entry '$EntryName'."
        }
        if ($entry.Length -eq 0) {
            throw "Archive '$ArchivePath' entry '$EntryName' is empty."
        }

        $stream = $entry.Open()
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
            return ([BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ArchiveEntryText {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$EntryName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive '$ArchivePath' has no entry '$EntryName'."
        }

        $stream = $entry.Open()
        $reader = [IO.StreamReader]::new(
            $stream,
            [Text.Encoding]::UTF8,
            $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

$runtimeEntries = @(
    "ryzen-smu-cli.exe",
    "inpoutx64.dll",
    "InpOut.LICENSE.txt",
    "PawnIO.COPYING.txt",
    "ZenStates-Core.LICENSE.txt",
    "ryzen-smu-cli.LICENSE.txt",
    "THIRD-PARTY-NOTICES.md",
    "ryzen-smu-cli.README.md",
    "CHANGELOG.md"
)
$dotnetNotices = @(
    "dotnet-LICENSE.txt",
    "dotnet-ThirdPartyNotices.txt"
)
$ambiguousDocuments = @("LICENSE", "README.md")
$pawnIoInstaller = "PawnIO_setup-v2.2.0.exe"
$symbolEntries = @("ryzen-smu-cli.pdb", "ZenStates-Core.pdb")

$frameworkEntries = Get-ArchiveEntryNames $FrameworkDependentArchive
$selfContainedEntries = Get-ArchiveEntryNames $SelfContainedArchive
$pawnIoEntries = Get-ArchiveEntryNames $PawnIoBundleArchive
$symbolsEntries = Get-ArchiveEntryNames $SymbolsArchive

Assert-ExactEntries $FrameworkDependentArchive $frameworkEntries $runtimeEntries
Assert-ContainsEntries $SelfContainedArchive $selfContainedEntries `
    ($runtimeEntries + $dotnetNotices)
Assert-ContainsEntries $PawnIoBundleArchive $pawnIoEntries `
    ($runtimeEntries + $dotnetNotices + $pawnIoInstaller)
Assert-ExcludesEntries $FrameworkDependentArchive $frameworkEntries `
    ($dotnetNotices + $pawnIoInstaller + $ambiguousDocuments)
Assert-ExcludesEntries $SelfContainedArchive $selfContainedEntries `
    ($pawnIoInstaller + $ambiguousDocuments)
Assert-ExcludesEntries $PawnIoBundleArchive $pawnIoEntries $ambiguousDocuments
Assert-ExactEntries $SymbolsArchive $symbolsEntries $symbolEntries

$pawnIoExpectedEntries = @($selfContainedEntries + $pawnIoInstaller)
Assert-ExactEntries $PawnIoBundleArchive $pawnIoEntries $pawnIoExpectedEntries

$expectedLicenseHash = (
    Get-FileHash -LiteralPath $ZenStatesLicensePath -Algorithm SHA256
).Hash.ToLowerInvariant()
foreach ($archivePath in @(
        $FrameworkDependentArchive,
        $SelfContainedArchive,
        $PawnIoBundleArchive)) {
    $archiveLicenseHash = Get-ArchiveEntrySha256 `
        $archivePath "ZenStates-Core.LICENSE.txt"
    if ($archiveLicenseHash -ne $expectedLicenseHash) {
        throw (
            "Archive '$archivePath' contains the wrong ZenStates-Core license. " +
            "Expected SHA-256 $expectedLicenseHash, got $archiveLicenseHash.")
    }

    $readme = Get-ArchiveEntryText `
        $archivePath "ryzen-smu-cli.README.md"
    foreach ($requiredContractMarker in @(
            "--stream-system-telemetry",
            "--targets <group:processor,...>",
            '"schemaVersion":2',
            '"packagePowerSource":"amd-rapl-package-energy"')) {
        if ($readme.IndexOf(
                $requiredContractMarker,
                [StringComparison]::Ordinal) -lt 0) {
            throw (
                "Archive '$archivePath' README is missing schema-2 marker " +
                "'$requiredContractMarker'.")
        }
    }

    $changelog = Get-ArchiveEntryText $archivePath "CHANGELOG.md"
    if ($changelog.IndexOf(
            "## [$Version]",
            [StringComparison]::Ordinal) -lt 0) {
        throw (
            "Archive '$archivePath' changelog is missing version '$Version'.")
    }
}

Write-Host "Release package content verification passed."
