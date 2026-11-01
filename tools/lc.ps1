$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& 'C:\Dev\Windows-check\tools\InvokeWindowsProjectCheck.ps1' -Root $projectRoot -Check lint @args
exit $LASTEXITCODE
