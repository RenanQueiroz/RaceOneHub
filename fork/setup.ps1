<#
.SYNOPSIS
Sets up or refreshes this fork's development runtime, the Hub and its desktop shortcut.

.DESCRIPTION
Idempotent; run it after cloning and after every update (fork\update.ps1 runs it).
  1. Python: .venv with requirements.txt (reinstalled only when requirements.txt changes).
  2. Release components: the pinned v0.1.0 RaceOneHub.exe is downloaded, checked and
     unpacked once into runtime\release-0.1.0. Its prepared host and engine files fill
     runtime\pc. The original Azimuth 0.9.9 is not needed (docs/BUILDING.md).
  3. Includes: the four game-derived includes, regenerated when their inputs change.
  4. Bridge and kernelx.dll, always built from src\ (a few seconds).
  5. The Hub, published to launcher\app when its sources change, and the
     "Forza Motorsport 5" desktop shortcut to it.
Everything it writes is ignored by Git. Logs go to fork\logs.
#>
[CmdletBinding()]
param(
    # Extracted Forza Motorsport 5 1.0.0.20 (the folder with Mount and EmbeddedXvd).
    # Remembered in fork\local.json.
    [string]$GameFolder,
    # Leave launcher\app alone: the Hub passes this while it is running.
    [switch]$SkipHub,
    # Only publish the Hub (and the shortcut); the Hub restarts itself this way.
    [switch]$HubOnly,
    # Wait for this process (the running Hub) to exit before publishing the Hub.
    [int]$WaitForPid,
    # Start the Hub when done.
    [switch]$StartHub,
    [switch]$NoShortcut,
    # Machine-readable progress for the Hub: one JSON object, replaced at each step.
    [string]$ProgressFile
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 2.0

$Root = Split-Path -Parent $PSScriptRoot
$ReleaseUrl = 'https://github.com/LoZazaMastro/RaceOneHub/releases/download/v0.1.0/RaceOneHub.exe'
$ReleaseSha256 = '938986dd7e14870cf92a21d33ab18107ab2440e1ad3353421d3880603f25c081'
$PayloadHash = 'cb1e3b41e5a734d9cbea1d57f94f54565c9819bf237c07cdd11fa38368efaed1'
$HostSha256 = '9607b17349f60f040c582652d991d6556cb6ec7843c24bf1f71fd1fdf587abd0'
$GameExeSha256 = '94dad21851d335e9a33231dc2a1de2ecd4b93eefdf5f6772085bf6886bf47db7'
$Includes = 'fm5_language_guards.inc', 'fm5_resolve_original_guard.inc', 'fm5_resolve_four_samples.inc', 'fm5_resolve_eight_samples.inc'

$LogDirectory = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force $LogDirectory | Out-Null
$Log = Join-Path $LogDirectory ('setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Get-ChildItem $LogDirectory -Filter 'setup-*.log' | Sort-Object Name -Descending | Select-Object -Skip 20 | Remove-Item -Force

$Steps = @(if ($HubOnly) { 'hub' } elseif ($SkipHub) { 'python', 'release', 'includes', 'bridge' } else { 'python', 'release', 'includes', 'bridge', 'hub' })
$HubChanged = $false

function Write-Log([string]$Text) {
    $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $Text
    Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
    Write-Host $Text
}

function Write-Result([hashtable]$Value) {
    if (-not $ProgressFile) { return }
    $json = ConvertTo-Json $Value -Compress
    $temporary = $ProgressFile + '.tmp'
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $ProgressFile -Force
}

function Step([string]$Name, [string]$Message) {
    Write-Log "== $Message"
    Write-Result @{ step = $Name; message = $Message; index = [array]::IndexOf($Steps, $Name) + 1; total = $Steps.Count; done = $false }
}

# Runs a native program, its output going to the log; throws on a non-zero exit.
function Invoke-Native([string]$Program, [string[]]$Arguments, [string]$What) {
    Add-Content -LiteralPath $Log -Value ('> ' + $Program + ' ' + ($Arguments -join ' ')) -Encoding UTF8
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Program @Arguments 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $Log -Encoding UTF8 }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE). See $Log" }
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# One digest over files (relative path and content), for "did the inputs change" stamps.
function Get-TreeStamp([string[]]$Paths) {
    $lines = foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                Sort-Object FullName |
                ForEach-Object { $_.FullName.Substring($Root.Length) + ':' + (Get-Sha256 $_.FullName) }
        } elseif (Test-Path -LiteralPath $path) { $path.Substring($Root.Length) + ':' + (Get-Sha256 $path) }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

function Read-Stamp([string]$Path) { if (Test-Path -LiteralPath $Path) { (Get-Content -LiteralPath $Path -Raw).Trim() } else { '' } }
function Write-Stamp([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value + "`n") }

function Find-Msvc {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 with the C++ x64 tools is required (vswhere.exe not found).' }
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) { throw 'Visual Studio 2022 with the C++ x64 tools (MSVC) is required.' }
    $version = (Get-Content -LiteralPath (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
    $directory = Join-Path $installation "VC\Tools\MSVC\$version"
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'bin\Hostx64\x64\cl.exe'))) { throw "cl.exe not found under $directory" }
    if ($version -ne '14.44.35207') { Write-Log "Note: MSVC $version; upstream verified 14.44.35207." }
    return $directory
}

