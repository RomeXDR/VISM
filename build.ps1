# Builds VI Save Manager into "..\VI Save Manager" and puts a shortcut on the Desktop and in the Start menu.
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$hasSdk = (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks 2>$null) -match '^(8|9|10)\.')
if (-not $hasSdk) {
    Write-Host 'Installing the .NET 8 SDK (one time)...' -ForegroundColor Cyan
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
    $env:Path += ';C:\Program Files\dotnet'
}
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'VI Save Manager'
Get-Process VISaveManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
dotnet publish -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }
$exe = Join-Path $out 'VISaveManager.exe'
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    $lnk = $ws.CreateShortcut((Join-Path $dir 'VI Save Manager.lnk'))
    $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $out; $lnk.IconLocation = $exe; $lnk.Save()
}
Write-Host "Built: $exe" -ForegroundColor Green
