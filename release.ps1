# The zip to send to the team: a NativeAOT build (no .NET install needed on their PC),
# saved as dist\VISaveManager-<version>.zip. Unzip anywhere and run VISaveManager.exe.
# Run:  powershell -ExecutionPolicy Bypass -File release.ps1
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$version = ([xml](Get-Content VISaveManager.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $PSScriptRoot 'dist\win-x64-nativeaot'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish -c Release -r win-x64 -p:PublishAot=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }
$zip = Join-Path $PSScriptRoot "dist\VISaveManager-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$files = Get-ChildItem -LiteralPath $out | Where-Object Extension -ne '.pdb'
Compress-Archive -LiteralPath $files.FullName -DestinationPath $zip
Write-Host "Zip to share: $zip" -ForegroundColor Green
explorer.exe "/select,`"$zip`""
