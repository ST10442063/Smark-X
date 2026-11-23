<#
    Starts the whole Smart-X stack for development on Windows:
    the gateway API, the virtual device fleet and the Blazor console,
    each in its own window so its log - and the emulator's hotkeys - stay visible.

        .\run-dev.ps1

    Close a window (or press Ctrl+C in it) to stop that process.
#>

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host 'Building the solution...' -ForegroundColor Cyan
dotnet build SmartX.sln --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The build failed - fix the errors above and run this again.' }

$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

function Start-Part([string] $title, [string] $command) {
    Start-Process $shell -WorkingDirectory $PSScriptRoot -ArgumentList @(
        '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = '$title'; $command")
}

Write-Host 'Starting the gateway API on http://localhost:5080 ...' -ForegroundColor Cyan
Start-Part 'Smart-X gateway API' 'dotnet run --project src/SmartX.Api --no-build'

# The emulator provisions itself from the gateway, so wait until the API answers.
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Seconds 1
    try { $healthy = (Invoke-WebRequest http://localhost:5080/api/health -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 }
    catch { $healthy = $false }
} until ($healthy -or (Get-Date) -gt $deadline)

if (-not $healthy) { throw 'The gateway did not answer within a minute - check its window for errors.' }

Write-Host 'Starting the device emulator (hotkeys work in its window) ...' -ForegroundColor Cyan
Start-Part 'Smart-X device emulator' 'dotnet run --project src/SmartX.DeviceEmulator --no-build'

Write-Host 'Starting the operator console on http://localhost:5090 ...' -ForegroundColor Cyan
Start-Part 'Smart-X console' 'dotnet run --project src/SmartX.Client --no-build'

Write-Host ''
Write-Host 'Open http://localhost:5090/stream once the console window says it is listening.' -ForegroundColor Green
Write-Host 'Swagger: http://localhost:5080/swagger'
