# Setup
$ErrorActionPreference = "Stop"

if (-not $env:Configuration) { $env:Configuration = "Release" }

if (-not $env:PackageVersion) { $env:PackageVersion = (gitversion | ConvertFrom-Json).NuGetVersionV2 }

if ($env:BuildRunner) { & ./tools/dotnet-install.ps1 -Version 1.0.0-rc4-004771 -Architecture x86 }

function Invoke-BuildStep { param([scriptblock]$cmd) & $cmd; if ($LASTEXITCODE -ne 0) { exit 1 } }

# Restore
Invoke-BuildStep { dotnet restore src/DataLoader/DataLoader.csproj }
Invoke-BuildStep { dotnet restore test/DataLoader.Tests/DataLoader.Tests.csproj }
Invoke-BuildStep { dotnet build src/DataLoader/DataLoader.csproj }
Invoke-BuildStep { dotnet build test/DataLoader.Tests/DataLoader.Tests.csproj --no-dependencies }
Invoke-BuildStep { dotnet test test/DataLoader.Tests/DataLoader.Tests.csproj --no-build }
Invoke-BuildStep { dotnet pack src/DataLoader/DataLoader.csproj --include-symbols --no-build }

# End
exit
