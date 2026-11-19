param([switch]$SkipTests,[switch]$SkipInstaller,[switch]$SkipPackageCheck,[string]$PublishPath,[string]$ElectronicsRuntimePath,[string]$PicoRuntimePath,[string]$InnoCompilerPath,[string]$InstallerOutputPath,[string]$Esp32S3RuntimePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if($PublishPath){$PublishPath=[IO.Path]::GetFullPath($PublishPath)}
if($ElectronicsRuntimePath){$ElectronicsRuntimePath=[IO.Path]::GetFullPath($ElectronicsRuntimePath)}
if($PicoRuntimePath){$PicoRuntimePath=[IO.Path]::GetFullPath($PicoRuntimePath)}
if($Esp32S3RuntimePath){$Esp32S3RuntimePath=[IO.Path]::GetFullPath($Esp32S3RuntimePath)}
if($InnoCompilerPath){$InnoCompilerPath=[IO.Path]::GetFullPath($InnoCompilerPath)}
if($InstallerOutputPath){$InstallerOutputPath=[IO.Path]::GetFullPath($InstallerOutputPath)}
Set-Location $root
$repo=Split-Path $root -Parent
if(-not $PublishPath){$PublishPath=Join-Path $root 'artifacts/publish'}
$publish=[IO.Path]::GetFullPath($PublishPath).TrimEnd('\','/')
$allowed=@((Join-Path $root 'artifacts'),(Join-Path $repo '.codex')) | ForEach-Object {([IO.Path]::GetFullPath($_)).TrimEnd('\','/')+'\'}
if(-not($allowed | Where-Object {$publish.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)})){throw 'Publish output must be a child folder of this repository artifacts or .codex folder.'}
if(Test-Path -LiteralPath $publish){if(Get-ChildItem -LiteralPath $publish -Force | Select-Object -First 1){throw 'Publish output is not empty. Choose a new PublishPath; previous files were preserved.'}}
if(-not $ElectronicsRuntimePath){$ElectronicsRuntimePath=Join-Path $root 'artifacts/electronics-runtime'}
if(-not $PicoRuntimePath){$PicoRuntimePath=Join-Path $root 'artifacts/pico-runtime'}
if(-not $Esp32S3RuntimePath){$Esp32S3RuntimePath=Join-Path $root 'artifacts/esp32s3-arduino-runtime'}
$ElectronicsRuntimePath=[IO.Path]::GetFullPath($ElectronicsRuntimePath)
$PicoRuntimePath=[IO.Path]::GetFullPath($PicoRuntimePath)
foreach($sdk in @($ElectronicsRuntimePath,$PicoRuntimePath,$Esp32S3RuntimePath)){if($publish -eq $sdk -or $publish.StartsWith($sdk.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $sdk.StartsWith($publish+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Publish output and source SDK folders must be separate.'}}
if(-not $SkipInstaller){
    & "$PSScriptRoot/build-installers.ps1" -PublishPath $publish -InnoCompilerPath $InnoCompilerPath -OutputPath $InstallerOutputPath -CheckPrerequisitesOnly
}
function Run-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Run-DotNet @('restore', 'Classroom.sln', '--locked-mode')
Run-DotNet @('build', 'Classroom.sln', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
& "$PSScriptRoot/prepare-electronics.ps1" -RuntimePath $ElectronicsRuntimePath
& "$PSScriptRoot/prepare-pico.ps1" -RuntimePath $PicoRuntimePath -BaseRuntimePath $ElectronicsRuntimePath
if (-not $SkipTests) {
    & "$PSScriptRoot/prepare-esp32s3.ps1" -RuntimePath $Esp32S3RuntimePath -BaseRuntimePath $ElectronicsRuntimePath -IncludeArduino
    $env:CLASSROOM_ELECTRONICS_RUNTIME=$ElectronicsRuntimePath
    $env:CLASSROOM_PICO_RUNTIME = $PicoRuntimePath
    $env:CLASSROOM_ESP32S3_RUNTIME = $Esp32S3RuntimePath
    $env:CLASSROOM_UI_OUTPUT = Join-Path $root 'artifacts/ui'
    Run-DotNet @('test', 'Classroom.sln', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', 'artifacts/tests')
}
foreach ($app in @('Teacher', 'Student')) {
    Run-DotNet @('publish', "src/Classroom.$app", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishTrimmed=false', '-p:DebugType=None', '-o', "$publish/$app")
}
foreach ($app in @('Server', 'Worker')) {
    $location = $app.ToLowerInvariant()
    Run-DotNet @('publish', "src/Classroom.$app", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishTrimmed=false', '-p:DebugType=None', '-o', "$publish/Teacher/runtime/$location")
}
foreach ($app in @('Teacher', 'Student')) {
    & "$PSScriptRoot/copy-electronics-runtime.ps1" -Source $ElectronicsRuntimePath -Destination (Join-Path $publish "$app/electronics/runtime") -Profile avr
    & "$PSScriptRoot/copy-electronics-runtime.ps1" -Source $PicoRuntimePath -Destination (Join-Path $publish "$app/electronics/pico-runtime") -Profile pico
}
$forbidden = [IO.Directory]::EnumerateFiles(('\\?\'+(Join-Path $publish 'Student')),'*',[IO.SearchOption]::AllDirectories) | Where-Object {
    [IO.Path]::GetFileName($_) -match '^Classroom\.(Domain|Storage|Server|Worker)(\.|$)|catalog\.json|\.sqlite($|-)|\.pfx$|e_sqlite3|BouncyCastle|EntityFrameworkCore'
}
if ($forbidden) { throw "Student distribution contains teacher files: $($forbidden -join ', ')" }
if(-not $SkipPackageCheck){
    foreach($app in @('Teacher','Student')){
        $checkOutput=Join-Path (Split-Path $publish -Parent) ('package-check-'+$app.ToLowerInvariant()+'-'+[guid]::NewGuid().ToString('N'))
        $process=Start-Process -FilePath (Join-Path $publish "$app/Classroom.$app.exe") -ArgumentList @('--verify-electronics',('"'+$checkOutput+'"')) -PassThru -Wait -WindowStyle Hidden
        if($process.ExitCode -ne 0){throw "Published $app electronics check failed. Inspect $checkOutput/package-check.json"}
    }
}
if (-not $SkipInstaller) {
    & "$PSScriptRoot/build-installers.ps1" -PublishPath $publish -InnoCompilerPath $InnoCompilerPath -OutputPath $InstallerOutputPath
}
