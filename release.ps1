# The zip to send to the team: one self-contained, ReadyToRun-compiled VISaveManager.exe (no .NET install needed),
# saved as dist\VISaveManager-<version>.zip. Unzip anywhere and run VISaveManager.exe.
# Run:  powershell -ExecutionPolicy Bypass -File release.ps1
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$version = ([xml](Get-Content VISaveManager.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $PSScriptRoot 'dist\VISaveManager'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish -c Release -r win-x64 -o $out   # single-file, self-contained, ReadyToRun (see the .csproj)
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }
$zip = Join-Path $PSScriptRoot "dist\VISaveManager-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Zip to share: $zip" -ForegroundColor Green
explorer.exe "/select,`"$zip`""
