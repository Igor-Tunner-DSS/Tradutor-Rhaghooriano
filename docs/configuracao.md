# Configuração

## Camadas de configuração (a última vence)

1. `config/appsettings.json` — valores padrão (placeholders `REPLACE_…`); **embutido** no executável.
2. `config/appsettings.Development.json` / `appsettings.Production.json` — conforme o ambiente (`RHAGHO_ENV`; Debug = Development, Release = Production).
3. `config/appsettings.local.json` — **seu** projeto (ignorado pelo git; também embutido se existir no momento do build).
4. `appsettings.local.json` dentro do diretório de dados do aplicativo (`FileSystem.AppDataDirectory`, uma subpasta de `%LOCALAPPDATA%`) — ajuste por máquina sem recompilar.

```json
{
  "Firebase": { "ProjectId": "meu-projeto", "ApiKey": "AIza…", "TimeoutSeconds": 20, "UseEmulators": false },
  "Ai": { "Endpoint": "https://us-central1-meu-projeto.cloudfunctions.net/ai", "TimeoutSeconds": 60 }
}
```

> `Firebase.ApiKey` é a **Web API Key** do Firebase: identificador público (não é segredo; a proteção vem das Security Rules). **Não existe** campo para a chave do OpenRouter no app — um teste garante isso.

## Firebase — passo a passo

1. Console Firebase → *Adicionar projeto*. Em *Configurações do projeto → Geral* copie o **ID do projeto** e a **Chave de API da Web**.
2. *Build → Authentication → Método de login*: ativar **E-mail/senha**. Em *Usuários*, crie as contas dos administradores (o app não tem cadastro).
3. *Build → Firestore Database*: criar banco (modo produção, qualquer região).
4. Na raiz do repositório: `firebase login`, ajustar `.firebaserc` (ou `firebase use --add`) e
   `firebase deploy --only firestore:rules,firestore:indexes`.
5. Ativar a política **TTL** do campo `expiresAt` da coleção `rateLimits` (Console → Firestore → TTL) — o `firestore.indexes.json` já a declara.

## OpenRouter / backend de IA

1. Crie uma chave em openrouter.ai (a chave **só** vai ao Secret Manager do Firebase):
   `firebase functions:secrets:set OPENROUTER_API_KEY`
2. `firebase deploy --only functions` (o *predeploy* instala e compila). Requer plano **Blaze** do Firebase (Functions v2 + Secret Manager); o uso esperado (~20 chamadas/dia/usuário) cabe no nível gratuito.
3. Copie a URL da função `ai` (saída do deploy) para `Ai.Endpoint`. Rotas: `POST …/ai/translate | conjugate | synonyms`.
4. Variáveis opcionais do backend: `DAILY_LIMIT` (padrão 20), `BURST_PER_MINUTE` (padrão 6).

## Desenvolvimento com o emulador

```powershell
firebase emulators:start            # Auth 9099, Firestore 8080, Functions 5001, UI 4000
# config/appsettings.local.json → { "Firebase": { "ProjectId": "demo-rhagho", "ApiKey": "fake", "UseEmulators": true } }
powershell -File scripts\run.ps1 -Emulators
```
Para a função no emulador, coloque `OPENROUTER_API_KEY=…` em `backend/functions/.secret.local` (ignorado pelo git). Crie um usuário na aba *Authentication* da UI do emulador.

## Ambientes

`Development` (padrão em Debug) e `Production` (Release) diferem apenas por `Environment` e pelo que você colocar em cada arquivo de ambiente — por exemplo, apontar `Development` para um projeto Firebase de testes.
