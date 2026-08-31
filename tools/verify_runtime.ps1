param(
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"

$FwRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ResolvedProjectRoot = if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    (Resolve-Path (Join-Path $FwRoot "..")).Path
} else {
    (Resolve-Path $ProjectRoot).Path
}

& dotnet run --project (Join-Path $FwRoot "csharp/Fw.Verify/Fw.Verify.csproj")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$MarkerPath = Join-Path ([IO.Path]::GetTempPath()) ("fw-runtime-{0}.ok" -f [Guid]::NewGuid().ToString("N"))
$StdOutPath = Join-Path ([IO.Path]::GetTempPath()) ("fw-runtime-{0}.out" -f [Guid]::NewGuid().ToString("N"))
$StdErrPath = Join-Path ([IO.Path]::GetTempPath()) ("fw-runtime-{0}.err" -f [Guid]::NewGuid().ToString("N"))
$PreviousMarker = $env:FW_RUNTIME_VERIFY_MARKER
$env:FW_RUNTIME_VERIFY_MARKER = $MarkerPath
try {
    $GodotCommand = (Get-Command godot -ErrorAction Stop).Source
    $GodotProcess = Start-Process `
        -FilePath $GodotCommand `
        -ArgumentList @("--headless", "--path", $ResolvedProjectRoot, "--script", "res://fw/tools/verify_runtime.gd") `
        -RedirectStandardOutput $StdOutPath `
        -RedirectStandardError $StdErrPath `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    $GodotExitCode = $GodotProcess.ExitCode
} finally {
    $env:FW_RUNTIME_VERIFY_MARKER = $PreviousMarker
}
$GodotText = ((Get-Content -LiteralPath $StdOutPath -Raw -ErrorAction SilentlyContinue) +
    (Get-Content -LiteralPath $StdErrPath -Raw -ErrorAction SilentlyContinue)).TrimEnd()
Remove-Item -LiteralPath $StdOutPath, $StdErrPath -Force -ErrorAction SilentlyContinue
if (-not [string]::IsNullOrWhiteSpace($GodotText)) {
    Write-Output $GodotText
}
if ($GodotExitCode -ne 0) {
    Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
    exit $GodotExitCode
}
if ($GodotText -match "(?m)^(SCRIPT ERROR|ERROR):") {
    Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
    throw "Godot runtime verification reported script or engine errors."
}
if (-not (Test-Path -LiteralPath $MarkerPath)) {
    throw "Godot runtime verification did not reach its success marker."
}
Remove-Item -LiteralPath $MarkerPath -Force
