param(
    [string]$RuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/esp32s3-runtime'),
    [string]$BaseRuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/electronics-runtime')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
$BaseRuntimePath=[IO.Path]::GetFullPath($BaseRuntimePath)
$allowed=@((Join-Path $repo '.codex'),(Join-Path $repo 'avalonia/artifacts'))
$requiredFiles=@('arduino-cli.exe','sandbox-xtensa/esp-clang/bin/clang++.exe','sandbox-xtensa/esp-clang/bin/xtensa-esp32s3-elf-clang-ld.exe','sandbox-xtensa/qemu/bin/qemu-system-xtensa.exe','sandbox-xtensa/qemu/COPYING','sandbox-xtensa/qemu/COPYING.LIB')
function Check-OwnedChild([string]$path){
    $absolute=[IO.Path]::GetFullPath($path)
    if(-not($allowed|Where-Object{$absolute.StartsWith($_.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)})){throw 'Choose an ESP32-S3 runtime under this workspace artifacts or .codex folder.'}
    if((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Runtime paths must not be links.'}
    $ancestor=Split-Path -Parent $absolute
    while($ancestor -and $ancestor.StartsWith($repo,[StringComparison]::OrdinalIgnoreCase)){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Runtime paths must not traverse links.'}
        $ancestor=Split-Path -Parent $ancestor
    }
}
Check-OwnedChild $RuntimePath
$marker=Join-Path $RuntimePath 'offline-esp32s3-foundation-v1.json'
if(Test-Path -LiteralPath $RuntimePath){
    if(-not(Test-Path -LiteralPath $marker)){throw 'An unmarked ESP32-S3 runtime exists. It was preserved; choose a new folder.'}
    $ready=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    if($ready.version -ne 1 -or -not $ready.PSObject.Properties['clang'] -or $ready.clang -ne '19.1.2_20250312' -or $ready.qemu -ne '9.2.2_20260417'){throw 'The ESP32-S3 runtime version differs. It was preserved.'}
    if(-not $ready.PSObject.Properties['linker'] -or $ready.linker -ne 'GNU 2.43.1'){throw 'The ESP32-S3 foundation linker differs. Prepare a fresh runtime.'}
    if(-not $ready.PSObject.Properties['files'] -or @($ready.files).Count -ne $requiredFiles.Count -or @(Compare-Object @($ready.files.path|Sort-Object) @($requiredFiles|Sort-Object)).Count){throw 'The ESP32-S3 runtime manifest is incomplete. It was preserved.'}
    foreach($file in $ready.files){if(-not(Test-Path -LiteralPath (Join-Path $RuntimePath $file.path)) -or (Get-FileHash -LiteralPath (Join-Path $RuntimePath $file.path) -Algorithm SHA256).Hash -ne $file.sha256){throw 'The ESP32-S3 runtime is incomplete or modified. It was preserved.'}}
    Write-Output 'Pinned ESP32-S3 foundation runtime is already prepared.'
    return
}
foreach($file in @('arduino-cli.exe','ArduinoCLI-LICENSE.txt')){if(-not(Test-Path -LiteralPath (Join-Path $BaseRuntimePath $file))){throw 'Prepare the base compiler runtime first.'}}
if(((& (Join-Path $BaseRuntimePath 'arduino-cli.exe') version) -join "`n") -notmatch '1\.5\.1'){throw 'ESP32-S3 preparation requires the pinned base Arduino CLI 1.5.1.'}
$archives=Join-Path $repo '.codex/electronics-build-tools'
New-Item -ItemType Directory -Path $archives -Force|Out-Null
function Download([string]$name,[string]$url,[string]$sha){
    $destination=Join-Path $archives $name
    if(-not(Test-Path -LiteralPath $destination)){
        $partial=$destination+'.partial-'+[guid]::NewGuid().ToString('N')
        & curl.exe --fail --location --retry 2 --max-time 900 --speed-time 90 --speed-limit 1024 --silent --show-error $url --output $partial
        if($LASTEXITCODE -ne 0){throw 'ESP32-S3 download failed; partial file was preserved for inspection.'}
        if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha){throw 'ESP32-S3 archive hash mismatch; existing files were preserved.'}
        Check-OwnedChild $partial;Check-OwnedChild $destination
        Move-Item -LiteralPath $partial -Destination $destination
    }
    if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sha){throw 'Existing ESP32-S3 archive hash mismatch. It was preserved.'}
    return $destination
}
$clang=Download 'clang-esp-19.1.2_20250312-win64.tar.xz' 'https://github.com/espressif/llvm-project/releases/download/esp-19.1.2_20250312/clang-esp-19.1.2_20250312-x86_64-w64-mingw32.tar.xz' '0FB20439A3D1D31088E646A6F2A8C9277AC6AED858188E2AFA470BDB42E13AFB'
$qemu=Download 'qemu-xtensa-9.2.2-20260417-win64.tar.xz' 'https://github.com/espressif/qemu/releases/download/esp-develop-9.2.2-20260417/qemu-xtensa-softmmu-esp_develop_9.2.2_20260417-x86_64-w64-mingw32.tar.xz' '3C483D77F5350A568DF1FAF4D8DBC82C95D6BC2B826D0D4BE910485E0A68CA2A'
$copying=Download 'qemu-9.2.2-20260417-COPYING.txt' 'https://raw.githubusercontent.com/espressif/qemu/esp-develop-9.2.2-20260417/COPYING' '6F04AE8364D0079A192B14635F4B1DA294CE18724C034C39A6A41D1B09DF6100'
$copyingLib=Download 'qemu-9.2.2-20260417-COPYING.LIB.txt' 'https://raw.githubusercontent.com/espressif/qemu/esp-develop-9.2.2-20260417/COPYING.LIB' 'DC626520DCD53A22F727AF3EE42C770E56C97A64FE3ADB063799D8AB032FE551'
$stage=$RuntimePath+'.preparing-'+[guid]::NewGuid().ToString('N')
Check-OwnedChild $stage
$tools=Join-Path $stage 'sandbox-xtensa'
New-Item -ItemType Directory -Path $tools,(Join-Path $stage 'data/packages') -Force|Out-Null
& tar.exe -xf $clang -C $tools
if($LASTEXITCODE -ne 0){throw 'Espressif Clang extraction failed; staging was preserved.'}
& tar.exe -xf $qemu -C $tools
if($LASTEXITCODE -ne 0){throw 'QEMU extraction failed; staging was preserved.'}
foreach($file in @('arduino-cli.exe','ArduinoCLI-LICENSE.txt')){Copy-Item -LiteralPath (Join-Path $BaseRuntimePath $file) -Destination $stage}
Copy-Item -LiteralPath $copying -Destination (Join-Path $tools 'qemu/COPYING')
Copy-Item -LiteralPath $copyingLib -Destination (Join-Path $tools 'qemu/COPYING.LIB')
$compiler=Join-Path $tools 'esp-clang/bin/clang++.exe'
$linker=Join-Path $tools 'esp-clang/bin/xtensa-esp32s3-elf-clang-ld.exe'
$emulator=Join-Path $tools 'qemu/bin/qemu-system-xtensa.exe'
if(((& $compiler --version) -join "`n") -notmatch 'clang version 19\.1\.2'){throw 'Unexpected Espressif Clang version.'}
if(((& $linker --version) -join "`n") -notmatch 'GNU Binutils\) 2\.43\.1'){throw 'Unexpected bundled GNU linker version.'}
if(((& $emulator --version) -join "`n") -notmatch 'esp_develop_9\.2\.2_20260417'){throw 'Unexpected ESP QEMU version.'}
$files=$requiredFiles|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash}}
@{version=1;clang='19.1.2_20250312';linker='GNU 2.43.1';qemu='9.2.2_20260417';profile='bare-metal foundation; not native editor or full Cirkit parity';files=@($files);sourceCompliance='Preserve all archive notices; corresponding source/dependency and ROM redistribution audit required before packaging.';clangSource='https://github.com/espressif/llvm-project/tree/esp-19.1.2_20250312';qemuSource='https://github.com/espressif/qemu/tree/esp-develop-9.2.2-20260417'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $stage 'offline-esp32s3-foundation-v1.json') -Encoding UTF8
Check-OwnedChild $stage;Check-OwnedChild $RuntimePath
[IO.Directory]::Move($stage,$RuntimePath)
Write-Output 'Prepared separate ESP32-S3 bare-metal foundation runtime; Arduino/peripherals/editor and final source-compliant packaging remain open.'
