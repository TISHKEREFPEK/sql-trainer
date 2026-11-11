param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][ValidateSet('avr','pico')][string]$Profile
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\','/')
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
if($Source -eq $Destination -or $Source.StartsWith($Destination+'\',[StringComparison]::OrdinalIgnoreCase) -or $Destination.StartsWith($Source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'SDK source and destination must be separate folders.'}
if(Test-Path -LiteralPath $Destination){if(Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1){throw 'SDK destination is not empty. Choose a new output folder; existing files were preserved.'}}
$items=@('arduino-cli.exe','node.exe','ArduinoCLI-LICENSE.txt','Node-LICENSE.txt','data')
$items+=if($Profile -eq 'avr'){@('offline-ready-v4.json','sandbox-avr','user/libraries/Servo')}else{@('offline-pico-v1.json','offline-pico-libraries-v1.json','sandbox-arm')}
$items+=@('user/libraries/Adafruit_SSD1306','user/libraries/Adafruit_GFX_Library','user/libraries/Adafruit_BusIO','user/libraries/Adafruit_ILI9341','offline-spi-libraries-v1.json','user/libraries/DHT_sensor_library','user/libraries/Adafruit_Unified_Sensor','offline-dht-libraries-v1.json')
foreach($item in $items){if(-not(Test-Path -LiteralPath (Join-Path $Source $item))){throw "Incomplete $Profile SDK: $item"}}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
@{version=1;profile=$Profile;state='copying'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'classroom-sdk-copy.json') -Encoding UTF8
foreach($item in $items){
    $inputPath=Join-Path $Source $item;$outputPath=Join-Path $Destination $item
    if(Test-Path -LiteralPath $inputPath -PathType Container){
        # Robocopy supports long Windows paths. /E copies, never purges/mirrors.
        & robocopy.exe $inputPath $outputPath /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $copyResult=$LASTEXITCODE
        if($copyResult -ge 8 -or ($copyResult -band 2)){throw "SDK directory copy failed ($copyResult): $item"}
    }else{
        New-Item -ItemType Directory -Path (Split-Path $outputPath -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $inputPath -Destination $outputPath
    }
}
@{version=1;profile=$Profile;state='ready';items=$items} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Destination 'classroom-sdk-copy.json') -Encoding UTF8
Write-Output "Copied $Profile SDK, including licenses. Download archives, previous SDKs, source configuration and user sketches are excluded."
