param([Parameter(Mandatory=$true)][string]$RuntimePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
foreach($dependency in @(@{Folder='OneWire';Version='2.3.8';Header='OneWire.h'},@{Folder='DallasTemperature';Version='4.0.5';Header='DallasTemperature.h'})){
    $directory=Join-Path $RuntimePath ('user/libraries/'+$dependency.Folder)
    $properties=Join-Path $directory 'library.properties'
    if(-not(Test-Path -LiteralPath $properties) -or -not(Test-Path -LiteralPath (Join-Path $directory $dependency.Header)) -or -not(Select-String -LiteralPath $properties -Pattern ('^version='+[regex]::Escape($dependency.Version)+'$') -Quiet)){
        throw 'Pinned OneWire/DallasTemperature libraries are required before applying the timing correction. Existing files were preserved.'
    }
}
$libraryFile=Join-Path ([IO.Path]::GetFullPath($RuntimePath)) 'user/libraries/OneWire/OneWire.cpp'
$originalHash='A252B02F7F7E8A49F8C96C81ADD003175CE72E7B9C927D9D98514937D2656013'
$patchedHash='282FF26244D8E6518C5474E9E7326DFA326DBCE4FE54116A281D7EC72107C942'
$hash=(Get-FileHash -LiteralPath $libraryFile -Algorithm SHA256).Hash
if($hash -eq $originalHash){
    $source=[IO.File]::ReadAllText($libraryFile,[Text.Encoding]::UTF8)
    $needle='delayMicroseconds(480);'
    if([regex]::Matches($source,[regex]::Escape($needle)).Count -ne 1){throw 'Pinned OneWire reset location differs; source was preserved.'}
    $patched=$source.Replace($needle,'delayMicroseconds(500); // SQL Classroom: AVR GCC 16 timing margin; standard-speed reset.')
    [IO.File]::WriteAllText($libraryFile,$patched,[Text.UTF8Encoding]::new($false))
    if((Get-FileHash -LiteralPath $libraryFile -Algorithm SHA256).Hash -ne $patchedHash){[IO.File]::WriteAllText($libraryFile,$source,[Text.UTF8Encoding]::new($false));throw 'OneWire patch hash differs; original restored.'}
}elseif($hash -ne $patchedHash){throw 'Different OneWire source exists. It was not overwritten; inspect it or prepare a fresh SDK.'}
$marker=Join-Path $RuntimePath 'offline-onewire-libraries-v2.json'
if(-not(Test-Path -LiteralPath $marker)){
    @{version=2;OneWire='2.3.8';DallasTemperature='4.0.5';patch='reset-500us-v1';sourceSha256=$originalHash;patchedSha256=$patchedHash;source='complete pinned libraries including licenses; single reset-pulse margin correction'} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
}
Write-Output 'Verified OneWire 2.3.8 reset-500us-v1 timing correction.'
