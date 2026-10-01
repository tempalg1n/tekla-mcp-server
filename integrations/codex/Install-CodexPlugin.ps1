#Requires -Version 5.1
<#
.SYNOPSIS
    Adds the "Tekla MCP" plugin card to Codex: one MCP server per installed Tekla version.

.DESCRIPTION
    Codex starts the MCP servers listed in config.toml, but only plugins get a card in its Plugins
    directory (issue #18). This script builds that card for THIS computer - nothing machine-specific
    ships with the release:

      1. finds the installed Tekla versions in the registry and their Open API folders;
      2. finds unpacked TeklaMcp.Server builds and reads which Tekla year each one was built for;
      3. writes a local Codex marketplace (default %LOCALAPPDATA%\TeklaMcp\codex-marketplace) with the
         plugin "tekla-mcp": one stdio server "tekla<year>" per Tekla version, started with that
         version's TEKLA_BIN_DIR and an explicit TEKLA_MCP_USE_MOCK;
      4. registers and installs it with the Codex CLI (codex plugin marketplace add, codex plugin add);
      5. reports MCP servers with the same names defined elsewhere. A direct [mcp_servers.tekla2026]
         table in config.toml REPLACES the card's server, even with enabled = false, and when two
         plugins declare one name only one of them is started (checked with Codex CLI 0.153.4).
         -RemoveConflicts removes those definitions through the Codex CLI.

    Run it again after unpacking a new release: it regenerates the card and reinstalls it.
    Afterwards quit the Codex app completely, start it again and open a new thread.

.PARAMETER Years
    Tekla years to register, e.g. 2021,2026. Default: every year that has both an installed Tekla and
    an unpacked server build.

.PARAMETER Server
    TeklaMcp.Server.exe paths (or their folders), several separated by ';'. The Tekla year is read
    from each build. These win over builds found by scanning.

.PARAMETER ServerRoot
    Folders whose sub-folders hold unpacked builds, several separated by ';'. Default: the release
    folder this script came with and the folder around it - unpack the zips for all your Tekla
    versions side by side.

.PARAMETER FileRoot
    TEKLA_MCP_FILE_ROOT for every server (';'-separated folders the file-exchange tools may read and
    write). Default: unset, so the server's own defaults apply.

.PARAMETER UseMock
    Start the servers with TEKLA_MCP_USE_MOCK=1: the mock backend, no Tekla needed.

.PARAMETER MarketplaceDir
    Where the local marketplace is written. Default: %LOCALAPPDATA%\TeklaMcp\codex-marketplace.

.PARAMETER CodexPath
    The codex executable to use. Default: codex on PATH, then the Codex app's own CLI.

.PARAMETER NoInstall
    Only write the marketplace, and print the Codex commands instead of running them.

.PARAMETER RemoveConflicts
    Remove same-name MCP servers defined elsewhere (codex mcp remove / codex plugin remove).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\codex\Install-CodexPlugin.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\codex\Install-CodexPlugin.ps1 -Years 2021,2026 -FileRoot D:\tekla-exchange
#>
[CmdletBinding()]
param(
    # Strings, parsed below: "powershell -File ... -Years 2021,2026" passes ONE string, which an
    # [int[]] parameter turned into 20212026.
    [string[]] $Years,
    [string[]] $Server,
    [string[]] $ServerRoot,
    [string] $FileRoot,
    [switch] $UseMock,
    [string] $MarketplaceDir = (Join-Path $env:LOCALAPPDATA 'TeklaMcp\codex-marketplace'),
    [string] $CodexPath,
    [switch] $NoInstall,
    [switch] $RemoveConflicts
)

$ErrorActionPreference = 'Stop'

$MarketplaceName = 'tekla-mcp-local'
$PluginName = 'tekla-mcp'
$Selector = "$PluginName@$MarketplaceName"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:Codex = $null

function Write-Note([string] $text) { Write-Host "  $text" }

function Write-Utf8([string] $path, [string] $text) {
    # Codex parses these files strictly: UTF-8 without a byte-order mark.
    [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom)
}

# "-Years 2021,2026" arrives as one string with -File and as two with -Command; accept both.
function ConvertTo-YearList([string[]] $values) {
    $years = @()
    foreach ($part in @($values | ForEach-Object { "$_" -split '[,;\s]+' } | Where-Object { $_ })) {
        $year = 0
        if (-not [int]::TryParse($part, [ref]$year) -or $year -lt 2000 -or $year -gt 2999) { throw "Not a Tekla year: '$part'." }
        $years += $year
    }
    return @($years | Sort-Object -Unique)
}

function ConvertTo-PathList([string[]] $values) {
    return @($values | ForEach-Object { "$_" -split ';' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
}

function Get-PropertyText($object, [string] $name) {
    if ($null -eq $object) { return '' }
    $property = $object.PSObject.Properties[$name]
    if ($property -and $null -ne $property.Value) { return [string]$property.Value }
    return ''
}

# Runs a native command without letting Windows PowerShell 5.1 turn its stderr into a terminating error.
function Invoke-Native([string] $file, [string[]] $arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $file @arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# --- Installed Tekla versions ---------------------------------------------------------------------

# The Open API folder is the one holding Tekla.Structures.Model.dll: bin on 2023+, nt\bin\plugins on
# 2021 (same candidates as TeklaBinLayout in the server). On Tekla 2026 the server also searches its
# Net48Runtime subfolder by itself, so TEKLA_BIN_DIR stays the API folder.
function Find-ApiDirectory([string] $root) {
    $candidates = @($root, (Join-Path $root 'plugins'), (Join-Path $root 'bin'), (Join-Path $root 'bin\plugins'),
                    (Join-Path $root 'nt\bin\plugins'), (Join-Path $root 'nt\bin'))
    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $dir 'Tekla.Structures.Model.dll')) { return $dir }
    }
    return $null
}

# Installed Teklas register under SOFTWARE\Trimble\Tekla Structures\<version>\setup (MainDir +
# TSVersionDir). Returns year -> Open API folder; the year comes from the Model DLL itself.
function Get-TeklaInstalls {
    $installs = @{}
    foreach ($hive in 'HKLM:', 'HKCU:') {
        $key = "$hive\SOFTWARE\Trimble\Tekla Structures"
        if (-not (Test-Path $key)) { continue }
        foreach ($versionKey in @(Get-ChildItem $key -ErrorAction SilentlyContinue)) {
            $setup = Join-Path $versionKey.PSPath 'setup'
            if (-not (Test-Path $setup)) { continue }
            $values = Get-ItemProperty $setup -ErrorAction SilentlyContinue
            $mainDir = (Get-PropertyText $values 'MainDir').Trim().Trim('"')
            if (-not $mainDir) { continue }
            $versionDir = (Get-PropertyText $values 'TSVersionDir').Trim()
            if (-not $versionDir) { $versionDir = $versionKey.PSChildName }
            $api = Find-ApiDirectory (Join-Path $mainDir $versionDir)
            if (-not $api) { continue }
            try {
                $year = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $api 'Tekla.Structures.Model.dll')).Version.Major
            }
            catch { continue }
            if (-not $installs.ContainsKey($year)) { $installs[$year] = $api }
        }
    }
    return $installs
}

