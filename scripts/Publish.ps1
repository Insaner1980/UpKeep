param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $projectRoot "artifacts/publish/UpKeep-win-x64-$stamp"
$app = Join-Path $projectRoot 'src/UpKeep.App/UpKeep.App.csproj'
$helper = Join-Path $projectRoot 'src/UpKeep.CleanupHost/UpKeep.CleanupHost.csproj'
dotnet publish $app -c $Configuration -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $destination --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish $helper -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $destination --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed.' }
foreach ($required in @('UpKeep.App.exe', 'UpKeep.App.pri', 'App.xbf', 'MainWindow.xbf', 'UpKeep.CleanupHost.exe', 'Assets/AppIcon.ico', 'Updates/PowerShellResources.ps1', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Missing publish file: $required" }
}
$archive = "$destination.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($destination, $archive)
Write-Output "Published folder: $destination"
Write-Output "Published ZIP: $archive"
