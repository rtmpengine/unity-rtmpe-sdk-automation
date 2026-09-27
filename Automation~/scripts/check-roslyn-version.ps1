#!/usr/bin/env pwsh
#
# Reads the Roslyn (csc) compiler version bundled in the Unity editor and checks
# it against the analyzer's pinned Microsoft.CodeAnalysis.CSharp version. The
# verdict is printed and appended to
# clients\unity-sdk\Tooling\COMPILER_COMPATIBILITY.md.
#
# Run on a machine with the FLOOR editor (Unity 2022.3 LTS) installed — that is
# the only host whose Roslyn can establish the pin. A newer editor passes for any
# pin at or below its own Roslyn and so proves nothing about the floor; the
# recorded verdict names the csc.dll it read so which editor answered is visible.
# Works in Windows PowerShell 5.1 (built in) and in PowerShell 7+ on any OS.
#
#   powershell -ExecutionPolicy Bypass -File scripts\check-roslyn-version.ps1
#   pwsh scripts/check-roslyn-version.ps1 -EditorPath "C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor"
#   pwsh scripts/check-roslyn-version.ps1 -CscPath "<path>\csc.dll"

param(
    [string]$EditorPath = $env:RTMPE_UNITY_EDITOR_PATH,
    [string]$CscPath,
    [string]$Pinned = $(if ($env:RTMPE_ANALYZER_PIN) { $env:RTMPE_ANALYZER_PIN } else { "4.3.0" })
)

$ErrorActionPreference = "Stop"

function Test-IsWindowsHost {
    if ($null -ne $IsWindows) { return $IsWindows }   # PowerShell 7+
    return ($env:OS -eq 'Windows_NT')                 # Windows PowerShell 5.1
}
function Test-IsMacHost {
    if ($null -ne $IsMacOS) { return $IsMacOS }
    return $false
}

# 1. Locate the bundled Roslyn csc.dll — explicit path, editor path, or OS defaults.
function Find-Csc {
    if ($CscPath -and (Test-Path $CscPath)) { return (Resolve-Path $CscPath).Path }

    $roots = @()
    if ($EditorPath) { $roots += $EditorPath }
    # Floor editor first. A wildcard root that matches nothing fails the
    # Test-Path below and falls through to the next candidate.
    if (Test-IsWindowsHost) {
        $roots += "C:\Program Files\Unity\Hub\Editor\2022.3.*\Editor"
        $roots += "C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor"
        $roots += "C:\Program Files\Unity\Hub\Editor"
    } elseif (Test-IsMacHost) {
        $roots += "/Applications/Unity/Hub/Editor/2022.3.*/Unity.app/Contents"
        $roots += "/Applications/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents"
        $roots += "/Applications/Unity/Hub/Editor"
    } else {
        $roots += "$HOME/Unity/Hub/Editor/2022.3.*/Editor"
        $roots += "$HOME/Unity/Hub/Editor/6000.3.13f1/Editor"
        $roots += "$HOME/Unity/Hub/Editor"
    }

    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem -Path $r -Recurse -Filter csc.dll -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match 'Roslyn' } |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$csc = Find-Csc
if (-not $csc) {
    Write-Error "Could not locate a Roslyn csc.dll. Pass -EditorPath or -CscPath explicitly."
    exit 3
}

# 2. Read the version from the PE resource (no execution); fall back to running csc.
$raw = $null
try {
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($csc)
    if ($vi.FileVersion)        { $raw = $vi.FileVersion }
    elseif ($vi.ProductVersion) { $raw = $vi.ProductVersion }
} catch { }
if (-not $raw) {
    try { $raw = (& dotnet $csc -version 2>$null | Select-Object -First 1) } catch { }
}
if (-not $raw) {
    Write-Error "Found csc.dll but could not read its version: $csc"
    exit 4
}

# 3. Reduce to a comparable major.minor.patch and compare.
$m = [regex]::Match($raw, '(\d+)\.(\d+)\.(\d+)')
if (-not $m.Success) { Write-Error "Unrecognized version string: $raw"; exit 5 }
$hostVer = [version]("{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value)
$pinVer  = [version]$Pinned
$ok      = ($pinVer -le $hostVer)
$okText  = if ($ok) { "YES" } else { "NO" }

# 4. Report.
Write-Host ""
Write-Host "  Unity csc.dll : $csc"
Write-Host "  Host Roslyn   : $raw  (parsed $hostVer)"
Write-Host "  Analyzer pin  : $Pinned"
if ($ok) {
    Write-Host "  Verdict       : YES — $Pinned <= host ($hostVer). The pin is safe; record and proceed." -ForegroundColor Green
} else {
    Write-Host "  Verdict       : NO — host ($hostVer) is BELOW $Pinned. Lower the analyzer pin to <= $hostVer and rebuild." -ForegroundColor Red
}
Write-Host ""

# 5. Append the verdict to the canonical record.
$md = Join-Path $PSScriptRoot "../clients/unity-sdk/Tooling/COMPILER_COMPATIBILITY.md"
$ts = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$line = "`n<!-- auto-recorded by scripts/check-roslyn-version.ps1 -->`n- **$ts** — host Roslyn ``$hostVer`` (``$csc``); pinned ``$Pinned``; ``$Pinned <= host`` -> **$okText**.`n"
Add-Content -Path $md -Value $line
Write-Host "Recorded in: $md"
