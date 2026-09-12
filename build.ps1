param([string]$DotNet = "dotnet")
$ErrorActionPreference = "Stop"
& $DotNet build (Join-Path $PSScriptRoot "MissFisherIceScheduler.csproj") -c Release -p:Platform=x64 -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "bin\x64\Release\MissFisherIceScheduler\latest.zip") -Destination (Join-Path $PSScriptRoot "latest.zip") -Force
Write-Host "Package: $(Join-Path $PSScriptRoot 'latest.zip')"
