[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Solution = "TeklaMcp.sln",
    [string]$ServerProject = "src/TeklaMcp.Server/TeklaMcp.Server.csproj",
    # NuGet version of the installed Tekla (table in docs/releasing.md), e.g. 2023.0.1.
    # Empty = the project default (2021.0.0), which only talks to Tekla 2021.
    [string]$TeklaVersion = "",
    [switch]$ServerOnly,
    [switch]$SkipClean,
    [switch]$NoKill,
    [switch]$RunSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ">> dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Stop-TeklaMcpServerProcesses {
    $killedIds = New-Object System.Collections.Generic.HashSet[int]

    $serverProcesses = Get-Process -Name "TeklaMcp.Server" -ErrorAction SilentlyContinue
    foreach ($proc in $serverProcesses) {
        if ($killedIds.Add($proc.Id)) {
            Write-Host "Stopping TeklaMcp.Server (PID: $($proc.Id))" -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }

    $dotnetProcesses = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -eq "dotnet.exe" -and
            $null -ne $_.CommandLine -and
            $_.CommandLine -match "TeklaMcp\.Server"
        }

    foreach ($proc in $dotnetProcesses) {
        if ($killedIds.Add([int]$proc.ProcessId)) {
            Write-Host "Stopping dotnet host for TeklaMcp.Server (PID: $($proc.ProcessId))" -ForegroundColor Yellow
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    if ($killedIds.Count -eq 0) {
        Write-Host "No running TeklaMcp.Server processes found." -ForegroundColor DarkGray
    }
}

if (-not (Test-Path -LiteralPath $Solution)) {
    throw "Solution not found: $Solution"
}

if (-not (Test-Path -LiteralPath $ServerProject)) {
    throw "Server project not found: $ServerProject"
}

if (-not $NoKill) {
    Stop-TeklaMcpServerProcesses
}
else {
    Write-Host "Skipping process shutdown (-NoKill)." -ForegroundColor DarkGray
}

if (-not $SkipClean) {
    Invoke-Dotnet -Arguments @("clean", $Solution, "-c", $Configuration)
}
else {
    Write-Host "Skipping clean step (-SkipClean)." -ForegroundColor DarkGray
}

$versionArgs = @()
if ($TeklaVersion) {
    $versionArgs = @("-p:TeklaVersion=$TeklaVersion")
}
elseif ($env:TeklaVersion) {
    Write-Host "Using TeklaVersion from the environment: $($env:TeklaVersion)" -ForegroundColor DarkGray
}
else {
    Write-Host "No -TeklaVersion given: building for the default Tekla 2021 (2021.0.0)." -ForegroundColor Yellow
}

if ($ServerOnly) {
    Invoke-Dotnet -Arguments (@("build", $ServerProject, "-c", $Configuration) + $versionArgs)
}
else {
    Invoke-Dotnet -Arguments (@("build", $Solution, "-c", $Configuration) + $versionArgs)
}

if ($RunSmokeTest) {
    Write-Host ">> python scripts/mcp_smoke_test.py" -ForegroundColor Cyan
    & python "scripts/mcp_smoke_test.py"
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke test failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Build flow completed successfully." -ForegroundColor Green
