param([Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
$dependencies=@(@{Folder='OneWire';Name='OneWire';Version='2.3.8';Header='OneWire.h'},@{Folder='DallasTemperature';Name='DallasTemperature';Version='4.0.5';Header='DallasTemperature.h'})
$missing=@()
foreach($entry in $dependencies){
    $library=Join-Path $RuntimePath ('user/libraries/'+$entry.Folder)
    $properties=Join-Path $library 'library.properties'
    $ready=Test-Path -LiteralPath $properties
    if($ready){$ready=Select-String -LiteralPath $properties -Pattern ('^version='+[regex]::Escape($entry.Version)+'$') -Quiet}
    if((Test-Path -LiteralPath $library)-and -not $ready){throw 'A different or incomplete 1-Wire dependency exists. Prepare a new SDK; it was not overwritten.'}
    if($ready -and -not(Test-Path -LiteralPath (Join-Path $library $entry.Header))){throw 'The existing 1-Wire library is incomplete. It was preserved.'}
    if(-not $ready){$missing+=$entry}
}
if($missing.Count -gt 0){
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
        $installArgs=@('lib','install','--no-deps')+@($missing|ForEach-Object{$_.Name+'@'+$_.Version})
        & $cli @installArgs
        if($LASTEXITCODE -ne 0){throw 'Pinned 1-Wire dependency installation failed.'}
    }finally{foreach($restoreEntry in $previous.GetEnumerator()){[Environment]::SetEnvironmentVariable($restoreEntry.Key,$restoreEntry.Value)}}
}
foreach($entry in $dependencies){
    $library=Join-Path $RuntimePath ('user/libraries/'+$entry.Folder)
    if(-not(Test-Path -LiteralPath (Join-Path $library $entry.Header)) -or -not(Select-String -LiteralPath (Join-Path $library 'library.properties') -Pattern ('^version='+[regex]::Escape($entry.Version)+'$') -Quiet)){throw 'The 1-Wire dependency is incomplete.'}
}
& (Join-Path $PSScriptRoot 'patch-onewire-timing.ps1') -RuntimePath $RuntimePath
Write-Output 'Pinned offline OneWire 2.3.8 and DallasTemperature 4.0.5 are prepared.'
