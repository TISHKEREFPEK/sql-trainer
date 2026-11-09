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
if(-not $CheckPrerequisitesOnly){foreach($app in @('Teacher','Student')){if(-not(Test-Path -LiteralPath (Join-Path $publish "$app/Classroom.$app.exe"))){throw "Published $app executable is missing."}}}
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
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach($app in @('Teacher','Student')){
    & $iscc '/Q' ('/DPublishRoot='+$publish) ('/O'+$output) (Join-Path $root "installer/$($app.ToLowerInvariant()).iss")
    if($LASTEXITCODE -ne 0){throw "Installer compilation failed: $app"}
}
Write-Output "Installers built in $output. These commands do not install or launch them."
