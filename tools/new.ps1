param(
    [string]$Name = "",

    [string]$ProjectRoot = "",

    [string]$GeneratorProject = "",

    [string]$FrameworkPath = "fwc",

    [switch]$Force
)

$ErrorActionPreference = "Stop"
$FrameworkPath = $FrameworkPath.Replace('\', '/')
foreach ($Part in $FrameworkPath.Split('/')) {
    if ($Part -in @('', '.', '..') -or $Part -notmatch '\A[A-Za-z0-9_. -]+\z' -or $Part.EndsWith(' ') -or $Part.EndsWith('.')) {
        throw 'FrameworkPath must be a project-relative path without traversal or shell metacharacters.'
    }
}

$ResolvedProjectRoot = if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    (Resolve-Path $ProjectRoot).Path
}

$ResolvedGeneratorProject = if ([string]::IsNullOrWhiteSpace($GeneratorProject)) {
    Join-Path $ResolvedProjectRoot "$FrameworkPath/csharp/FwGen/FwGen.csproj"
} else {
    $GeneratorProject
}

Push-Location $ResolvedProjectRoot
try {
    $DotnetArgs = @(
        "run",
        "--project", $ResolvedGeneratorProject,
        "--",
        "--root", $ResolvedProjectRoot,
        "craft",
        "fw-new",
        "--framework-path", $FrameworkPath
    )
    if (-not [string]::IsNullOrWhiteSpace($Name)) {
        $DotnetArgs += @("--name", $Name)
    }
    if ($Force) {
        $DotnetArgs += "--force"
    }
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
