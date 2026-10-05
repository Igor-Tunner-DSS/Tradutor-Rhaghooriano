# Rhagho'oriano — Tradutor e Dicionário (.NET 9 · MAUI · Firebase · SQLite)

Aplicativo Windows nativo (.NET MAUI) para traduzir **Português ⇄ Rhagho'oriano** e administrar o dicionário compartilhado da conlang.

- **Tradutor**: motor local e determinístico (dicionário + regras verbais oficiais). A IA é só apoio, nunca autoridade.
- **Modificação**: CRUD do dicionário (somente usuários autenticados) e importação do dicionário inicial com relatório de validação.
- **Dicionário compartilhado** no Cloud Firestore, com cache local em SQLite (funciona offline para consulta/tradução).
- **IA** (`openrouter/free`) atrás de um backend seguro (Cloud Function): a chave do OpenRouter **nunca** está no aplicativo.

> ## Leia primeiro: o que foi e o que NÃO foi verificado
> Este projeto foi escrito e testado num ambiente Linux sem Windows, sem workload MAUI e sem acesso ao NuGet. Por isso:
>
> | Parte | Estado |
> |---|---|
> | Núcleo linguístico, importador, tradução, sincronização, CRUD, autorização, IA (validação) | **Compilado e testado** (106 testes) |
> | SQLite, Firestore (REST), Firebase Auth (REST), cliente de IA, configuração, injeção de dependência | **Compilado e testado** (40 testes), com HTTP simulado; SQLite executado de verdade sobre a `libsqlite3` |
> | ViewModels (toda a lógica de tela) | **Compilado e testado** (29 testes) |
> | Backend de IA (Cloud Functions) | **Compilado e testado** (23 testes; `openrouter` simulado) |
> | **Projeto MAUI** (`src/Rhaghoorian.App`: XAML, tema, serviços de plataforma Windows) | **NÃO foi compilado nem executado.** Só foi checado que o XML é válido, que cada `{Binding}` existe num ViewModel e que estilos/recursos referenciados existem. **Espere ter de corrigir alguns detalhes no primeiro build no Windows.** |
> | **Firebase real** (Auth, Firestore, Security Rules, Functions) e **OpenRouter real** | **Nunca exercitados**: não havia credenciais. Regras e testes de regras (`firebase/rules-tests`) existem, mas não rodaram no emulador. |
>
> Detalhes técnicos da verificação: os testes foram executados com o SDK .NET 8 (arquivos `.csproj` com TFM trocado para `net8.0` numa cópia), com um executor mínimo compatível com xUnit e um provedor SQLite de verificação sobre a `libsqlite3` (o NuGet estava bloqueado). Os projetos entregues usam **xUnit 2.9**, **Microsoft.Data.Sqlite 9.0** e `net9.0`; a primeira execução real é o `scripts\setup.ps1`.
> Versões de pacotes foram escolhidas sem poder consultá-las no NuGet; se algum `restore` reclamar, ajuste o número da versão.

## Arquitetura

```
┌───────────────────────────── Windows (.NET MAUI) ─────────────────────────────┐
│ Views (XAML) ── ViewModels (Presentation) ── Serviços de plataforma (MAUI)    │
│                      │                                                        │
│                 Core (regras, tradução, importação, serviços)                 │
│                      │                                                        │
│   Infrastructure:  SQLite local │ Firebase Auth/Firestore (REST) │ Cliente IA │
└──────────────────────┬──────────────────────────────┬─────────────────────────┘
                       │                              │ HTTPS + (ID token opcional)
          Cloud Firestore (fonte da verdade)    Cloud Function "ai"  ← segredo OPENROUTER_API_KEY
          + Firebase Authentication                   │
                                                      ▼
                                          OpenRouter · modelo openrouter/free
```

| Projeto | Papel |
|---|---|
| `src/Rhaghoorian.Core` | Modelos, normalização, tokenizador, **`IRhaghoLinguisticService`** (regras verbais), importador, tradução, sugestões, serviços (CRUD/sync/import/IA). Sem dependências externas. |
| `src/Rhaghoorian.Infrastructure` | SQLite, Firestore/Auth via REST, cliente do backend de IA, configuração em camadas, composição de DI. |
| `src/Rhaghoorian.Presentation` | MVVM sem MAUI: ViewModels, comandos assíncronos, tratamento central de erros, coordenação de abertura. (Isolado para ser testável.) |
| `src/Rhaghoorian.App` | Somente XAML, estilos/temas e serviços de plataforma (diálogos, navegação, DPAPI, tema, seletor de arquivo). |
| `backend/functions` | Cloud Function (Node 22/TypeScript): valida usuário, limita uso, monta o prompt, chama o OpenRouter. |
| `firebase/` + `firebase.json` | Regras do Firestore, índices/TTL, emulador. |
| `tests/` | `Core.Tests`, `Infrastructure.Tests` (inclui composição de DI e SQLite), `Presentation.Tests`, `TestSupport` (fakes, só para testes). |
| `config/` · `data/` · `scripts/` · `docs/` | Configuração central, dicionário e regras originais, PowerShell, documentação. |

