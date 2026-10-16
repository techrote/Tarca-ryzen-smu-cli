[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Executable,

    [string]$LogPath,

    [switch]$IncludePhysicalTopology,

    [switch]$IncludePowerLimits
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    Start-Transcript -LiteralPath $LogPath -Force | Out-Null
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this read-only hardware smoke test from an Administrator PowerShell."
    }

    $resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
    $readOnlyCommands = @(
        @("--info"),
        @("--get-pbo-scalar"),
        @("--get-offsets-terse"),
        @("--get-enabled-cores"),
        @("--get-fmax"),
        @("--get-vcore"),
        @("--get-cpu-temperature")
    )

    if ($IncludePhysicalTopology) {
        $readOnlyCommands += ,@("--get-physical-cores")
    }

    if ($IncludePowerLimits) {
        $readOnlyCommands += ,@("--get-ppt", "--get-tdc", "--get-edc")
    }

    foreach ($arguments in $readOnlyCommands) {
        Write-Host "`n> $resolvedExecutable $($arguments -join ' ')"
        & $resolvedExecutable @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$($arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host "`nRead-only hardware smoke test passed."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        Stop-Transcript | Out-Null
    }
}
