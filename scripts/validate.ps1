<#
.SYNOPSIS  Verificação rápida (sem alterar nada): compila, roda todos os testes e confere arquivos/segredos.
#>
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $Root
$failed = $false
function Check($name, [scriptblock]$block) {
    Write-Host "-> $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { Write-Host "   FALHOU" -ForegroundColor Red; $script:failed = $true } else { Write-Host "   ok" -ForegroundColor Green }
}

Check 'dotnet build'   { dotnet build Rhaghoorian.sln -c Debug --nologo -v q }
Check 'testes Core'    { dotnet test tests/Rhaghoorian.Core.Tests --no-build --nologo -v q }
Check 'testes Infra'   { dotnet test tests/Rhaghoorian.Infrastructure.Tests --no-build --nologo -v q }
Check 'testes Apresentação' { dotnet test tests/Rhaghoorian.Presentation.Tests --no-build --nologo -v q }

Write-Host '-> segredos' -ForegroundColor Cyan
$hits = Get-ChildItem -Recurse -File -Path src, config, firebase -ErrorAction SilentlyContinue |
        Select-String -Pattern 'sk-or-v1-[A-Za-z0-9]{20,}|OPENROUTER_API_KEY\s*=\s*["''][^"'']+' -List
if ($hits) { Write-Host '   POSSÍVEL SEGREDO em:' -ForegroundColor Red; $hits | ForEach-Object { "   $($_.Path)" }; $failed = $true } else { Write-Host '   ok' -ForegroundColor Green }

if ($failed) { Write-Host "`nValidação FALHOU." -ForegroundColor Red; exit 1 }
Write-Host "`nValidação OK." -ForegroundColor Green
