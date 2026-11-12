param(
    [Parameter(Mandatory)][string]$Zip,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$PublicKey,
    [Parameter(Mandatory)][version]$ApiVersion,
    [Parameter(Mandatory)][version]$AppVersion
)

$ErrorActionPreference = 'Stop'
$zipPath = (Resolve-Path -LiteralPath $Zip).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $output)
$manifestPath = Join-Path $output 'plugin.json'
$signaturePath = Join-Path $output 'plugin.sig'
$data = [IO.File]::ReadAllBytes($manifestPath)
$signature = [IO.File]::ReadAllBytes($signaturePath)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublicKey).Path))
    if (-not $rsa.VerifyData($data, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw 'The Edge TTS package signature does not match the APEX ChatD public key.'
    }
} finally { $rsa.Dispose() }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.id -cne 'apexchatd.edge-tts' -or $manifest.files -isnot [System.Collections.IDictionary] -or
    $manifest.entryAssembly -cne 'ApexChatd.EdgeTts.dll' -or -not $manifest.files.Contains($manifest.entryAssembly)) {
    throw 'The ZIP is not an Edge TTS plugin package.'
}
if ($ApiVersion -lt [version]$manifest.minApiVersion -or $ApiVersion -ge [version]$manifest.maxApiVersionExclusive -or
    ($manifest.minAppVersion -and $AppVersion -lt [version]$manifest.minAppVersion)) {
    throw "Edge TTS $($manifest.version) is incompatible with APEX ChatD $AppVersion (plugin API $ApiVersion)."
}

$matched = 0
foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
    if ($relative -in @('plugin.json', 'plugin.sig')) { continue }
    if (-not $manifest.files.Contains($relative) -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $manifest.files[$relative]) {
        throw "Unexpected or changed Edge TTS package file: $relative"
    }
    $matched++
}
if ($matched -ne $manifest.files.Count) { throw 'Some Edge TTS package files are missing.' }
Write-Output "Verified Edge TTS package $($manifest.version)."
