param([Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference = 'Stop'
$RuntimePath = [IO.Path]::GetFullPath($RuntimePath)
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tools = Join-Path $repoRoot '.codex/electronics-build-tools'
$target = Join-Path $RuntimePath 'sandbox-avr'
$marker = Join-Path $target 'sandbox-ready.json'
if ((Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath (Join-Path $target 'bin/avr-g++.exe')) -and (Test-Path -LiteralPath (Join-Path $target 'ctags.exe'))) { return }
New-Item -ItemType Directory -Path $tools -Force | Out-Null
function Fetch([string]$Url,[string]$File,[string]$Sha) {
    if (-not (Test-Path -LiteralPath $File)) { Invoke-WebRequest -Uri $Url -OutFile $File }
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $Sha.ToUpperInvariant()) { throw "Checksum mismatch: $File" }
}
$avrArchive = Join-Path $tools 'avr-gcc-16.1.0-x64-windows.zip'
Fetch 'https://github.com/ZakKemble/avr-gcc-build/releases/download/v16.1.0-1/avr-gcc-16.1.0-x64-windows.zip' $avrArchive 'DE4AAD501EE42B63D5D262E45470924CD2654F6BC437F0299875EFA6B8D8D0FE'
$ctagsArchive = Join-Path $tools 'ctags-abc8fca7499f44c725122881cd380a88c37abe0e.zip'
Fetch 'https://codeload.github.com/arduino/ctags/zip/abc8fca7499f44c725122881cd380a88c37abe0e' $ctagsArchive '1677945EE31A57FDD52ADBB64CF61C23D0D261C6E2BA3BEDA048C8DDBDC4A9AF'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Build the runtime on a Windows build machine with Visual Studio C++ Build Tools. Classroom PCs use the prepared runtime and do not need Build Tools.' }
$installation = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ x64 Build Tools are required to build Arduino ctags from its pinned source.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$work = Join-Path $tools ('build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Expand-Archive -LiteralPath $avrArchive -DestinationPath (Join-Path $work 'avr')
Expand-Archive -LiteralPath $ctagsArchive -DestinationPath (Join-Path $work 'ctags')
$source = Join-Path $work 'ctags/ctags-abc8fca7499f44c725122881cd380a88c37abe0e'
$driver = Join-Path $work 'build-ctags.cmd'
$commands = "@echo off`r`nchcp 65001 >nul`r`ncall `"$vcvars`"`r`nif errorlevel 1 exit /b %errorlevel%`r`ncd /d `"$source`"`r`nnmake /nologo /f mk_mvc.mak ctags`r`n"
[IO.File]::WriteAllText($driver,$commands,(New-Object Text.UTF8Encoding($false)))
& $env:ComSpec /d /c $driver
if ($LASTEXITCODE -ne 0) { throw 'Building x64 Arduino ctags failed.' }
$stage = Join-Path $RuntimePath ('sandbox-avr.staging-' + [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath (Join-Path $work 'avr/avr-gcc-16.1.0-x64-windows') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $source 'ctags.exe') -Destination (Join-Path $stage 'ctags.exe')
Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination (Join-Path $stage 'ctags-COPYING')
Copy-Item -LiteralPath $ctagsArchive -Destination (Join-Path $stage 'ctags-source.zip')
[IO.File]::WriteAllText((Join-Path $stage 'sandbox-ready.json'),'{"version":1,"avrGcc":"16.1.0","avrSource":"https://github.com/ZakKemble/avr-gcc-build/tree/v16.1.0-1","ctagsCommit":"abc8fca7499f44c725122881cd380a88c37abe0e","ctagsSourceArchive":"ctags-source.zip"}')
# Only move our explicitly named toolchain children within this runtime.
$runtimePrefix = $RuntimePath.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($stage,$target)) { if (-not ([IO.Path]::GetFullPath($path)).StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Toolchain path leaves runtime' } }
if (Test-Path -LiteralPath $target) {
    $backup = Join-Path $RuntimePath ('sandbox-avr.previous-' + [guid]::NewGuid().ToString('N'))
    if (-not ([IO.Path]::GetFullPath($backup)).StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path leaves runtime' }
    Move-Item -LiteralPath $target -Destination $backup
}
Move-Item -LiteralPath $stage -Destination $target
Write-Output 'Prepared x64 AVR compiler and source-compatible Arduino ctags for AppContainer. No build tools or internet are required on classroom PCs.'
