<#
.SYNOPSIS
  Prepara o projeto Rhagho'oriano em uma máquina Windows: verifica ferramentas, restaura, compila, testa e valida a configuração.
.DESCRIPTION
  Não finge que Firebase/OpenRouter estão configurados: o que depender do SEU projeto Firebase é apenas verificado e
  reportado (veja a seção "Pendências externas" no final). Use -SkipTests / -SkipApp / -SkipBackend para pular etapas.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipApp,
    [switch]$SkipBackend
)

$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $Root
$pending = New-Object System.Collections.Generic.List[string]
$step = 0
function Step($text) { $script:step++; Write-Host "`n[$script:step] $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "    OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    !!  $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "    ERRO $text" -ForegroundColor Red; exit 1 }
function Run($cmd, $arguments) {
    & $cmd @arguments
    if ($LASTEXITCODE -ne 0) { Fail "'$cmd $($arguments -join ' ')' falhou (código $LASTEXITCODE)." }
}

# 1) .NET 9 -------------------------------------------------------------------------------------------------------
Step 'Verificando .NET 9 SDK'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet não encontrado. Instale o .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0' }
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -notmatch '(?m)^9\.') { Fail ".NET 9 SDK não instalado. SDKs encontrados:`n$sdks" }
Ok ".NET SDK: $((& dotnet --version))"

# 2) Dependências -------------------------------------------------------------------------------------------------
Step 'Verificando dependências'
if (-not $SkipApp) {
    $workloads = (& dotnet workload list) -join "`n"
    if ($workloads -notmatch 'maui') {
        Warn 'Workload MAUI ausente. Instalando (pode pedir permissão de administrador)…'
        Run 'dotnet' @('workload', 'install', 'maui')
    }
    Ok 'Workload MAUI disponível'
}
if (-not $SkipBackend) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail 'Node.js não encontrado (necessário para o backend de IA). Instale o Node 22 LTS: https://nodejs.org' }
    $nodeMajor = [int]((& node --version).TrimStart('v').Split('.')[0])
    if ($nodeMajor -lt 22) { Warn "Node $((& node --version)) encontrado; o backend declara Node 22. Atualize antes de publicar as Functions." }
    Ok "Node $((& node --version))"
}
if (-not (Get-Command firebase -ErrorAction SilentlyContinue)) {
    Warn 'firebase-tools não encontrado (npm install -g firebase-tools). Necessário só para publicar regras/functions e usar o emulador.'
    $pending.Add('Instalar firebase-tools (npm install -g firebase-tools) e executar "firebase login".')
} else { Ok "firebase-tools $((& firebase --version))" }

# 3) Restaurar ----------------------------------------------------------------------------------------------------
Step 'Restaurando pacotes .NET'
if ($SkipApp) {
    foreach ($p in 'src/Rhaghoorian.Core', 'src/Rhaghoorian.Infrastructure', 'src/Rhaghoorian.Presentation',
                   'tests/Rhaghoorian.Core.Tests', 'tests/Rhaghoorian.Infrastructure.Tests', 'tests/Rhaghoorian.Presentation.Tests') { Run 'dotnet' @('restore', $p) }
} else { Run 'dotnet' @('restore', 'Rhaghoorian.sln') }
Ok 'Restore concluído'

# 4) Backend ------------------------------------------------------------------------------------------------------
if (-not $SkipBackend) {
    Step 'Restaurando, compilando e testando o backend de IA (Cloud Functions)'
    Push-Location 'backend/functions'
    try {
        Run 'npm' @('install', '--no-audit', '--no-fund')
        Run 'npm' @('run', 'build')
        if (-not $SkipTests) { Run 'npm' @('test') }
    } finally { Pop-Location }
    Ok 'Backend compilado' 
}

# 5) Compilar -----------------------------------------------------------------------------------------------------
Step 'Compilando a solução'
if ($SkipApp) {
    foreach ($p in 'src/Rhaghoorian.Core', 'src/Rhaghoorian.Infrastructure', 'src/Rhaghoorian.Presentation') { Run 'dotnet' @('build', $p, '--no-restore', '-c', 'Debug') }
} else { Run 'dotnet' @('build', 'Rhaghoorian.sln', '--no-restore', '-c', 'Debug') }
Ok 'Compilação concluída'

