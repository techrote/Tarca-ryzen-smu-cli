[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

# Windows Git treats a repository reached through WSL's UNC share as
# non-local. Supply a process-scoped safe-directory value so ZenStates-Core's
# version target can inspect its own Git metadata without changing user config.
$env:GIT_CONFIG_COUNT = "1"
$env:GIT_CONFIG_KEY_0 = "safe.directory"
$env:GIT_CONFIG_VALUE_0 = "*"
$env:GIT_CONFIG_GLOBAL = "NUL"

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot "artifacts\win-x64"
}

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    Invoke-DotNet restore ".\ryzen-smu-cli.sln"
    Invoke-DotNet build ".\ryzen-smu-cli.sln" `
        --configuration $Configuration `
        --no-restore `
        "-p:TargetFrameworks=net8.0-windows" `
        "-p:IsWindows=false"
    Invoke-DotNet test ".\ryzen-smu-cli.sln" `
        --configuration $Configuration `
        --no-build
    Invoke-DotNet publish ".\ryzen-smu-cli\ryzen-smu-cli.csproj" `
        --configuration $Configuration `
        --no-restore `
        --output $PublishDirectory

    $projectPath = Join-Path $repositoryRoot `
        "ryzen-smu-cli\ryzen-smu-cli.csproj"
    $versionNode = Select-Xml -LiteralPath $projectPath `
        -XPath "/Project/PropertyGroup/Version" |
        Select-Object -First 1
    if ($null -eq $versionNode) {
        throw "The project does not define a Version property."
    }

    $expectedVersion = $versionNode.Node.InnerText
    $publishedExecutable = Join-Path $PublishDirectory "ryzen-smu-cli.exe"
    $versionOutput = @(& $publishedExecutable --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or
        ($versionOutput -join "`n") -notmatch
            [Regex]::Escape($expectedVersion)) {
        throw (
            "Published --version output does not contain project version " +
            "'$expectedVersion'.")
    }

    $helpOutput = @(& $publishedExecutable --help 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Published --help failed with exit code $LASTEXITCODE."
    }

    $helpText = $helpOutput -join "`n"
    foreach ($requiredOption in @(
            "--stream-telemetry",
            "--stream-system-telemetry",
            "--targets",
            "--get-ppt",
            "--get-tdc",
            "--get-edc")) {
        if ($helpText -notmatch [Regex]::Escape($requiredOption)) {
            throw "Published --help is missing '$requiredOption'."
        }
    }
}
finally {
    Pop-Location
}