# --- Unpacked server builds -----------------------------------------------------------------------

# The Tekla year a build was compiled for: TeklaMcp.Tekla.dll references exactly one
# Tekla.Structures.Model version. Read in a child process - one PowerShell process cannot
# reflection-load two TeklaMcp.Tekla.dll files of different builds.
function Get-BuildYear([string] $exe) {
    $dll = Join-Path (Split-Path -Parent $exe) 'TeklaMcp.Tekla.dll'
    if (-not (Test-Path -LiteralPath $dll)) { return $null } # the net8.0 mock build has no Tekla backend
    $probe = "[Reflection.Assembly]::ReflectionOnlyLoadFrom('" + $dll.Replace("'", "''") + "').GetReferencedAssemblies() | " +
             "Where-Object { `$_.Name -eq 'Tekla.Structures.Model' } | ForEach-Object { `$_.Version.Major }"
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($probe))
    $result = Invoke-Native (Join-Path $PSHOME 'powershell.exe') @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
    $year = 0
    foreach ($line in $result.Output) {
        if ([int]::TryParse($line.Trim(), [ref]$year) -and $year -ge 2000) { return $year }
    }
    # Fallback: the release folder name, TeklaMcp.Server-vX.Y.Z-tekla2026.
    if ((Split-Path -Leaf (Split-Path -Parent $exe)) -match 'tekla(\d{4})$') { return [int]$Matches[1] }
    return $null
}

