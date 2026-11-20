param(
    [string]$RuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/esp32s3-runtime'),
    [string]$BaseRuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/electronics-runtime'),
    [switch]$IncludeArduino
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
$BaseRuntimePath=[IO.Path]::GetFullPath($BaseRuntimePath)
$allowed=@((Join-Path $repo '.codex'),(Join-Path $repo 'avalonia/artifacts'))
$requiredFiles=@('arduino-cli.exe','sandbox-xtensa/esp-clang/bin/clang++.exe','sandbox-xtensa/esp-clang/bin/xtensa-esp32s3-elf-clang-ld.exe','sandbox-xtensa/qemu/bin/qemu-system-xtensa.exe','sandbox-xtensa/qemu/COPYING','sandbox-xtensa/qemu/COPYING.LIB')
$arduinoFiles=@(
    'data/packages/esp32/hardware/esp32/3.3.12/platform.txt',
    'data/packages/esp32/hardware/esp32/3.3.12/tools/partitions/default.csv',
    'data/packages/esp32/tools/esp32s3-libs/3.3.12/flags/cpp_flags',
    'data/packages/esp32/tools/esptool_py/5.3.1/esptool.exe',
    'sandbox-xtensa/ctags/ctags.exe',
    'sandbox-xtensa/gnu-runtime/xtensa-esp-elf/include/c++/14.2.0/string',
    'sandbox-xtensa/qemu/share/qemu/esp32s3_rev0_rom.bin'
)
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
    if($IncludeArduino){
        $arduinoMarker=Join-Path $RuntimePath 'offline-esp32s3-arduino-v1.json'
        if(-not(Test-Path -LiteralPath $arduinoMarker) -or -not(Test-Path -LiteralPath (Join-Path $RuntimePath 'data/inventory.yaml'))){throw 'Choose a new RuntimePath for Arduino. The existing SDK was preserved.'}
        $arduino=Get-Content -LiteralPath $arduinoMarker -Raw|ConvertFrom-Json
        if($arduino.version -ne 1 -or $arduino.core -ne '3.3.12' -or @($arduino.files).Count -ne $arduinoFiles.Count -or
            @(Compare-Object @($arduino.files.path|Sort-Object) @($arduinoFiles|Sort-Object)).Count){throw 'Unexpected ESP32-S3 Arduino manifest. It was preserved.'}
        foreach($file in $arduino.files){
            $path=Join-Path $RuntimePath $file.path
            Check-OwnedChild $path
            if(-not(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw 'The ESP32-S3 Arduino SDK is incomplete or modified. It was preserved.'}
        }
        Write-Output 'Pinned ESP32-S3 Arduino runtime is already prepared.'
        return
    }
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
function Install-ArduinoSdk([string]$destination){
    $core=Download 'esp32-core-3.3.12.zip' 'https://github.com/espressif/arduino-esp32/releases/download/3.3.12/esp32-core-3.3.12.zip' '87401004F279A206781E368B66098A8383B7CF87CCE7811A6AFD0163503FA78F'
    $sdk=Download 'esp32s3-libs-3.3.12.zip' 'https://github.com/espressif/arduino-esp32/releases/download/3.3.12/esp32s3-libs-3.3.12.zip' '08F4E7292FEE97DC011DC64E3C670A9A4E83A6B52A604C4B9B18AB2885CDD092'
    $esptool=Download 'esptool-v5.3.1-windows-amd64.zip' 'https://github.com/espressif/esptool/releases/download/v5.3.1/esptool-v5.3.1-windows-amd64.zip' '2B4A73C45DB27426685896F64CE3E557F63A64F43CC100CB65C0CC3486AF96D3'
    $gnu=Download 'xtensa-esp-elf-14.2.0_20260121-win32.zip' 'https://github.com/espressif/crosstool-NG/releases/download/esp-14.2.0_20260121/xtensa-esp-elf-14.2.0_20260121-i686-w64-mingw32.zip' 'D57EEB3783BCE663CCA312323AC9B305A22DFAB479C642320C62AEEFCC536AC0'
    $extract=Join-Path $destination 'arduino-extract'
    New-Item -ItemType Directory -Path $extract|Out-Null
    foreach($package in @(
        @{archive=$core;folder='esp32-core-3.3.12';target='data/packages/esp32/hardware/esp32/3.3.12'},
        @{archive=$sdk;folder='esp32s3-libs';target='data/packages/esp32/tools/esp32s3-libs/3.3.12'},
        @{archive=$esptool;folder='esptool-windows-amd64';target='data/packages/esp32/tools/esptool_py/5.3.1'}
    )){
        & tar.exe -xf $package.archive -C $extract
        if($LASTEXITCODE -ne 0){throw 'ESP32-S3 Arduino archive extraction failed. Staging was preserved.'}
        $source=Join-Path $extract $package.folder
        $target=Join-Path $destination $package.target
        Check-OwnedChild $source;Check-OwnedChild $target
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
        [IO.Directory]::Move($source,$target)
    }
    $gnuParts=@('xtensa-esp-elf/include','xtensa-esp-elf/lib/esp32s3/no-rtti',
        'lib/gcc/xtensa-esp-elf/14.2.0/esp32s3','lib/gcc/xtensa-esp-elf/14.2.0/include-fixed','share/licenses')
    $gnuEntries=@($gnuParts|ForEach-Object{'xtensa-esp-elf/'+$_})
    # Only target headers, libraries and notices are used. Clang supplies the host executables.
    & tar.exe -xf $gnu -C $extract @gnuEntries
    if($LASTEXITCODE -ne 0){throw 'ESP32-S3 target libraries extraction failed. Staging was preserved.'}
    $gnuSource=Join-Path $extract 'xtensa-esp-elf'
    $gnuTarget=Join-Path $destination 'sandbox-xtensa/gnu-runtime'
    Check-OwnedChild $gnuSource;Check-OwnedChild $gnuTarget
    [IO.Directory]::Move($gnuSource,$gnuTarget)
    if(-not(Get-ChildItem -LiteralPath $extract -Force)){[IO.Directory]::Delete($extract)}
    $ctags=Join-Path $destination 'sandbox-xtensa/ctags'
    New-Item -ItemType Directory -Path $ctags|Out-Null
    foreach($file in @('ctags.exe','ctags-COPYING','ctags-source.zip')){
        $source=Join-Path $BaseRuntimePath ('sandbox-avr/'+$file)
        if(-not(Test-Path -LiteralPath $source)){throw 'Prepare the base x64 Arduino ctags runtime first.'}
        Copy-Item -LiteralPath $source -Destination $ctags
    }
    $data=Join-Path $destination 'data'
    foreach($file in @('package_index.json','package_index.json.sig','library_index.json','library_index.json.sig')){
        $source=Join-Path $BaseRuntimePath ('data/'+$file)
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $data}
    }
    $indexPath=Join-Path $data 'package_esp32_index.json'
    $indexUrl='https://espressif.github.io/arduino-esp32/package_esp32_index.json'
    & curl.exe --fail --location --retry 2 --max-time 90 --silent --show-error $indexUrl --output $indexPath
    if($LASTEXITCODE -ne 0){throw 'Could not fetch the Arduino platform index. Staging was preserved.'}
    $index=Get-Content -LiteralPath $indexPath -Raw|ConvertFrom-Json
    $pinned=@($index.packages|Where-Object name -eq 'esp32'|ForEach-Object{$_.platforms}|Where-Object version -eq '3.3.12')
    if($pinned.Count -ne 1 -or $pinned[0].checksum -ne 'SHA-256:87401004f279a206781e368b66098a8383b7cf87cce7811a6afd0163503fa78f'){throw 'The Arduino platform index does not list the expected ESP32 release.'}
    $user=Join-Path $destination 'user'
    New-Item -ItemType Directory -Path (Join-Path $user 'libraries') -Force|Out-Null
    # CLI initializes its inventory on first use. Do that in staging so later
    # AppContainer compilations can keep the installed data tree read-only.
    $config=Join-Path $destination 'prepare-arduino-cli.yaml'
    @{directories=@{data=$data;downloads=(Join-Path $destination 'downloads');user=$user};
        metrics=@{enabled=$false};updater=@{enable_notification=$false}}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $config -Encoding UTF8
    & (Join-Path $destination 'arduino-cli.exe') --config-file $config core list --format json|Out-Null
    if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $data 'inventory.yaml'))){throw 'Could not initialize the ESP32-S3 Arduino inventory. Staging was preserved.'}
    [IO.File]::Delete($config)
    $files=@($arduinoFiles|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $destination $_) -Algorithm SHA256).Hash}})
    @{version=1;core='3.3.12';espIdf='5.5.5';esptool='5.3.1';gnuTarget='14.2.0_20260121';index=$indexUrl;files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $destination 'offline-esp32s3-arduino-v1.json') -Encoding UTF8
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
if($IncludeArduino){Install-ArduinoSdk $stage}
Check-OwnedChild $stage;Check-OwnedChild $RuntimePath
[IO.Directory]::Move($stage,$RuntimePath)
if($IncludeArduino){Write-Output 'Prepared ESP32-S3 Arduino and bare-metal runtime for local verification.'}
else{Write-Output 'Prepared ESP32-S3 bare-metal foundation runtime.'}
