param([Parameter(Mandatory=$true)][string]$PublishPath,[string]$InnoCompilerPath,[string]$OutputPath,[switch]$CheckPrerequisitesOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
$publish=[IO.Path]::GetFullPath($PublishPath)
if(-not $OutputPath){$OutputPath=Join-Path $root 'artifacts/installers'}
$output=[IO.Path]::GetFullPath($OutputPath).TrimEnd('\','/')
$allowed=@((Join-Path $root 'artifacts'),(Join-Path (Split-Path $root -Parent) '.codex')) | ForEach-Object {([IO.Path]::GetFullPath($_)).TrimEnd('\','/')+'\'}
if(-not($allowed | Where-Object {$output.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)})){throw 'Installer output must be under this repository artifacts or .codex folder.'}
if($output -eq $publish -or $publish.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($publish.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Installer output and published application folders must be separate.'}
if(Test-Path -LiteralPath $output){if(Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1){throw 'Installer output is not empty. Choose a new OutputPath; prior installers were preserved.'}}
function Is-Inno7([string]$file){
    if(-not(Test-Path -LiteralPath $file -PathType Leaf)){return $false}
    # Inno 7 console binaries may have a zero VERSIONINFO resource.
    $text=(& $file --version 2>$null | Out-String).Trim()
    return $LASTEXITCODE -eq 0 -and $text -match '^([7-9]|[1-9][0-9]+)\.[0-9]+\.[0-9]+$'
}
$iscc=$InnoCompilerPath
if($iscc){$iscc=[IO.Path]::GetFullPath($iscc);if(-not(Is-Inno7 $iscc)){throw 'The specified compiler must be Inno Setup 7 or newer.'}}
else{
    $candidates=@("$env:ProgramFiles\Inno Setup 7\ISCC.exe","${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe")
    $command=Get-Command ISCC.exe -ErrorAction SilentlyContinue;if($command){$candidates+=@($command.Source)}
    $iscc=$candidates | Where-Object {Is-Inno7 $_} | Select-Object -First 1
}
if(-not $iscc){throw 'Inno Setup 7 or newer is required for SDK long paths. Provide -InnoCompilerPath.'}
if($CheckPrerequisitesOnly){Write-Output 'Installer prerequisites passed; no files were created.';return}

$entryPoints=@('Teacher/Classroom.Teacher','Student/Classroom.Student',
    'Teacher/runtime/server/Classroom.Server','Teacher/runtime/worker/Classroom.Worker')
$publishedFiles=foreach($entry in $entryPoints){
    foreach($extension in @('exe','dll')){
        $relativePath="$entry.$extension"
        $path=Join-Path $publish $relativePath
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Published application file is missing: $relativePath"}
        $versionInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        [ordered]@{path=$relativePath;productVersion=$versionInfo.ProductVersion;fileVersion=$versionInfo.FileVersion;
            sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
}
$productVersions=@($publishedFiles.productVersion | Select-Object -Unique)
$fileVersions=@($publishedFiles.fileVersion | Select-Object -Unique)
if($productVersions.Count -ne 1 -or $productVersions[0] -notmatch '^(?<public>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)(?:\+[0-9A-Za-z.-]+)?$' -or
    $fileVersions.Count -ne 1 -or $fileVersions[0] -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'){
    throw 'Teacher, Student, Server and Worker must come from a single version and source revision.'
}
$appVersion=($productVersions[0] -split '\+',2)[0]
$fileVersion=$fileVersions[0]
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installers=@()
foreach($app in @('Teacher','Student')){
    & $iscc '/Q' ('/DPublishRoot='+$publish) ('/DAppVersion='+$appVersion) ('/DAppFileVersion='+$fileVersion) ('/O'+$output) (Join-Path $root "installer/$($app.ToLowerInvariant()).iss")
    if($LASTEXITCODE -ne 0){throw "Installer compilation failed: $app"}
    $installer=Get-Item -LiteralPath (Join-Path $output "SQL-$app-$appVersion-win-x64.exe")
    $installers+=@{role=$app;path=$installer.Name;bytes=$installer.Length;sha256=(Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash}
}
[ordered]@{schemaVersion=1;appVersion=$appVersion;productVersion=$productVersions[0];fileVersion=$fileVersion;
    applications=@($publishedFiles);installers=$installers} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $output 'package-receipt.json') -Encoding UTF8
Write-Output "Installers built in $output. These commands do not install or launch them."