# "0.8.1" from the ProductVersion "0.8.1+<commit>"; strict semver, as Codex requires.
function Get-BuildVersion([string] $exe) {
    $product = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $base = ("$product" -split '\+')[0].Trim()
    if ($base -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { return $base }
    return '0.0.0'
}

function Get-VersionKey([string] $semver) { return [version](($semver -split '-')[0]) }

function Get-DefaultServerRoots {
    $roots = @()
    $releaseDir = Split-Path -Parent $PSScriptRoot
    if ($releaseDir -and (Test-Path -LiteralPath (Join-Path $releaseDir 'TeklaMcp.Server.exe'))) {
        $roots += $releaseDir
        $parent = Split-Path -Parent $releaseDir
        if ($parent) { $roots += $parent }
    }
    return $roots
}

# Returns year -> build. Explicit -Server builds win; otherwise the highest version per year.
function Find-ServerBuilds([string[]] $explicit, [string[]] $roots) {
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($item in @($explicit | Where-Object { $_ })) {
        $path = $item
        if (Test-Path -LiteralPath $path -PathType Container) { $path = Join-Path $path 'TeklaMcp.Server.exe' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "No TeklaMcp.Server.exe at '$item'." }
        $found.Add([pscustomobject]@{ Exe = (Resolve-Path -LiteralPath $path).ProviderPath; Explicit = $true })
    }
    foreach ($root in @($roots | Where-Object { $_ })) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { Write-Note "no such folder: $root"; continue }
        $folders = @(Get-Item -LiteralPath $root) + @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)
        foreach ($folder in $folders) {
            $exe = Join-Path $folder.FullName 'TeklaMcp.Server.exe'
            if (Test-Path -LiteralPath $exe) { $found.Add([pscustomobject]@{ Exe = $exe; Explicit = $false }) }
        }
    }

    $builds = @{}
    $seen = @{}
    foreach ($candidate in $found) {
        if ($seen.ContainsKey($candidate.Exe.ToLowerInvariant())) { continue }
        $seen[$candidate.Exe.ToLowerInvariant()] = $true
        $year = Get-BuildYear $candidate.Exe
        if (-not $year) { Write-Note "skipped $($candidate.Exe): not a Tekla build (the net8.0 mock zip?)"; continue }
        $build = [pscustomobject]@{ Year = $year; Exe = $candidate.Exe; Version = (Get-BuildVersion $candidate.Exe); Explicit = $candidate.Explicit }
        if (-not $builds.ContainsKey($year)) { $builds[$year] = $build; continue }
        $current = $builds[$year]
        if ($current.Explicit -and -not $build.Explicit) { continue }
        if (($build.Explicit -and -not $current.Explicit) -or (Get-VersionKey $build.Version) -gt (Get-VersionKey $current.Version)) {
            $builds[$year] = $build
        }
    }
    return $builds
}

# --- Codex -------------------------------------------------------------------------------------

function Find-Codex {
    if ($CodexPath) {
        if (-not (Test-Path -LiteralPath $CodexPath)) { throw "No codex executable at '$CodexPath'." }
        return $CodexPath
    }
    $onPath = Get-Command codex -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    # The Codex app keeps its CLI in %LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe.
    $appBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $appBin) {
        $app = Get-ChildItem -LiteralPath $appBin -Filter codex.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($app) { return $app.FullName }
    }
    return $null
}

function Invoke-Codex([string[]] $arguments) {
    $result = Invoke-Native $script:Codex $arguments
    if ($result.ExitCode -ne 0) {
        $result.Output | ForEach-Object { Write-Note $_ }
        throw "codex $($arguments -join ' ') failed (exit code $($result.ExitCode))."
    }
    return $result.Output
}