# 6) Testes -------------------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Step 'Executando testes (.NET)'
    foreach ($t in 'Core', 'Infrastructure', 'Presentation') {
        Run 'dotnet' @('test', "tests/Rhaghoorian.$t.Tests", '--no-build', '--nologo', '-v', 'minimal')
    }
    Ok 'Todos os testes .NET passaram'
}

# 7) Configuração -------------------------------------------------------------------------------------------------
Step 'Verificando configurações'
$cfg = Get-Content 'config/appsettings.json' -Raw | ConvertFrom-Json
$localPath = 'config/appsettings.local.json'
if (Test-Path $localPath) {
    $local = Get-Content $localPath -Raw | ConvertFrom-Json
    if ($local.Firebase.ProjectId) { $cfg.Firebase.ProjectId = $local.Firebase.ProjectId }
    if ($local.Firebase.ApiKey)    { $cfg.Firebase.ApiKey    = $local.Firebase.ApiKey }
    if ($local.Ai.Endpoint)        { $cfg.Ai.Endpoint        = $local.Ai.Endpoint }
    if ($local.Firebase.UseEmulators) { $cfg.Firebase.UseEmulators = $true }
}
$placeholders = @($cfg.Firebase.ProjectId, $cfg.Firebase.ApiKey, $cfg.Ai.Endpoint) | Where-Object { $_ -like 'REPLACE*' }
if ($placeholders.Count -gt 0 -and -not $cfg.Firebase.UseEmulators) {
    Warn 'Firebase/IA ainda NÃO configurados (valores REPLACE_…). O app abre e funciona offline com o dicionário incluído, mas login, sincronização e IA ficam indisponíveis.'
    $pending.Add('Criar o projeto Firebase e preencher config/appsettings.local.json (copie config/appsettings.local.example.json). Veja docs/configuracao.md.')
} else { Ok 'Firebase e endpoint de IA preenchidos' }

if ((Get-ChildItem -Recurse -File -Include *.cs,*.xaml,*.json,*.ts -Path src, config, backend/functions/src -ErrorAction SilentlyContinue |
     Select-String -Pattern 'sk-or-v1-[A-Za-z0-9]{20,}' -List).Count -gt 0) {
    Fail 'Possível chave do OpenRouter encontrada em arquivos do repositório! Remova-a e use o Secret Manager.'
} else { Ok 'Nenhuma chave do OpenRouter nos arquivos do projeto' }
$pending.Add('Guardar a chave do OpenRouter no backend: firebase functions:secrets:set OPENROUTER_API_KEY (nunca no app).')

# 8) Banco local --------------------------------------------------------------------------------------------------
Step 'Preparando o banco local'
Ok 'O SQLite é criado/atualizado automaticamente na primeira execução em %LOCALAPPDATA% (a carga inicial vem do dicionário incluído no aplicativo).'

# 9) Estrutura do Firebase ----------------------------------------------------------------------------------------
Step 'Validando a estrutura do Firebase'
foreach ($f in 'firebase.json', 'firebase/firestore.rules', 'firebase/firestore.indexes.json', 'backend/functions/package.json') {
    if (Test-Path $f) { Ok $f } else { Fail "Arquivo ausente: $f" }
}
$rc = Get-Content '.firebaserc' -Raw | ConvertFrom-Json
if ($rc.projects.default -like 'REPLACE*') {
    Warn '.firebaserc ainda aponta para REPLACE_WITH_YOUR_FIREBASE_PROJECT_ID.'
    $pending.Add('Editar .firebaserc (ou "firebase use --add") com o ID do seu projeto e publicar: firebase deploy --only firestore:rules,firestore:indexes,functions')
}

# Resumo ----------------------------------------------------------------------------------------------------------
Write-Host "`n================ Pronto ================" -ForegroundColor Green
Write-Host 'Executar o aplicativo:  powershell -File scripts\run.ps1'
if ($pending.Count -gt 0) {
    Write-Host "`nPendências externas (dependem das SUAS credenciais; nada disso foi feito por este script):" -ForegroundColor Yellow
    $i = 1; foreach ($p in $pending) { Write-Host "  $i. $p"; $i++ }
}
