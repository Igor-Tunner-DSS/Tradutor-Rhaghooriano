# Configuração

## Camadas de configuração (a última vence)

1. `config/appsettings.json` — valores padrão (placeholders `REPLACE_…`); **embutido** no executável.
2. `config/appsettings.Development.json` / `appsettings.Production.json` — conforme o ambiente (`RHAGHO_ENV`; Debug = Development, Release = Production).
3. `config/appsettings.local.json` — **seu** projeto (ignorado pelo git; também embutido se existir no momento do build).
4. `appsettings.local.json` dentro do diretório de dados do aplicativo (`FileSystem.AppDataDirectory`, uma subpasta de `%LOCALAPPDATA%`) — ajuste por máquina sem recompilar.

```json
{
  "Firebase": { "ProjectId": "meu-projeto", "ApiKey": "AIza…", "TimeoutSeconds": 20, "UseEmulators": false },
  "Ai": { "Model": "openrouter/free", "TimeoutSeconds": 45, "MaxRetries": 2, "DailyLimit": 20 }
}
```

> `Firebase.ApiKey` é a **Web API Key** do Firebase: identificador público (não é segredo; a proteção vem das Security Rules). **Não existe** campo para a chave do OpenRouter em nenhum arquivo — ela é pessoal e é digitada no app (botão **IA**); um teste garante que o modelo de configuração não tem esse campo.

## Firebase — plano Spark (gratuito, sem cartão)

1. Console Firebase → *Adicionar projeto* (mantenha o plano **Spark**; não ative o Blaze). Em *Configurações do projeto → Geral* copie o **ID do projeto** e a **Chave de API da Web**.
2. *Build → Authentication → Método de login*: ativar **E-mail/senha**. Em *Usuários*, crie as contas dos administradores (o app não tem cadastro).
3. *Build → Firestore Database*: criar o banco **`(default)`** (o app usa exatamente esse ID; a cota gratuita vale para um banco por projeto e bancos nomeados exigem faturamento), modo produção, qualquer região disponível.
4. Na raiz do repositório: `firebase login`, ajustar `.firebaserc` (ou `firebase use --add`) e
   `firebase deploy --only firestore:rules,firestore:indexes` (funciona no Spark).
5. Não use Cloud Functions, Storage nem Hosting: exigem Blaze e o projeto não precisa deles.

### Cotas diárias do Firestore no Spark

1 GiB de dados e, por dia, 50.000 leituras, 20.000 escritas e 20.000 exclusões (um banco por projeto). O Google também cobra as leituras feitas para avaliar Security Rules (`get()`/`exists()`), por isso as regras do projeto não as usam. O app foi desenhado para ficar muito abaixo disso — veja a tabela no README e [modelo-de-dados.md](modelo-de-dados.md). Dois cuidados seus:

- Edite o dicionário **pelo app**: ele grava o carimbo `syncStamp` e os marcadores de exclusão que a sincronização incremental usa.
- Não altere as Security Rules para usar `get()`/`exists()`: cada uso conta como leitura.

## IA — chave pessoal do OpenRouter

Cada pessoa: openrouter.ai/keys → criar chave (e-mail ou GitHub; sem cartão) → no app, botão **IA** → colar → **Testar e salvar**. A chave é guardada protegida pelo Windows (DPAPI), só naquele computador. Limites dos modelos gratuitos (documentação do OpenRouter na data desta versão): 20 requisições por minuto e 50 por dia por conta; 1.000 por dia depois de comprar US$ 10 em créditos.

`Ai.DailyLimit` (padrão 20) é um freio **local** por computador, dia UTC. Mude-o em `config/appsettings.local.json` se a sua conta permitir mais.

## Desenvolvimento com o emulador

```powershell
firebase emulators:start            # Auth 9099, Firestore 8080, UI 4000
# config/appsettings.local.json → { "Firebase": { "ProjectId": "demo-rhagho", "ApiKey": "fake", "UseEmulators": true } }
powershell -File scripts\run.ps1 -Emulators
```
Crie um usuário na aba *Authentication* da UI do emulador. O emulador do Firestore exige Java.

## Ambientes

`Development` (padrão em Debug) e `Production` (Release) diferem apenas por `Environment` e pelo que você colocar em cada arquivo de ambiente — por exemplo, apontar `Development` para um projeto Firebase de testes.