function Get-CodexHome {
    if ($env:CODEX_HOME) { return $env:CODEX_HOME }
    return (Join-Path $env:USERPROFILE '.codex')
}

# Direct [mcp_servers.<name>] tables in config.toml.
function Get-DirectServerNames([string] $configPath) {
    $names = @()
    if (-not (Test-Path -LiteralPath $configPath)) { return $names }
    foreach ($line in Get-Content -LiteralPath $configPath -Encoding UTF8) {
        if ($line -match '^\s*\[\s*mcp_servers\s*\.\s*(?:"([^"]+)"|''([^'']+)''|([A-Za-z0-9_-]+))\s*\]') {
            $names += @($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ } | Select-Object -First 1
        }
    }
    return $names
}

# MCP server names of the installed plugins: <CODEX_HOME>\plugins\cache\<marketplace>\<plugin>\<version>.
function Get-PluginServers([string] $codexHome) {
    $servers = @()
    $cache = Join-Path $codexHome 'plugins\cache'
    if (-not (Test-Path -LiteralPath $cache)) { return $servers }
    foreach ($marketplace in @(Get-ChildItem -LiteralPath $cache -Directory -ErrorAction SilentlyContinue)) {
        foreach ($plugin in @(Get-ChildItem -LiteralPath $marketplace.FullName -Directory -ErrorAction SilentlyContinue)) {
            foreach ($version in @(Get-ChildItem -LiteralPath $plugin.FullName -Directory -ErrorAction SilentlyContinue)) {
                $declared = @()
                foreach ($file in @('.mcp.json', '.codex-plugin\plugin.json')) {
                    $path = Join-Path $version.FullName $file
                    if (-not (Test-Path -LiteralPath $path)) { continue }
                    try {
                        $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
                        $block = $json.PSObject.Properties['mcpServers']
                        if ($block -and $block.Value -is [System.Management.Automation.PSCustomObject]) {
                            $declared += @($block.Value.PSObject.Properties | ForEach-Object { $_.Name })
                        }
                    }
                    catch { }
                }
                foreach ($name in $declared) {
                    $servers += [pscustomobject]@{ Selector = "$($plugin.Name)@$($marketplace.Name)"; Server = $name }
                }
            }
        }
    }
    return $servers
}

# --- Main --------------------------------------------------------------------------------------

Write-Host 'Tekla MCP plugin card for Codex'

$requestedYears = ConvertTo-YearList $Years
$Server = ConvertTo-PathList $Server
$ServerRoot = ConvertTo-PathList $ServerRoot
if ($Server.Count -eq 0 -and $ServerRoot.Count -eq 0) { $ServerRoot = Get-DefaultServerRoots }
$installs = Get-TeklaInstalls
$builds = Find-ServerBuilds $Server $ServerRoot
if ($builds.Count -eq 0) {
    throw ('No TeklaMcp.Server build found. Run this script from the codex folder of an unpacked release ' +
           'zip (TeklaMcp.Server-vX.Y.Z-tekla<year>.zip), or pass -Server <path to TeklaMcp.Server.exe>.')
}

if ($requestedYears.Count -gt 0) {
    $selected = $requestedYears
}
else {
    $selected = @($builds.Keys | Where-Object { $installs.ContainsKey($_) } | Sort-Object)
    if ($selected.Count -eq 0) {
        if ($installs.Count -eq 0) {
            Write-Warning 'No Tekla installation found in the registry: registering every server build found.'
            $selected = @($builds.Keys | Sort-Object)
        }
        else {
            throw ("No server build matches an installed Tekla. Builds: Tekla $((@($builds.Keys | Sort-Object)) -join ', '); " +
                   "installed: Tekla $((@($installs.Keys | Sort-Object)) -join ', '). Unpack the zip for your Tekla, or pass -Years.")
        }
    }
}
foreach ($year in $selected) {
    if (-not $builds.ContainsKey($year)) {
        throw "No server build for Tekla $year. Unpack TeklaMcp.Server-vX.Y.Z-tekla$year.zip next to this one, or pass -Server."
    }
}

