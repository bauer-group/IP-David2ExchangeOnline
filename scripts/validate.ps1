# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [switch]$SkipDavid,
    [int]$RealItems = 3
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$validationRoot = Join-Path $root '.validate'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "VALIDATION FAILED: $Message" }
}

Push-Location $root
try {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host '== build =='
    dotnet build David2Exchange.slnx -c Release
    Assert-True ($LASTEXITCODE -eq 0) 'dotnet build failed'

    Write-Host '== tests =='
    dotnet test tests\David2Exchange.Tests\David2Exchange.Tests.csproj -c Release --no-build
    Assert-True ($LASTEXITCODE -eq 0) 'tests failed'

    Write-Host '== mock probe =='
    $mockOut = Join-Path $validationRoot 'mock'
    dotnet run --project src\David2Exchange -c Release --no-build -- probe --mock --out $mockOut --max-items 10 --export-eml 3
    Assert-True ($LASTEXITCODE -eq 0) 'mock probe failed'

    $reportPath = Join-Path $mockOut 'probe-report.json'
    Assert-True (Test-Path $reportPath) 'probe-report.json missing'
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    Assert-True ($report.TotalItems -ge 1) 'mock probe read no items'
    Assert-True ($report.TotalEmlExported -ge 1) 'mock probe exported no EML'
    Assert-True ((Test-Path (Join-Path $mockOut 'items.jsonl'))) 'items.jsonl missing'
    Assert-True ((Get-ChildItem (Join-Path $mockOut 'eml') -Filter *.eml).Count -ge 1) 'no EML files'

    Write-Host "mock: items=$($report.TotalItems) eml=$($report.TotalEmlExported) archives=$($report.Archives.Count)"

    if (-not $SkipDavid -and (Test-Path 'Registry::HKEY_CLASSES_ROOT\DVOBJAPILib.DvISEAPI')) {
        Write-Host '== real David probe =='
        $realOut = Join-Path $validationRoot 'real'
        dotnet run --project src\David2Exchange -c Release --no-build -- probe `
            --out $realOut `
            --archive 'user\@me' `
            --type 2 `
            --max-items $RealItems `
            --export-eml 1

        if ($LASTEXITCODE -eq 0) {
            $realReport = Get-Content (Join-Path $realOut 'probe-report.json') -Raw | ConvertFrom-Json
            Write-Host "real: items=$($realReport.TotalItems) eml=$($realReport.TotalEmlExported)"
            Assert-True ($realReport.TotalItems -ge 1) 'real probe read no items'
        }
        else {
            Write-Warning "Real David probe exited with $LASTEXITCODE (permissions/session?). Mock validation passed."
        }

        Write-Host '== real discover/run/report =='
        $runRoot = Join-Path $validationRoot 'run'
        $runConfig = Join-Path $validationRoot 'migration.yaml'
        @'
david:
  server: local
  archiveRoot: "\\\\srv-bauer\\david\\archive"
  progId: DVOBJAPILib.DvISEAPI
  timezone: Europe/Berlin
  logon: AUTH
m365:
  tenantId: ""
  clientId: ""
  certThumbprint: ""
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  provenanceDomain: david.local
  ewsFolder: David-Migration
staging:
  root: run
  manifest: run/manifest.db
  logs: run/logs
limits:
  limit: 3
mailboxes:
  - id: user01
    source: { kind: user, archivePath: "user\\@me" }
    target: { upn: user01@example.com }
    include: [mail]
'@ | Set-Content -LiteralPath $runConfig -Encoding UTF8

        dotnet run --project src\David2Exchange -c Release --no-build -- discover --config $runConfig --limit 3
        Assert-True ($LASTEXITCODE -eq 0) 'real discover failed'

        dotnet run --project src\David2Exchange -c Release --no-build -- run --config $runConfig --limit 3 --output local
        Assert-True ($LASTEXITCODE -eq 0) 'real local run failed'

        dotnet run --project src\David2Exchange -c Release --no-build -- report --config $runConfig --out (Join-Path $runRoot 'report.html')
        Assert-True ($LASTEXITCODE -eq 0) 'report failed'
        Assert-True (Test-Path (Join-Path $runRoot 'manifest.db')) 'manifest.db missing'
        Assert-True (Test-Path (Join-Path $runRoot 'report.html')) 'report.html missing'
    }
    else {
        Write-Host 'real David probe skipped (no David ProgID or -SkipDavid)'
    }

    Write-Host 'VALIDATION PASSED'
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
}

