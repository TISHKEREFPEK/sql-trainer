param([switch]$SkipTests, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
function Run-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Run-DotNet @('restore', 'Classroom.sln', '--locked-mode')
Run-DotNet @('build', 'Classroom.sln', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
if (-not $SkipTests) {
    $env:CLASSROOM_UI_OUTPUT = Join-Path $root 'artifacts/ui'
    Run-DotNet @('test', 'Classroom.sln', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', 'artifacts/tests')
}
$publish = Join-Path $root 'artifacts/publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($app in @('Teacher', 'Student')) {
    Run-DotNet @('publish', "src/Classroom.$app", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishTrimmed=false', '-p:DebugType=None', '-o', "$publish/$app")
}
foreach ($app in @('Server', 'Worker')) {
    $location = $app.ToLowerInvariant()
    Run-DotNet @('publish', "src/Classroom.$app", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishTrimmed=false', '-p:DebugType=None', '-o', "$publish/Teacher/runtime/$location")
}
$forbidden = Get-ChildItem "$publish/Student" -Recurse -File | Where-Object {
    $_.Name -match '^Classroom\.(Domain|Storage|Server|Worker)(\.|$)|catalog\.json|\.sqlite($|-)|\.pfx$|e_sqlite3|BouncyCastle|EntityFrameworkCore'
}
if ($forbidden) { throw "Student distribution contains teacher files: $($forbidden.Name -join ', ')" }
if (-not $SkipInstaller) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $iscc = if ($compiler) { $compiler.Source } else { "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
    if (-not (Test-Path $iscc)) { throw 'Install Inno Setup 6 to build installers, or use -SkipInstaller.' }
    foreach ($app in @('Teacher', 'Student')) {
        & $iscc "$root/installer/$($app.ToLowerInvariant()).iss"
        if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $app" }
    }
}