Write-Host ''
Write-Host 'Servers on the card:'
foreach ($year in $selected) {
    $build = $builds[$year]
    $api = '(not found in the registry: the server looks for the running Tekla itself)'
    if ($installs.ContainsKey($year)) { $api = $installs[$year] }
    Write-Note ("tekla$year  server {0} ({1})" -f $build.Exe, $build.Version)
    Write-Note ("           Tekla {0} Open API: {1}" -f $year, $api)
}
foreach ($year in @($builds.Keys | Sort-Object)) {
    if ($selected -notcontains $year) { Write-Note "not added: Tekla $year build ($($builds[$year].Exe)) - Tekla $year is not installed; pass -Years to add it." }
}

# --- Write the marketplace ------------------------------------------------------------------------

$serverNames = @($selected | ForEach-Object { "tekla$_" })
$mcpServers = [ordered]@{}
foreach ($year in $selected) {
    $build = $builds[$year]
    $environment = [ordered]@{}
    $environment['TEKLA_MCP_USE_MOCK'] = if ($UseMock) { '1' } else { '0' }
    if ($installs.ContainsKey($year)) { $environment['TEKLA_BIN_DIR'] = $installs[$year] }
    if ($FileRoot) { $environment['TEKLA_MCP_FILE_ROOT'] = $FileRoot }
    $mcpServers["tekla$year"] = [ordered]@{
        command             = $build.Exe
        args                = @()
        cwd                 = (Split-Path -Parent $build.Exe)
        env                 = $environment
        startup_timeout_sec = 30
    }
}

$marketplaceFile = Join-Path $MarketplaceDir '.agents\plugins\marketplace.json'
$pluginRoot = Join-Path $MarketplaceDir "plugins\$PluginName"
if (Test-Path -LiteralPath $marketplaceFile) {
    $existingName = ''
    try { $existingName = Get-PropertyText (Get-Content -LiteralPath $marketplaceFile -Raw | ConvertFrom-Json) 'name' } catch { }
    if ($existingName -ne $MarketplaceName) {
        throw "'$MarketplaceDir' already holds another Codex marketplace ('$existingName'). Pass another -MarketplaceDir."
    }
}
if (Test-Path -LiteralPath $pluginRoot) {
    # Only ever replace a folder this script generated.
    $manifest = Join-Path $pluginRoot '.codex-plugin\plugin.json'
    $ours = $false
    if (Test-Path -LiteralPath $manifest) {
        try { $ours = (Get-PropertyText (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json) 'name') -eq $PluginName } catch { }
    }
    if (-not $ours) { throw "'$pluginRoot' exists but was not generated by this script; leaving it alone." }
    Remove-Item -LiteralPath $pluginRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $pluginRoot '.codex-plugin'), (Join-Path $pluginRoot 'assets'),
    (Split-Path -Parent $marketplaceFile) | Out-Null

# The version carries a cachebuster: Codex caches installed plugins by version, so a regenerated card
# needs a new one to be reinstalled.
$version = @($selected | ForEach-Object { $builds[$_].Version } | Sort-Object { Get-VersionKey $_ } -Descending)[0]
$manifestJson = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestJson.version = "$version+codex.local-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$serverList = ($selected | ForEach-Object { "tekla$_ (Tekla $_)" }) -join ', '
$manifestJson.interface.longDescription = "$($manifestJson.interface.longDescription) Servers on this computer: $serverList."
Write-Utf8 (Join-Path $pluginRoot '.codex-plugin\plugin.json') ($manifestJson | ConvertTo-Json -Depth 10)
Write-Utf8 (Join-Path $pluginRoot '.mcp.json') ([ordered]@{ mcpServers = $mcpServers } | ConvertTo-Json -Depth 10)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tekla-mcp.svg') -Destination (Join-Path $pluginRoot 'assets\tekla-mcp.svg') -Force

