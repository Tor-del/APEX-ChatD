param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Zip,
    [string]$SigningKey
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$projectDirectory = Split-Path -Parent $projectPath
$manifest = Get-Content -LiteralPath (Join-Path $projectDirectory 'plugin.json') -Raw | ConvertFrom-Json
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Choose a new output directory: $outputPath"
}

dotnet publish $projectPath -c Release --no-self-contained -p:DebugType=None -p:DebugSymbols=false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed ($LASTEXITCODE)." }

$sdk = Join-Path $outputPath 'Chatd.PluginSdk.dll'
if (Test-Path -LiteralPath $sdk) { Remove-Item -LiteralPath $sdk }

$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $outputPath -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($outputPath.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
if (-not $hashes.Contains($manifest.entryAssembly)) { throw 'The entry assembly is missing from the output.' }
$manifest | Add-Member -NotePropertyName files -NotePropertyValue $hashes -Force
$manifestPath = Join-Path $outputPath 'plugin.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10))

if ($SigningKey) {
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $SigningKey).Path))
        $signature = $rsa.SignData([IO.File]::ReadAllBytes($manifestPath),
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [IO.File]::WriteAllBytes((Join-Path $outputPath 'plugin.sig'), $signature)
    } finally {
        $rsa.Dispose()
    }
}

if ($Zip) {
    $zipPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Zip)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($zipPath))
    [IO.Compression.ZipFile]::CreateFromDirectory($outputPath, $zipPath)
}