**Por que REST e não o SDK do Firebase?** Não existe SDK oficial do Firestore/Auth para .NET MAUI (o `Google.Cloud.Firestore` usa gRPC de servidor e não é suportado em MAUI). Auth usa Identity Toolkit REST; Firestore usa a API REST v1 (leituras públicas, escritas com ID token, `commit` atômico com pré-condição `updateTime` para concorrência otimista). Decisão detalhada em [docs/modelo-de-dados.md](docs/modelo-de-dados.md).

## Instalação rápida (Windows 10/11)

Pré-requisitos: .NET 9 SDK, Node.js 22 LTS, Visual Studio 2022 17.12+ (ou `dotnet workload install maui`), e — para publicar regras/functions — `npm i -g firebase-tools`.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1     # verifica, restaura, compila, testa, valida configuração
powershell -File scripts\run.ps1                                # executa o aplicativo
powershell -File scripts\validate.ps1                           # revalidação rápida (build + testes + busca de segredos)
```

Sem configurar o Firebase o aplicativo **abre e funciona offline** com o dicionário incluído (870 entradas): Tradutor, pesquisa, filtros, ordenação, histórico e temas. Login, sincronização, escrita e IA exigem o passo abaixo.

## Configuração externa (depende de VOCÊ — nada disto foi feito por mim)

Passo a passo completo em [docs/configuracao.md](docs/configuracao.md). Resumo:

1. Criar um projeto Firebase; ativar **Authentication (E-mail/senha)** e **Cloud Firestore** (modo produção). Criar as contas dos administradores no console (não há cadastro no app).
2. Copiar `config/appsettings.local.example.json` → `config/appsettings.local.json` (ignorado pelo git) com `Firebase.ProjectId`, `Firebase.ApiKey` (chave Web — identificador público) e `Ai.Endpoint`.
3. `.firebaserc` com o ID do projeto; `firebase deploy --only firestore:rules,firestore:indexes`.
4. `firebase functions:secrets:set OPENROUTER_API_KEY` e `firebase deploy --only functions`. Copiar a URL da função `ai` para `Ai.Endpoint`.
5. Abrir o app, entrar, **Modificação → Importação → Analisar → Publicar** (cria no Firestore só o que ainda não existe).

## Autenticação e autorização

- Firebase Authentication, **somente e-mail e senha**; todas as contas são administradoras (sem níveis).
- Visitante: Tradutor, ver/pesquisar/filtrar/ordenar o Dicionário. Autenticado: criar, editar, excluir, aceitar sugestões de IA, importar.
- **A interface apenas esconde botões.** A autorização real está em três lugares: `DictionaryService` (recusa sem sessão), Security Rules do Firestore e a Cloud Function (rotas de cadastro exigem ID token válido).
- A senha é enviada uma única vez (TLS) ao Firebase e apagada da memória do formulário; só o *refresh token* é guardado, protegido pelo **DPAPI do Windows** (`DpapiTokenStore`).

## Firestore, SQLite e sincronização

- Coleção `dictionary/{id}` (um documento por registro) e `meta/dictionary` com um contador `revision` incrementado a cada escrita (no mesmo `commit`).
- Abertura do app: SQLite → verifica dados locais (carga inicial se vazio) → conexão → restaura sessão → compara `revision` (1 leitura) → baixa tudo só se mudou → libera.
- Offline: consulta e tradução usam o cache; **escritas exigem conexão** e só são dadas como concluídas após o Firestore confirmar (depois atualizam o SQLite).
- Edição simultânea: `update`/`delete` usam `currentDocument.updateTime`; se outra pessoa alterou antes, aparece o aviso de conflito com opção de recarregar — nada é sobrescrito em silêncio.
- **Histórico de traduções, tema e relatório de importação ficam só no SQLite local.**

## Importação do dicionário inicial

O arquivo "Dicionário rhagho'oriano atual" está embutido no app (`data/`) e é lido por `DictionaryFileParser` sem descartar nem corrigir nada. O relatório de validação (duplicatas, chaves repetidas, aspas/apóstrofos, formas verbais incompletas, seção `TODO`, números invertidos…) fica salvo localmente e pode ser revisto em **Modificação → Importação**. Resultado real: [docs/relatorio-importacao.md](docs/relatorio-importacao.md) (870 entradas, 0 erros, 177 avisos, 94 informações — incluindo **6 blocos verbais inteiros duplicados** no arquivo).

## Regras linguísticas (fonte oficial: "Regra de flexão verbal rhagho'oriano")

Quatro flexões; o verbo **não** flexiona por pessoa/número/modo; a base é o infinitivo e também o presente.

| Tempo | Regra | `amar` (`mor'hk`) |
|---|---|---|
| presente | base | `mor'hk` |
| passado | base + `'ta` | `mor'hk'ta` |
| futuro | base + `'vil` | `mor'hk'vil` |
| imperativo | base + `'nux` | `mor'hk'nux` |

