# Baut BatteryWidget.exe mit dem in Windows enthaltenen C#-Compiler (.NET Framework 4.8) – kein SDK nötig.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc -nologo -target:winexe -platform:x64 -optimize+ -codepage:65001 `
    "-win32manifest:$(Join-Path $root 'src\app.manifest')" `
    "-out:$(Join-Path $out 'BatteryWidget.exe')" `
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }
Write-Host "OK: $(Join-Path $out 'BatteryWidget.exe')"
