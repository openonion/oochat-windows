$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'ConnectOnion.WinUIClient\ConnectOnion.WinUIClient.csproj'

dotnet run --project $projectPath --configuration Release -p:Platform=x64 -p:RunUnpackaged=true
exit $LASTEXITCODE
