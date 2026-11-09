param([string]$Compiler)

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
dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $payload
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Compiler (Join-Path $PSScriptRoot 'APEXChatd.iss')
exit $LASTEXITCODE