$marketplace = [ordered]@{
    name      = $MarketplaceName
    interface = [ordered]@{ displayName = 'Tekla MCP (this computer)' }
    plugins   = @(
        [ordered]@{
            name     = $PluginName
            source   = [ordered]@{ source = 'local'; path = "./plugins/$PluginName" }
            policy   = [ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
            category = 'Engineering'
        }
    )
}
Write-Utf8 $marketplaceFile ($marketplace | ConvertTo-Json -Depth 10)

Write-Host ''
Write-Host "Marketplace written: $MarketplaceDir"
Write-Note "plugin $Selector, version $($manifestJson.version)"

# --- Register with Codex --------------------------------------------------------------------------

$script:Codex = Find-Codex
$installed = $false
if ($NoInstall -or -not $script:Codex) {
    if (-not $NoInstall) { Write-Warning 'The Codex CLI was not found (not on PATH, no Codex app CLI). Pass -CodexPath, or run:' }
    else { Write-Host 'Install it with:' }
    Write-Note "codex plugin marketplace add `"$MarketplaceDir`""
    Write-Note "codex plugin add $Selector"
}
else {
    Write-Host ''
    Write-Host "Installing with $($script:Codex)"
    Invoke-Codex @('plugin', 'marketplace', 'add', $MarketplaceDir) | ForEach-Object { Write-Note $_ }
    Invoke-Codex @('plugin', 'add', $Selector) | ForEach-Object { Write-Note $_ }
    $installed = $true
}

# --- Same-name servers elsewhere ------------------------------------------------------------------

$codexHome = Get-CodexHome
$configPath = Join-Path $codexHome 'config.toml'
$direct = @(Get-DirectServerNames $configPath | Where-Object { $serverNames -contains $_ } | Sort-Object -Unique)
$foreign = @(Get-PluginServers $codexHome | Where-Object { $_.Selector -ne $Selector -and $serverNames -contains $_.Server })
if ($direct.Count -gt 0 -or $foreign.Count -gt 0) {
    Write-Host ''
    if ($RemoveConflicts -and $script:Codex) {
        Write-Host 'Removing other definitions of the same MCP server names:'
    }
    else {
        Write-Warning 'These define the same MCP server names as the card and are started instead of its servers:'
    }
    foreach ($name in $direct) { Write-Note "[mcp_servers.$name] in $configPath  ->  codex mcp remove $name" }
    foreach ($entry in @($foreign | Sort-Object Selector, Server)) {
        Write-Note "plugin $($entry.Selector) declares $($entry.Server)  ->  codex plugin remove $($entry.Selector)"
    }
    if ($RemoveConflicts -and $script:Codex) {
        foreach ($name in $direct) { Invoke-Codex @('mcp', 'remove', $name) | ForEach-Object { Write-Note $_ } }
        foreach ($pluginSelector in @($foreign | ForEach-Object { $_.Selector } | Sort-Object -Unique)) {
            Invoke-Codex @('plugin', 'remove', $pluginSelector) | ForEach-Object { Write-Note $_ }
        }
    }
    else {
        Write-Note 'Run the commands above, or this script again with -RemoveConflicts.'
    }
}

# --- What Codex will start ------------------------------------------------------------------------

if ($installed) {
    $resolved = @()
    try {
        # Assigned first: Windows PowerShell 5.1 emits a parsed JSON array as ONE pipeline object.
        $parsed = ((Invoke-Codex @('mcp', 'list', '--json')) -join "`n") | ConvertFrom-Json
        $resolved = @($parsed)
    }
    catch {
        Write-Note "codex mcp list --json could not be read: $($_.Exception.Message)"
    }
    if ($resolved.Count -gt 0) {
        Write-Host ''
        Write-Host 'Codex resolves these servers:'
        foreach ($name in $serverNames) {
            $entry = $resolved | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if (-not $entry) { Write-Note "$name  MISSING"; continue }
            $expected = $mcpServers[$name].command
            $actual = Get-PropertyText $entry.transport 'command'
            $origin = if ($actual -eq $expected) { 'from the card' } else { "NOT the card's server: $actual" }
            $state = if ($entry.enabled) { 'enabled' } else { 'switched off' }
            Write-Note "$name  $state, $origin"
        }
    }
}

Write-Host ''
Write-Host 'Next: quit the Codex app completely (also from the system tray), start it again and open a new'
Write-Host 'thread. The Plugins directory shows "Tekla MCP"; each tekla<year> server has its own switch.'
Write-Host 'Switch off the years you do not use, then ask: "Check the Tekla connection and summarize the model".'
