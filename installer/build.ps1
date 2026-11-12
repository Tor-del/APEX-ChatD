param([string]$Compiler, [string]$PluginApprovalPublicKey, [string]$PluginSigningKey, [string]$EdgePluginZip)

$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $Compiler) {
        $Compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Pass -Compiler with the path to ISCC.exe.'
}

$project = Join-Path $PSScriptRoot '..\TwitchChatAgent.csproj'
$payload = Join-Path $PSScriptRoot '..\bin\InstallerPayload'
$publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $payload)
if ($PluginApprovalPublicKey) {
    $publishArguments += "-p:PluginApprovalPublicKey=$((Resolve-Path -LiteralPath $PluginApprovalPublicKey).Path)"
}
$publicKeyPath = if ($PluginApprovalPublicKey) { $PluginApprovalPublicKey }
    else { Join-Path $PSScriptRoot '..\Assets\plugin-approval-public.pem' }
if ($PluginSigningKey) {
    $privateKey = [Security.Cryptography.RSA]::Create()
    $publicKey = [Security.Cryptography.RSA]::Create()
    try {
        $privateKey.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PluginSigningKey).Path))
        $publicKey.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $publicKeyPath).Path))
        if ($privateKey.ExportSubjectPublicKeyInfoPem() -cne $publicKey.ExportSubjectPublicKeyInfoPem()) {
            throw 'The signing key does not match the public key embedded in APEX Chatd.'
        }
    } finally {
        $privateKey.Dispose()
        $publicKey.Dispose()
    }
}
$packageRoot = Join-Path $PSScriptRoot "..\bin\PluginPackages\$([Guid]::NewGuid().ToString('N'))"
$edgePayload = $null
if ($EdgePluginZip) {
    $sdk = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\PluginSdk\Chatd.PluginSdk.csproj') -Raw)
    $app = [xml](Get-Content -LiteralPath $project -Raw)
    $edgePayload = Join-Path $packageRoot 'EdgeTts'
    & (Join-Path $PSScriptRoot 'Import-EdgePlugin.ps1') -Zip $EdgePluginZip -Destination $edgePayload `
        -PublicKey $publicKeyPath -ApiVersion $sdk.Project.PropertyGroup.Version -AppVersion $app.Project.PropertyGroup.Version
    $edgePayload = (Resolve-Path -LiteralPath $edgePayload).Path
}
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packageBuilder = Join-Path $PSScriptRoot '..\Plugins\build-package.ps1'
$updaterPayload = Join-Path $packageRoot 'GitHubUpdater'
& $packageBuilder -Project (Join-Path $PSScriptRoot '..\Plugins\GitHubUpdater\GitHubUpdater.csproj') -OutputDirectory $updaterPayload -SigningKey $PluginSigningKey

$updaterPayload = (Resolve-Path -LiteralPath $updaterPayload).Path
$compilerArguments = @("/DUpdaterPayload=$updaterPayload")
if ($edgePayload) { $compilerArguments += "/DEdgePayload=$edgePayload" }
$compilerArguments += Join-Path $PSScriptRoot 'APEXChatd.iss'
& $Compiler @compilerArguments
exit $LASTEXITCODE
