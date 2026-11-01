#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$PlanOnly,
    [switch]$AllowExternalUpload
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$OutputEncoding = [Console]::OutputEncoding

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$config = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'windows-check.config.psd1')
$testProject = $config.TestProjects[0]
$configuration = $testProject.Configuration
$platform = $testProject.Properties.Platform
$testFilter = $testProject.Properties.VSTestTestCaseFilter
# Include the bounded native fixture and read-only WinGet catalog check in coverage.
$testFilter = "($testFilter)|FullyQualifiedName~UpKeep.Tests.NativeRecycleIntegrationTests|FullyQualifiedName~UpKeep.Tests.WinGetIntegrationTests"
$projectKey = 'Insaner1980_UpKeep' # Public SonarQube project identifier.
$organization = 'insaner1980'
$projectUrl = "https://sonarcloud.io/project/overview?id=$projectKey"
$reportPath = Join-Path $projectRoot 'reports/sonar.txt'
$resultsRelativePath = 'reports/sonar-test-results'
$testResultsDir = Join-Path $projectRoot $resultsRelativePath
$coveragePath = Join-Path $testResultsDir 'UpKeep.Tests.xml'
$trxPath = Join-Path $testResultsDir 'UpKeep.Tests.trx'
$uiCoveragePath = Join-Path $testResultsDir 'UpKeep.UiTests.xml'
$uiReportPath = Join-Path $testResultsDir 'UpKeep.UiTests.json'
$uiExecutable = Join-Path $projectRoot "artifacts/bin/UpKeep.UiTests/$platform/$configuration/net10.0-windows10.0.26100.0/win-x64/UpKeep.UiTests.exe"
$testCommand = "dotnet test `"$($testProject.Path)`" -c $configuration -p:Platform=$platform --no-build --no-restore --filter `"$testFilter`" --results-directory `"$resultsRelativePath`" --logger `"trx;LogFileName=UpKeep.Tests.trx`""

if ($PlanOnly) {
    Write-Output @(
        'sonar'
        '  - dotnet tool restore'
        "  - dotnet restore $($config.Solution) -p:Platform=$platform"
        "  - SonarScanner begin: $projectKey ($organization)"
        "  - dotnet build $($config.Solution) -c $configuration -p:Platform=$platform --no-restore --no-incremental --disable-build-servers -m:1 -nr:false"
        "  - dotnet-coverage collect: $testCommand"
        '  - dotnet-coverage collect: isolated WinUI workflow tests (interactive desktop required)'
        '  - Validate fresh VS Coverage XML and successful, nonempty TRX results'
        '  - SonarScanner end: upload analysis to SonarQube Cloud'
        '  - Requires SONAR_TOKEN and -AllowExternalUpload; the sonar profile command supplies these'
        "  - results: $projectUrl"
        "  - report: $reportPath"
        "  - coverage and test results: $testResultsDir"
    )
    exit 0
}

if (-not $AllowExternalUpload) {
    throw 'Sonar-analyysi lähettää tulokset SonarQube Cloudiin. Käytä sonar-komentoa tai -AllowExternalUpload.'
}
if ([string]::IsNullOrWhiteSpace($env:SONAR_TOKEN)) {
    throw 'SONAR_TOKEN puuttuu. PowerShell-profiilin sonar-komento lataa tallennetun tunnisteen.'
}

function Invoke-DotNetCommand {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & dotnet @Arguments 2>&1 | ForEach-Object {
            ([string]$_).Replace($env:SONAR_TOKEN, '[REDACTED_SECRET]')
        } | Tee-Object -FilePath $reportPath -Append | Out-Host
        $commandExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($commandExitCode -ne 0) {
        throw "dotnet-komento epäonnistui (exit $commandExitCode)."
    }
}

New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
# Only these reports belong to this script; remove stale results before collecting.
foreach ($path in @($coveragePath, $trxPath, $uiCoveragePath, $uiReportPath)) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Remove-Item -LiteralPath $path
    }
}
Set-Content -LiteralPath $reportPath -Encoding utf8 -Value @(
    'sonar'
    "Root: $projectRoot"
    "Project: $projectKey"
    "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    ''
)

Push-Location -LiteralPath $projectRoot
try {
    Invoke-DotNetCommand -Arguments @('tool', 'restore')
    Invoke-DotNetCommand -Arguments @('restore', $config.Solution, "-p:Platform=$platform", '-m:1', '-nr:false')
    $beginArguments = @(
        'tool', 'run', 'dotnet-sonarscanner', '--', 'begin'
        "/k:$projectKey"
        "/o:$organization"
        '/d:sonar.host.url=https://sonarcloud.io'
        "/d:sonar.token=$env:SONAR_TOKEN"
        "/d:sonar.cs.vstest.reportsPaths=$resultsRelativePath/UpKeep.Tests.trx"
        "/d:sonar.cs.vscoveragexml.reportsPaths=$resultsRelativePath/UpKeep.Tests.xml,$resultsRelativePath/UpKeep.UiTests.xml"
        '/d:sonar.scanner.scanAll=false'
        '/d:sonar.exclusions=artifacts/**,reports/**,**/bin/**,**/obj/**,src/UpKeep.App/Assets/**'
    )
    # An initial local analysis can run before the repository has its first commit.
    git rev-parse --verify --quiet HEAD *> $null
    if ($LASTEXITCODE -ne 0) {
        $beginArguments += '/d:sonar.scm.disabled=true'
    }
    Invoke-DotNetCommand -Arguments $beginArguments
    Invoke-DotNetCommand -Arguments @(
        'build', $config.Solution, '-c', $configuration, "-p:Platform=$platform"
        '--no-restore', '--no-incremental', '--disable-build-servers', '-m:1', '-nr:false'
    )
    Invoke-DotNetCommand -Arguments @(
        'tool', 'run', 'dotnet-coverage', '--', 'collect', $testCommand
        '-f', 'xml', '-o', $coveragePath
    )
    $previousUiReport = $env:UPKEEP_UI_TEST_REPORT
    try {
        $env:UPKEEP_UI_TEST_REPORT = $uiReportPath
        Invoke-DotNetCommand -Arguments @(
            'tool', 'run', 'dotnet-coverage', '--', 'collect', $uiExecutable
            '-f', 'xml', '-o', $uiCoveragePath
        )
        if (-not (Test-Path -LiteralPath $uiReportPath) -or
            -not (Test-Path -LiteralPath $uiCoveragePath) -or
            (Get-Content -LiteralPath $uiReportPath -Raw | ConvertFrom-Json).passed -ne $true) {
            throw 'WinUI-testien hyväksytty tulos tai kattavuusraportti puuttuu. Analyysia ei lähetetty.'
        }
    }
    finally {
        $env:UPKEEP_UI_TEST_REPORT = $previousUiReport
    }

    if (-not (Test-Path -LiteralPath $coveragePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw 'Kattavuus- tai testiraportti puuttuu. Analyysia ei lähetetty.'
    }
    [xml]$coverage = Get-Content -LiteralPath $coveragePath -Raw
    if ($coverage.DocumentElement.Name -ne 'results' -or
        $null -eq $coverage.SelectSingleNode('/results/modules/module')) {
        throw 'Virheellinen VS Coverage XML -raportti. Analyysia ei lähetetty.'
    }
    [xml]$testResults = Get-Content -LiteralPath $trxPath -Raw
    $counters = $testResults.TestRun.ResultSummary.Counters
    if ($null -eq $counters -or [int]$counters.executed -le 0 -or
        [int]$counters.failed -ne 0 -or $testResults.TestRun.ResultSummary.outcome -ne 'Completed') {
        throw 'Testiajo ei onnistunut tai testejä ei ajettu. Analyysia ei lähetetty.'
    }
    Invoke-DotNetCommand -Arguments @(
        'tool', 'run', 'dotnet-sonarscanner', '--', 'end'
        "/d:sonar.token=$env:SONAR_TOKEN"
    )
}
finally {
    Pop-Location
}

Write-Output "Tulokset palvelimen käsittelyn jälkeen: $projectUrl"
