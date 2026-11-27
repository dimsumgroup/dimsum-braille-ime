param(
    [string] $RimeSource = (Split-Path $PSScriptRoot -Parent),
    [string] $EngineInstaller,
    [string] $SevenZip = '7z',
    [string] $NsisCompiler = 'makensis',
    [string] $InnoCompiler,
    [string] $SigningThumbprint,
    [string] $SignTool = 'signtool.exe'
)
$ErrorActionPreference = 'Stop'
$manager = Join-Path $PSScriptRoot 'dist\manager'

New-Item -ItemType Directory -Force -Path $manager | Out-Null

if (!$EngineInstaller) {
    $EngineInstaller = Join-Path $PSScriptRoot 'dist\engine-0.17.4.exe'
    if (!(Test-Path $EngineInstaller)) {
        Invoke-WebRequest -Uri 'https://github.com/rime/weasel/releases/download/0.17.4/weasel-0.17.4.0-installer.exe' -OutFile $EngineInstaller
    }
}
$assetDirectory = Join-Path $manager 'assets'
python (Join-Path $PSScriptRoot 'prepare-assets.py') $RimeSource $assetDirectory `
    --engine $EngineInstaller --seven-zip $SevenZip --nsis $NsisCompiler
if ($LASTEXITCODE -ne 0) { throw 'Asset preparation failed' }

function Sign-File([string] $path) {
    & $SignTool sign /sha1 $SigningThumbprint /fd sha256 /tr https://timestamp.digicert.com /td sha256 $path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $path" }
    & $SignTool verify /pa $path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $path" }
}

$slimEngine = Join-Path $assetDirectory 'weasel-installer.exe'
if ($SigningThumbprint) { Sign-File $slimEngine }
$engineHash = (Get-FileHash -Algorithm SHA256 $slimEngine).Hash.ToLowerInvariant()
dotnet publish (Join-Path $PSScriptRoot 'Manager') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    "-p:BundledEngineSha256=$engineHash" -o $manager
if ($LASTEXITCODE -ne 0) { throw 'Manager build failed' }
if ($SigningThumbprint) { Sign-File (Join-Path $manager 'DimsumManager.exe') }

if ($InnoCompiler) {
    & $InnoCompiler (Join-Path $PSScriptRoot 'installer\dimsum.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    if ($SigningThumbprint) { Sign-File (Join-Path $PSScriptRoot 'dist\Dimsum_Setup_v1.3.1.exe') }
}
