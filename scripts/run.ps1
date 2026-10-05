<#
.SYNOPSIS  Executa o aplicativo Rhagho'oriano (Windows, .NET MAUI).
.PARAMETER Environment  Development (padrão) ou Production.
.PARAMETER Emulators    Usa o Firebase Emulator Suite local (inicie antes: firebase emulators:start).
#>
[CmdletBinding()]
param(
    [ValidateSet('Development', 'Production')][string]$Environment = 'Development',
    [switch]$Emulators
)
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $Root

$env:RHAGHO_ENV = $Environment
if ($Emulators) {
    Write-Host 'Dica: com -Emulators, defina "Firebase.UseEmulators": true em config/appsettings.local.json.' -ForegroundColor Yellow
}
$configuration = if ($Environment -eq 'Production') { 'Release' } else { 'Debug' }
& dotnet run --project src/Rhaghoorian.App -f net9.0-windows10.0.19041.0 -c $configuration
exit $LASTEXITCODE