Implementado de forma determinística em `RhaghoLinguisticService` (a IA nunca decide sufixos). Frase de referência, testada: `nuruh hoari'vil naurn ton ido's` → **Eu irei caçar 5 vezes**. Mais em [docs/linguistica.md](docs/linguistica.md).

## IA e backend

- `openrouter/free`, chamado só pela função `ai` (`backend/functions`), com timeout (40 s), cancelamento (cliente desconectou), até 2 *retries* com *backoff* para 429/5xx/rede/resposta vazia, validação da resposta, **limite de ~20 usos/dia** por identidade (uid ou hash do IP) e rajada de 6/min, logs sem segredos nem texto do usuário.
- O app envia contexto **compacto** (só os registros das palavras do texto, no máximo 80), nunca o dicionário inteiro, e só quando o usuário clica em **Refinar com IA** — e o botão só aparece se o motor local deixou palavras sem registro ou ambiguidade real.
- Saídas: tradução → só o texto; conjugação → 4 linhas; sinônimos → lista. A resposta é sanitizada (`Tradução:`, cercas de código, `<think>`) e **Português → Rhagho'oriano via IA é descartado se contiver palavra fora do dicionário**.
- Sugestões da IA (sinônimos, conferência de conjugação) só entram no formulário e só são gravadas se um usuário autenticado salvar.

## Testes

```powershell
dotnet test tests\Rhaghoorian.Core.Tests            # 106
dotnet test tests\Rhaghoorian.Infrastructure.Tests  # 40  (SQLite real, Firestore/Auth/IA simulados, composição de DI)
dotnet test tests\Rhaghoorian.Presentation.Tests    # 29
cd backend\functions; npm test                      # 23
cd firebase\rules-tests; npm i; npm test            # regras no emulador (requer firebase-tools + Java) — NÃO executado por mim
```

Cobrem: normalização de acentos, pesquisa (com/sem acento, forma alternativa), as quatro flexões e o reconhecimento de formas flexionadas, tradução palavra a palavra e de expressões, palavras inexistentes e sugestões, CRUD, autenticação, sincronização, proteção das operações administrativas, concorrência, importação/relatório, histórico, tema, startup offline, IA (sanitização/validação/limites).

## Solução de problemas

| Sintoma | Causa provável / ação |
|---|---|
| Barra mostra "Dicionário online ainda não inicializado" | Firestore vazio: entre e use **Modificação → Importação → Publicar**. |
| "O Firebase ainda não foi configurado" | `config/appsettings.local.json` ausente ou com `REPLACE_…`. |
| Login: "E-mail ou senha incorretos" | Conta não existe no Firebase Authentication (criada no console) ou senha errada. |
| Erro 403 ao salvar | Regras não publicadas ou sessão de outro projeto. `firebase deploy --only firestore:rules`. |
| IA: "indisponível" / "limite diário" | Segredo não configurado, função não publicada, OpenRouter fora do ar, ou 20 usos do dia esgotados. A tradução local continua. |
| "Banco local ilegível" | O arquivo foi movido para `rhagho.db.corrupt-AAAAMMDDHHMMSS` e recriado; o cache é refeito pela sincronização (o histórico antigo fica no arquivo preservado). |
| Build do App falha em `WindowsAppSDKSelfContained` | Remova essas três propriedades do `.csproj` (`WindowsAppSDKSelfContained`, `SelfContained`, `RuntimeIdentifier`) e instale o *Windows App Runtime*. |
| Pesquisa não acha palavra com `’` | A comparação trata `’` e `'` como iguais; se não achar, confira a classe/filtros ativos. |

## Limitações conhecidas

- **Projeto MAUI não compilado** (ver aviso no topo); layout/estilo podem precisar de ajustes visuais.
- Conjugação **portuguesa** para formas não cadastradas só cobre verbos **regulares** (com as alternâncias ç/c/qu/g/gu/j). Formas irregulares (ser, ir, ter…) dependem de estarem listadas individualmente no dicionário (as do arquivo inicial estão).
- Ambiguidade de pessoa/tempo em Português → Rhagho'oriano (ex.: "amamos") escolhe a primeira leitura (presente) e mostra as outras; o desempate por IA é manual.
- Limite de IA para visitantes é por IP (IP compartilhado divide a cota); dia = UTC. Ative a política TTL no campo `expiresAt` da coleção `rateLimits` (declarada em `firestore.indexes.json`).
- `openrouter/free` é um roteador de modelos gratuitos: qualidade/latência variam e podem falhar; por isso nada depende dele.
- Firestore REST: o `commit` com máscara vazia + `increment` (usado para o contador `revision`) segue a documentação, mas **não foi testado contra o servidor real**.
- Alfabeto especial **não** implementado (apenas o ponto de extensão `IRhaghoScript`, identidade). Autenticação é só e-mail/senha; sem recuperação de senha no app.
