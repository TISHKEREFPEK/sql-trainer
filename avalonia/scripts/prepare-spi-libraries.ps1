param([Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
$library=Join-Path $RuntimePath 'user/libraries/Adafruit_ILI9341'
$properties=Join-Path $library 'library.properties'
$ready=Test-Path -LiteralPath $properties
if($ready){$ready=Select-String -LiteralPath $properties -Pattern '^version=1\.7\.0$' -Quiet}
if((Test-Path -LiteralPath $library)-and -not $ready){throw 'A different or incomplete Adafruit ILI9341 exists. Prepare a new SDK; it was not overwritten.'}
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
        & $cli lib install --no-deps 'Adafruit ILI9341@1.7.0'
        if($LASTEXITCODE -ne 0){throw 'Pinned Adafruit ILI9341 installation failed.'}
    }finally{foreach($entry in $previous.GetEnumerator()){[Environment]::SetEnvironmentVariable($entry.Key,$entry.Value)}}
}
if(-not(Test-Path -LiteralPath (Join-Path $library 'Adafruit_ILI9341.h')) -or -not(Select-String -LiteralPath $properties -Pattern '^version=1\.7\.0$' -Quiet)){throw 'The ILI9341 library is incomplete.'}
$marker=Join-Path $RuntimePath 'offline-spi-libraries-v1.json'
if(-not(Test-Path -LiteralPath $marker)){
    @{version=1;Adafruit_ILI9341='1.7.0';source='Arduino library index; complete library folder including license';interface='SPI display; touch/SD libraries are not included'} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
}
Write-Output 'Pinned offline Adafruit ILI9341 1.7.0 is prepared.'
