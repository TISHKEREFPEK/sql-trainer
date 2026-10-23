param([string]$RuntimePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/electronics-runtime'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RuntimePath = [System.IO.Path]::GetFullPath($RuntimePath)
New-Item -ItemType Directory -Path $RuntimePath -Force | Out-Null
$cli = Join-Path $RuntimePath 'arduino-cli.exe'
$node = Join-Path $RuntimePath 'node.exe'
$core = Join-Path $RuntimePath 'data/packages/arduino/hardware/avr/1.8.6'
$marker = Join-Path $RuntimePath 'offline-ready-v2.json'
if ((Test-Path -LiteralPath $marker) -and (Test-Path -LiteralPath $cli) -and (Test-Path -LiteralPath $node) -and (Test-Path -LiteralPath $core)) {
    Write-Output 'Offline Arduino Uno runtime is already prepared.'
    return
}
function Download([string]$Url, [string]$File) { Invoke-WebRequest -Uri $Url -OutFile $File }
function Check([string]$File, [string]$Hash) {
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $Hash.ToUpperInvariant()) { throw "Checksum mismatch: $File" }
}
function Run-Cli([string[]]$Parameters) {
    & $cli @Parameters
    if ($LASTEXITCODE -ne 0) { throw "Arduino CLI failed: $($Parameters -join ' ')" }
}
$archive = Join-Path $RuntimePath 'arduino-cli.zip'
if (-not (Test-Path -LiteralPath $archive)) { Download 'https://downloads.arduino.cc/arduino-cli/arduino-cli_1.5.1_Windows_64bit.zip' $archive }
Check $archive 'FABE42E0EB04D00E776A66178299FF95A46C623DBC260F997E58FD514853DD40'
Expand-Archive -LiteralPath $archive -DestinationPath $RuntimePath -Force
Copy-Item -LiteralPath (Join-Path $RuntimePath 'LICENSE.txt') -Destination (Join-Path $RuntimePath 'ArduinoCLI-LICENSE.txt') -Force
$nodeVersion = 'v24.19.0'
if (-not (Test-Path -LiteralPath $node)) {
    $zipName = "node-$nodeVersion-win-x64.zip"
    $nodeZip = Join-Path $RuntimePath $zipName
    $sums = Join-Path $RuntimePath 'node-SHASUMS256.txt'
    Download "https://nodejs.org/dist/$nodeVersion/$zipName" $nodeZip
    Download "https://nodejs.org/dist/$nodeVersion/SHASUMS256.txt" $sums
    $entry = Get-Content -LiteralPath $sums | Where-Object { $_ -match "\s+$([regex]::Escape($zipName))$" }
    if (-not $entry) { throw 'Node checksum is missing.' }
    Check $nodeZip (($entry -split '\s+')[0])
    Expand-Archive -LiteralPath $nodeZip -DestinationPath $RuntimePath -Force
    Copy-Item -LiteralPath (Join-Path $RuntimePath "node-$nodeVersion-win-x64/node.exe") -Destination $node
}
if ((& $node --version).Trim() -ne $nodeVersion) { throw "Expected Node $nodeVersion" }
Download "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" (Join-Path $RuntimePath 'Node-LICENSE.txt')
$env:ARDUINO_DIRECTORIES_DATA = Join-Path $RuntimePath 'data'
$env:ARDUINO_DIRECTORIES_DOWNLOADS = Join-Path $RuntimePath 'downloads'
$env:ARDUINO_DIRECTORIES_USER = Join-Path $RuntimePath 'user'
New-Item -ItemType Directory -Path $env:ARDUINO_DIRECTORIES_USER -Force | Out-Null
Run-Cli @('core', 'update-index')
Run-Cli @('core', 'install', 'arduino:avr@1.8.6')
Run-Cli @('lib', 'install', 'Servo@1.2.2')
Set-Content -LiteralPath $marker -Value '{"version":2,"arduinoCli":"1.5.1","avrCore":"1.8.6","node":"24.19.0","avr8js":"0.21.0","servo":"1.2.2"}'
Write-Output 'Prepared offline Arduino Uno toolchain. Internet is not needed for sketch compilation or simulation.'
