param(
    [string]$RuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/pico-runtime'),
    [string]$BaseRuntimePath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/electronics-runtime')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
$BaseRuntimePath=[IO.Path]::GetFullPath($BaseRuntimePath)
$runtimePrefix=$RuntimePath.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$basePrefix=$BaseRuntimePath.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if($RuntimePath -eq $BaseRuntimePath -or $RuntimePath.StartsWith($basePrefix,[StringComparison]::OrdinalIgnoreCase) -or $BaseRuntimePath.StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Pico and AVR runtime folders must be separate and must not contain one another.'}
$coreVersion='6.3.2'
$armVersion='14.2.1-1.1'
$index='https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json'
$core=Join-Path $RuntimePath "data/packages/rp2040/hardware/rp2040/$coreVersion/platform.txt"
$arm=Join-Path $RuntimePath 'sandbox-arm'
$marker=Join-Path $RuntimePath 'offline-pico-v1.json'
if((Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath $core) -and (Test-Path -LiteralPath (Join-Path $arm 'bin/arm-none-eabi-g++.exe')) -and (Test-Path -LiteralPath (Join-Path $arm 'ctags.exe')) -and (Test-Path -LiteralPath (Join-Path $RuntimePath 'arduino-cli.exe')) -and (Test-Path -LiteralPath (Join-Path $RuntimePath 'node.exe'))){
    $ready=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if($ready.version -eq 1 -and $ready.core -eq $coreVersion -and $ready.arm -eq $armVersion){Write-Output 'Pinned Pico compiler runtime is already prepared.';return}
    throw 'Pico marker version differs. Prepare a new runtime folder instead of overwriting it.'
}
foreach($name in @('arduino-cli.exe','node.exe','ArduinoCLI-LICENSE.txt','Node-LICENSE.txt')){
    if(-not (Test-Path -LiteralPath (Join-Path $BaseRuntimePath $name))){throw 'Prepare the base runtime first with prepare-electronics.ps1.'}
}
foreach($name in @('ctags.exe','ctags-COPYING','ctags-source.zip')){if(-not(Test-Path -LiteralPath (Join-Path $BaseRuntimePath "sandbox-avr/$name"))){throw 'Prepare the native x64 ctags in the base runtime first.'}}
New-Item -ItemType Directory -Path $RuntimePath -Force | Out-Null
foreach($name in @('arduino-cli.exe','node.exe','ArduinoCLI-LICENSE.txt','Node-LICENSE.txt')){Copy-Item -LiteralPath (Join-Path $BaseRuntimePath $name) -Destination $RuntimePath -Force}
$cli=Join-Path $RuntimePath 'arduino-cli.exe'
if((& $cli version) -notmatch '1\.5\.1'){throw 'Pico preparation requires the pinned base Arduino CLI 1.5.1.'}
if((& (Join-Path $RuntimePath 'node.exe') --version).Trim() -ne 'v24.19.0'){throw 'Pico preparation requires the pinned base Node v24.19.0.'}
$config=Join-Path $RuntimePath 'arduino-cli.yaml'
@{directories=@{data=(Join-Path $RuntimePath 'data');downloads=(Join-Path $RuntimePath 'downloads');user=(Join-Path $RuntimePath 'user')};board_manager=@{additional_urls=@($index)};metrics=@{enabled=$false};updater=@{enable_notification=$false}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding UTF8
if(-not (Test-Path -LiteralPath $core)){
    & $cli --config-file $config core update-index
    if($LASTEXITCODE -ne 0){throw 'Could not prepare Pico compiler indexes.'}
    & $cli --config-file $config core install "rp2040:rp2040@$coreVersion" --skip-post-install --skip-pre-uninstall
    if($LASTEXITCODE -ne 0){throw 'Could not install the pinned Pico core and declared tools.'}
}
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$archives=Join-Path $repo '.codex/electronics-build-tools'
New-Item -ItemType Directory -Path $archives -Force | Out-Null
$zipName="xpack-arm-none-eabi-gcc-$armVersion-win32-x64.zip"
$archive=Join-Path $archives $zipName
$sha='0B2D496B383BA578182EB57B3F7D35FF510E36EDA56257883B902FA07C3BBA55'
if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest "https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/releases/download/v$armVersion/$zipName" -OutFile $archive}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha){throw 'Pico ARM compiler archive SHA256 mismatch.'}
if(Test-Path -LiteralPath $arm){throw 'An unmarked ARM compiler folder exists. Inspect it and choose a new runtime; it was not replaced.'}
$stage=Join-Path ([IO.Path]::GetTempPath()) ('pico-arm-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
# A short temporary root also supports PowerShell 5.1's ZIP path limits.
Expand-Archive -LiteralPath $archive -DestinationPath $stage
$source=Join-Path $stage "xpack-arm-none-eabi-gcc-$armVersion"
$tempPrefix=([IO.Path]::GetFullPath([IO.Path]::GetTempPath())).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
foreach($path in @($stage,$source)){if(-not ([IO.Path]::GetFullPath($path)).StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Pico staging path leaves the intended temporary folder.'}}
if(-not ([IO.Path]::GetFullPath($arm)).StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Pico compiler path leaves the intended runtime.'}
if(-not (Test-Path -LiteralPath (Join-Path $source 'bin/arm-none-eabi-g++.exe'))){throw 'Pico ARM archive has an unexpected layout.'}
Move-Item -LiteralPath $source -Destination $arm
Remove-Item -LiteralPath $stage
foreach($name in @('ctags.exe','ctags-COPYING','ctags-source.zip')){Copy-Item -LiteralPath (Join-Path $BaseRuntimePath "sandbox-avr/$name") -Destination (Join-Path $arm $name)}
@{version=1;core=$coreVersion;arm=$armVersion;armArchiveSha256=$sha;coreIndex=$index;armSources="https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/tree/v14.2.1-1.1.1";coreSources="https://github.com/earlephilhower/arduino-pico/tree/$coreVersion";sandbox='AppContainer'} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
Write-Output 'Prepared pinned Pico compiler. Keep all bundled licenses/source references; internet is not used for sketch compilation. Pico circuit integration is still unfinished.'