function Find-WindowsSdk {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $preferred = '10.0.26100.0'
    if (Test-Path -LiteralPath (Join-Path $kits "Include\$preferred\um\windows.h")) { return @($kits, $preferred) }
    $latest = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\windows.h') } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $latest) { throw 'A Windows 10/11 SDK is required.' }
    Write-Log "Note: Windows SDK $($latest.Name); upstream verified $preferred."
    return @($kits, $latest.Name)
}

function Wait-ForHub {
    if ($WaitForPid) {
        $process = Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue
        if ($process) { Write-Log "Waiting for process $WaitForPid to exit"; $process.WaitForExit(60000) | Out-Null }
    }
    $app = Join-Path $Root 'launcher\app\RaceOneHub.exe'
    $running = Get-Process -Name RaceOneHub -ErrorAction SilentlyContinue | Where-Object {
        try { [string]::Equals($_.MainModule.FileName, $app, [StringComparison]::OrdinalIgnoreCase) } catch { $false } }
    if ($running) { throw 'Close Race One Hub before updating it (or run setup with -SkipHub).' }
}

try {
    Write-Log "Race One Hub fork setup in $Root"

    # Local settings: the game folder.
    $LocalFile = Join-Path $PSScriptRoot 'local.json'
    $Local = if (Test-Path -LiteralPath $LocalFile) { Get-Content -LiteralPath $LocalFile -Raw | ConvertFrom-Json } else { New-Object psobject }
    if (-not $GameFolder) { $GameFolder = if ($Local.PSObject.Properties['gameFolder']) { $Local.gameFolder } else { Join-Path (Split-Path -Parent $Root) 'Forza Motorsport 5\Forza_1.0.0.20_x64__8wekyb3d8bbwe' } }
    $GameFolder = [IO.Path]::GetFullPath($GameFolder).TrimEnd('\')
    if ((Split-Path -Leaf $GameFolder) -ieq 'Mount') { $GameFolder = Split-Path -Parent $GameFolder }
    $GameExe = Join-Path $GameFolder 'Mount\forza_durango_release_final.exe'
    if (-not (Test-Path -LiteralPath $GameExe)) { throw "Forza Motorsport 5 not found in $GameFolder (pass -GameFolder: the folder with Mount and EmbeddedXvd)." }
    $Local | Add-Member -NotePropertyName gameFolder -NotePropertyValue $GameFolder -Force
    [IO.File]::WriteAllText($LocalFile, ($Local | ConvertTo-Json) + "`n")

    if (-not $HubOnly) {
        Step 'python' 'Python environment'
        $Python = Join-Path $Root '.venv\Scripts\python.exe'
        if (-not (Test-Path -LiteralPath $Python)) {
            $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
            if ($launcher) { Invoke-Native $launcher.Source @('-m', 'venv', (Join-Path $Root '.venv')) 'Creating .venv' }
            else { Invoke-Native 'python' @('-m', 'venv', (Join-Path $Root '.venv')) 'Creating .venv' }
        }
        $requirements = Join-Path $Root 'requirements.txt'
        $requirementsStamp = Join-Path $Root '.venv\.fork-requirements'
        $wanted = Get-Sha256 $requirements
        if ((Read-Stamp $requirementsStamp) -ne $wanted) {
            Invoke-Native $Python @('-m', 'pip', 'install', '--disable-pip-version-check', '-q', '-r', $requirements) 'Installing requirements'
            Write-Stamp $requirementsStamp $wanted
        } else { Write-Log 'requirements.txt unchanged' }

        Step 'release' 'Release components'
        $Release = Join-Path $Root 'runtime\release-0.1.0'
        if (-not (Test-Path -LiteralPath (Join-Path $Release 'runtime\pc\ForzaMastro5.exe'))) {
            $cache = Join-Path $PSScriptRoot 'cache'
            New-Item -ItemType Directory -Force $cache | Out-Null
            $exe = Join-Path $cache 'RaceOneHub-v0.1.0.exe'
            if (-not (Test-Path -LiteralPath $exe) -or (Get-Sha256 $exe) -ne $ReleaseSha256) {
                Write-Log "Downloading $ReleaseUrl"
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $ReleaseUrl -OutFile ($exe + '.part') -UseBasicParsing
                Move-Item -LiteralPath ($exe + '.part') -Destination $exe -Force
            }
            if ((Get-Sha256 $exe) -ne $ReleaseSha256) { throw 'The downloaded RaceOneHub.exe does not match the pinned v0.1.0 release.' }
            # The release unpacks its components on any start; --check-backend reads capabilities only.
            $report = Join-Path $cache 'check-backend.json'
            $process = Start-Process -FilePath $exe -ArgumentList @('--check-backend', ('"' + $report + '"')) -Wait -PassThru
            $components = Join-Path $env:LOCALAPPDATA "RaceOneHub\components\$PayloadHash"
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $components 'runtime\pc\ForzaMastro5.exe'))) { throw "The release did not unpack its components (see $report)." }
            $staging = $Release + '.unpacking'
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
            Copy-Item -LiteralPath $components -Destination $staging -Recurse
            if (Test-Path -LiteralPath $Release) { Remove-Item -LiteralPath $Release -Recurse -Force }
            Move-Item -LiteralPath $staging -Destination $Release
            Write-Log "Unpacked release components into $Release"
        }
        if ((Get-Sha256 (Join-Path $Release 'runtime\pc\ForzaMastro5.exe')) -ne $HostSha256) { throw 'runtime\release-0.1.0 does not hold the v0.1.0 prepared host; delete it and run setup again.' }
        $RuntimePc = Join-Path $Root 'runtime\pc'
        $hostPath = Join-Path $RuntimePc 'ForzaMastro5.exe'
        if (-not (Test-Path -LiteralPath $hostPath) -or (Get-Sha256 $hostPath) -ne $HostSha256) {
            # Host and engine files only; the bridge is built from source below and no
            # template manifest is used, so the backend copies the full bridge.
            New-Item -ItemType Directory -Force $RuntimePc | Out-Null
            Copy-Item -LiteralPath (Join-Path $Release 'runtime\pc\ForzaMastro5.exe') -Destination $RuntimePc -Force
            Copy-Item -LiteralPath (Join-Path $Release 'runtime\pc\data') -Destination $RuntimePc -Recurse -Force
            Write-Log 'Copied the prepared host and engine files into runtime\pc'
        }
        $capabilitiesPath = Join-Path $RuntimePc 'launcher-capabilities.json'
        $capabilities = Get-Content -LiteralPath (Join-Path $Release 'runtime\pc\launcher-capabilities.json') -Raw | ConvertFrom-Json
        $capabilities.bridge_template = $false
        [IO.File]::WriteAllText($capabilitiesPath, ($capabilities | ConvertTo-Json) + "`n")
        foreach ($stale in @((Join-Path $Root 'runtime\bridge-template.json'))) { if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force } }

        Step 'includes' 'Game-derived includes'
        $includeOutput = Join-Path $Root 'analysis\fm5-includes'
        $includeStamp = (Get-Sha256 (Join-Path $Root 'tools\generate_fm5_includes.py')) + ' ' + $HostSha256 + ' ' + $GameExeSha256
        $includesPresent = @($Includes | Where-Object { Test-Path -LiteralPath (Join-Path $Root "src\allocation-port\$_") }).Count -eq $Includes.Count
        if (-not $includesPresent -or (Read-Stamp (Join-Path $includeOutput '.fork-inputs')) -ne $includeStamp) {
            if (Test-Path -LiteralPath $includeOutput) { Remove-Item -LiteralPath $includeOutput -Recurse -Force }
            New-Item -ItemType Directory -Force (Split-Path -Parent $includeOutput) | Out-Null
            Invoke-Native $Python @((Join-Path $Root 'tools\generate_fm5_includes.py'), '--game-exe', $GameExe,
                '--prepared-host', $hostPath, '--output-dir', $includeOutput) 'Generating the includes'
            foreach ($name in $Includes) { Copy-Item -LiteralPath (Join-Path $includeOutput $name) -Destination (Join-Path $Root "src\allocation-port\$name") -Force }
            Write-Stamp (Join-Path $includeOutput '.fork-inputs') $includeStamp
        } else { Write-Log 'Includes up to date' }

        Step 'bridge' 'Bridge and kernelx.dll'
        $vc = Find-Msvc
        $sdk = Find-WindowsSdk
        Invoke-Native $Python @((Join-Path $Root 'tools\build_pc_bridge.py'), '--graphics-adapters', '--portable-xg',
            '--vc-dir', $vc, '--sdk-dir', $sdk[0], '--sdk-version', $sdk[1]) 'Building the bridge'
        # tools/build_xg_bridge.py also needs the private modern XG; only its kernelx.dll step is needed here.
        $xgBuild = Join-Path $Root 'analysis\xg-host-build'
        New-Item -ItemType Directory -Force $xgBuild | Out-Null
        $include = @("/I$vc\include") + (@('ucrt', 'shared', 'um') | ForEach-Object { "/I$($sdk[0])\Include\$($sdk[1])\$_" })
        $libraries = @("/LIBPATH:$vc\lib\x64") + (@('ucrt', 'um') | ForEach-Object { "/LIBPATH:$($sdk[0])\Lib\$($sdk[1])\$_\x64" })
        Invoke-Native (Join-Path $vc 'bin\Hostx64\x64\cl.exe') (@('/nologo', '/W4', '/WX', '/O2', '/MT', '/LD') + $include +
            @((Join-Path $Root 'src\fm5_xmem.c'), "/Fo$xgBuild\fm5_xmem.obj", "/Fe$xgBuild\kernelx.dll", '/link', '/Brepro',
              "/DEF:$(Join-Path $Root 'src\kernelx-forwarders.def')") + $libraries + @('kernel32.lib')) 'Building kernelx.dll'
        New-Item -ItemType Directory -Force (Join-Path $Root 'runtime\xg-host') | Out-Null
        Copy-Item -LiteralPath (Join-Path $xgBuild 'kernelx.dll') -Destination (Join-Path $Root 'runtime\xg-host\kernelx.dll') -Force
        Write-Log ('Bridge ' + (Get-Sha256 (Join-Path $RuntimePc 'ForzaMastro5Bridge.dll')) + ', kernelx.dll ' + (Get-Sha256 (Join-Path $Root 'runtime\xg-host\kernelx.dll')))

        # Tools that default to ROOT/game find the dump through this junction.
        $junction = Join-Path $Root 'game\Forza_1.0.0.20_x64__8wekyb3d8bbwe'
        if (-not (Test-Path -LiteralPath $junction)) {
            New-Item -ItemType Directory -Force (Split-Path -Parent $junction) | Out-Null
            New-Item -ItemType Junction -Path $junction -Target $GameFolder | Out-Null
        }
        # First run: point the Hub at the game so PLAY works straight away.
        $hubState = Join-Path $Root 'launcher\data\builder-state.json'
        if (-not (Test-Path -LiteralPath $hubState)) {
            New-Item -ItemType Directory -Force (Split-Path -Parent $hubState) | Out-Null
            $state = [ordered]@{ outputDirectory = (Join-Path $Root 'builds\RaceOne5'); gameFolder = $GameFolder; createDesktopShortcut = $false }
            [IO.File]::WriteAllText($hubState, ($state | ConvertTo-Json) + "`n")
        }
    }

    $HubSources = Join-Path $Root 'launcher\ForzaMastro.Launcher'
    $App = Join-Path $Root 'launcher\app'
    $hubStamp = Get-TreeStamp @($HubSources)
    $HubChanged = (Read-Stamp (Join-Path $App '.fork-source')) -ne $hubStamp -or -not (Test-Path -LiteralPath (Join-Path $App 'RaceOneHub.exe'))
    if ($Steps -contains 'hub') {
        Step 'hub' 'Race One Hub'
        if ($HubChanged) {
            Wait-ForHub
            $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
            if (-not $dotnet) { throw 'The .NET SDK (8 or later) is required to build the Hub.' }
            if (Test-Path -LiteralPath $App) { Remove-Item -LiteralPath $App -Recurse -Force }
            # RollForward lets the net8.0 Hub run on a newer .NET desktop runtime when 8 is absent.
            Invoke-Native $dotnet.Source @('publish', (Join-Path $HubSources 'ForzaMastro.Launcher.csproj'), '-c', 'Release',
                '-o', $App, '-p:RollForward=Major', '--nologo', '-v', 'q') 'Publishing the Hub'
            Write-Stamp (Join-Path $App '.fork-source') $hubStamp
            $HubChanged = $false
        } else { Write-Log 'Hub up to date' }
        if (-not $NoShortcut) {
            $link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Forza Motorsport 5.lnk'
            $shell = New-Object -ComObject WScript.Shell
            try {
                $shortcut = $shell.CreateShortcut($link)
                $shortcut.TargetPath = Join-Path $App 'RaceOneHub.exe'
                $shortcut.WorkingDirectory = $App
                $shortcut.IconLocation = (Join-Path $HubSources 'Assets\ForzaMotorsport5.ico') + ',0'
                $shortcut.Description = 'Race One Hub (Forza Motorsport 5), from ' + $Root
                $shortcut.Save()
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
            Write-Log "Shortcut: $link"
        }
    }
    Write-Result @{ done = $true; ok = $true; hub_changed = [bool]$HubChanged; log = $Log }
    Write-Log 'Done.'
    if ($StartHub) { Start-Process -FilePath (Join-Path $App 'RaceOneHub.exe') -WorkingDirectory $App }
    exit 0
} catch {
    $message = $_.Exception.Message
    Add-Content -LiteralPath $Log -Value ('ERROR ' + $message + "`n" + $_.ScriptStackTrace) -Encoding UTF8
    Write-Result @{ done = $true; ok = $false; error = $message; log = $Log }
    Write-Host "Setup failed: $message" -ForegroundColor Red
    if ($StartHub -and (Test-Path -LiteralPath (Join-Path $Root 'launcher\app\RaceOneHub.exe'))) { Start-Process -FilePath (Join-Path $Root 'launcher\app\RaceOneHub.exe') -WorkingDirectory (Join-Path $Root 'launcher\app') }
    exit 1
}
