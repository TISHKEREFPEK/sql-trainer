param([Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
foreach($entry in @(@{Folder='DHT_sensor_library';Name='DHT sensor library';Version='1.4.7';Header='DHT.h'},@{Folder='Adafruit_Unified_Sensor';Name='Adafruit Unified Sensor';Version='1.1.15';Header='Adafruit_Sensor.h'})){
    $library=Join-Path $RuntimePath ('user/libraries/'+$entry.Folder)
    $properties=Join-Path $library 'library.properties'
    $ready=Test-Path -LiteralPath $properties
    if($ready){$ready=Select-String -LiteralPath $properties -Pattern ('^version='+[regex]::Escape($entry.Version)+'$') -Quiet}
    if((Test-Path -LiteralPath $library)-and -not $ready){throw 'A different or incomplete DHT dependency exists. Prepare a new SDK; it was not overwritten.'}
    if(-not $ready){
        $cli=Join-Path $RuntimePath 'arduino-cli.exe'
        if(-not(Test-Path -LiteralPath $cli)){throw 'Prepare the base compiler runtime first.'}
        $previous=@{}
        foreach($item in @('DATA','DOWNLOADS','USER')){
            $key='ARDUINO_DIRECTORIES_'+$item;$previous[$key]=[Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key,(Join-Path $RuntimePath $item.ToLowerInvariant()))
        }
        try{
            & $cli lib update-index
            if($LASTEXITCODE -ne 0){throw 'Arduino library index update failed.'}
            & $cli lib install --no-deps ($entry.Name+'@'+$entry.Version)
            if($LASTEXITCODE -ne 0){throw 'Pinned DHT dependency installation failed.'}
        }finally{foreach($restoreEntry in $previous.GetEnumerator()){[Environment]::SetEnvironmentVariable($restoreEntry.Key,$restoreEntry.Value)}}
    }
    if(-not(Test-Path -LiteralPath (Join-Path $library $entry.Header)) -or -not(Select-String -LiteralPath $properties -Pattern ('^version='+[regex]::Escape($entry.Version)+'$') -Quiet)){throw 'The DHT dependency is incomplete.'}
}
$marker=Join-Path $RuntimePath 'offline-dht-libraries-v1.json'
if(-not(Test-Path -LiteralPath $marker)){
    @{version=1;DHT_sensor_library='1.4.7';Adafruit_Unified_Sensor='1.1.15';source='Arduino library index; complete library folder including license';interface='DHT11 single-wire GPIO'} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
}
Write-Output 'Pinned offline DHT 1.4.7 and Unified Sensor 1.1.15 is prepared.'
