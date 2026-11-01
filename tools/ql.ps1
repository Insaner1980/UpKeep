$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$checkerRoot = if ($env:WINDOWS_CHECK_ROOT) { $env:WINDOWS_CHECK_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'Windows-check' }
$checker = Join-Path $checkerRoot 'tools/InvokeWindowsProjectCheck.ps1'
if (-not (Test-Path -LiteralPath $checker -PathType Leaf)) {
    throw 'Windows-check is unavailable. Set WINDOWS_CHECK_ROOT to its checkout directory. See README.md.'
}
& $checker -Root $projectRoot -Check codeql @args
exit $LASTEXITCODE
