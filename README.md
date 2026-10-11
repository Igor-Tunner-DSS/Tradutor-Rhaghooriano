# Rhagho'oriano — Tradutor e Dicionário (.NET 9 · MAUI · Firebase Spark · SQLite)

Aplicativo Windows nativo (.NET MAUI) para traduzir **Português ⇄ Rhagho'oriano** e administrar o dicionário compartilhado da conlang.

- **Tradutor**: motor local e determinístico (dicionário + regras verbais oficiais). A IA é só apoio, nunca autoridade.
- **Modificação**: CRUD do dicionário (somente usuários autenticados) e importação do dicionário inicial com relatório de validação.
- **Dicionário compartilhado** no Cloud Firestore, com cache local em SQLite (funciona offline para consulta/tradução).
- **100% compatível com o plano gratuito (Spark) do Firebase** — sem cartão, sem Cloud Functions.
- **IA** (`openrouter/free`): cada pessoa usa **a própria chave do OpenRouter**, digitada no app e guardada só no computador dela.

> ## Leia primeiro: o que foi e o que NÃO foi verificado
> Este projeto foi escrito e testado num ambiente Linux sem Windows, sem workload MAUI e sem acesso ao NuGet. Por isso:
>
> | Parte | Estado |
> |---|---|
> | Núcleo: linguística, importador, tradução, sincronização **incremental**, CRUD, autorização, chave/limite de IA | **Compilado e testado** (122 testes) |
> | SQLite, Firestore (REST), Firebase Auth (REST), cliente OpenRouter, configuração, injeção de dependência | **Compilado e testado** (57 testes), com HTTP simulado; SQLite executado de verdade sobre a `libsqlite3` |
> | ViewModels (toda a lógica de tela, incl. Configurações de IA) | **Compilado e testado** (35 testes) |
> | **Projeto MAUI** (`src/Rhaghoorian.App`: XAML, tema, serviços de plataforma Windows) | **NÃO foi compilado nem executado.** Só foi checado que o XML é válido e que cada `{Binding}`, estilo e recurso referenciado existe. **Espere corrigir alguns detalhes no primeiro build no Windows.** |
> | **Firebase real** (Auth, Firestore, Security Rules) e **OpenRouter real** | **Nunca exercitados**: não havia credenciais. As regras e os testes de regras (`firebase/rules-tests`) existem, mas não rodaram no emulador. |
>
> Os testes foram executados com o SDK .NET 8 (cópia com `net8.0`), um executor mínimo compatível com xUnit e um provedor SQLite de verificação sobre a `libsqlite3` (o NuGet estava bloqueado). Os projetos entregues usam **xUnit 2.9**, **Microsoft.Data.Sqlite 9.0** e `net9.0`; a primeira execução real é o `scripts\setup.ps1`. Versões de pacote foram escolhidas sem poder consultar o NuGet; se um `restore` reclamar, ajuste o número.

## Arquitetura

```
┌────────────────────────────── Windows (.NET MAUI) ──────────────────────────────┐
│ Views (XAML) ── ViewModels (Presentation) ── Serviços de plataforma (MAUI)       │
│                      │                                                          │
│                 Core (regras, tradução, importação, serviços)                   │
│                      │                                                          │
│  Infrastructure:  SQLite local │ Firebase Auth/Firestore (REST) │ Cliente IA    │
└──────────────────────┬──────────────────────────────────────────┬───────────────┘
                       │ HTTPS                                    │ HTTPS, chave pessoal (DPAPI)
        Firebase Spark: Authentication + Firestore                ▼
        (fonte da verdade do dicionário)                OpenRouter · openrouter/free
```

| Projeto | Papel |
|---|---|
| `src/Rhaghoorian.Core` | Modelos, normalização, tokenizador, **`IRhaghoLinguisticService`** (regras verbais), importador, tradução, sugestões, sincronização, prompts e limites de IA. Sem dependências externas. |
| `src/Rhaghoorian.Infrastructure` | SQLite, Firestore/Auth via REST, cliente OpenRouter, configuração em camadas, composição de DI. |
| `src/Rhaghoorian.Presentation` | MVVM sem MAUI: ViewModels, comandos assíncronos, tratamento central de erros, coordenação de abertura (isolado para ser testável). |
| `src/Rhaghoorian.App` | Somente XAML, estilos/temas e serviços de plataforma (diálogos, navegação, DPAPI, tema, seletor de arquivo, abrir URL). |
| `firebase/` + `firebase.json` | Regras do Firestore, índices (nenhum necessário), emulador (Auth + Firestore). |
| `tests/` | `Core.Tests`, `Infrastructure.Tests` (SQLite, Firestore/Auth/IA, composição de DI), `Presentation.Tests`, `TestSupport` (fakes, só para testes). |
| `config/` · `data/` · `scripts/` · `docs/` | Configuração central, dicionário e regras originais, PowerShell, documentação. |

**Por que REST e não o SDK do Firebase?** Não existe SDK oficial do Firestore/Auth para .NET MAUI. Auth usa Identity Toolkit REST; Firestore usa a API REST v1 (leituras públicas, escritas com ID token, `commit` atômico com pré-condição `updateTime` para concorrência otimista).

## Compatibilidade com o plano Spark (gratuito)

Usado: **Authentication (e-mail/senha)** e **Cloud Firestore** (banco `(default)`). **Não usado**: Cloud Functions, Cloud Storage, Hosting — por isso não há backend e a IA é chamada direto do app com a chave de cada pessoa.

Cotas diárias do Firestore no Spark (página de preços do Google; confira se mudaram): 1 GiB armazenado, **50.000 leituras, 20.000 escritas e 20.000 exclusões por dia**, para um único banco por projeto (o app usa o `(default)`). Como o app respeita isso:

| Custo | Como é mantido baixo |
|---|---|
| Leituras | Sincronização **incremental**: se a revisão não mudou, lê **1 documento** (`meta/dictionary`). Se mudou, lê só o que foi alterado/excluído desde o cursor (carimbo de servidor `syncStamp` + marcadores em `deletions/`). Carga completa só na 1ª vez, com cache vazio, ou como rede de segurança a cada 7 dias. As Security Rules **não** usam `get()/exists()` (cada uma custaria uma leitura extra). |
| Escritas | Editar = 2 escritas (registro + contador de revisão); excluir = 1 exclusão + 2 escritas; importar 870 registros ≈ 870 + 3 escritas e 870 leituras (verificação de existência), uma única vez. |
| Estourou a cota | O Firestore recusa até a cota renovar (não cobra). O app mostra "cota diária gratuita atingida", mantém o cache e continua traduzindo/consultando offline. |

Ordem de grandeza: 10 pessoas abrindo o app 5 vezes ao dia gastam ~50 leituras de revisão + os documentos alterados; a carga completa semanal de cada máquina (~870 leituras, crescendo com o dicionário) fica em ~1.200/dia na média. Larga folga sob 50.000. Detalhes em [docs/modelo-de-dados.md](docs/modelo-de-dados.md).

## Instalação rápida (Windows 10/11)

Pré-requisitos: .NET 9 SDK, Visual Studio 2022 17.12+ (ou `dotnet workload install maui`) e, para publicar as regras do Firestore, Node.js + `npm i -g firebase-tools`.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1     # verifica, restaura, compila, testa, valida configuração
powershell -File scripts\run.ps1                                # executa o aplicativo
powershell -File scripts\validate.ps1                           # revalidação rápida (build + testes + busca de segredos)
```

Sem configurar o Firebase o aplicativo **abre e funciona offline** com o dicionário incluído (870 entradas): Tradutor, pesquisa, filtros, ordenação, histórico e temas. Login, escrita e sincronização exigem o passo abaixo; a IA exige a chave pessoal.

## Configuração externa (depende de VOCÊ — nada disto foi feito por mim)

Passo a passo em [docs/configuracao.md](docs/configuracao.md). Resumo:

1. Criar um projeto Firebase (**plano Spark, sem cartão**); ativar **Authentication (E-mail/senha)** e **Cloud Firestore** (banco `(default)`, modo produção). Criar as contas dos administradores no console (não há cadastro no app).
2. Copiar `config/appsettings.local.example.json` → `config/appsettings.local.json` (ignorado pelo git) com `Firebase.ProjectId` e `Firebase.ApiKey` (chave Web — identificador público).
3. `.firebaserc` com o ID do projeto; `firebase deploy --only firestore:rules,firestore:indexes`.
4. No app, entrar e usar **Modificação → Importação → Analisar → Publicar** (cria no Firestore só o que ainda não existe).
5. **Cada pessoa** que quiser IA: botão **IA** → seguir as instruções → colar a própria chave do OpenRouter.

## IA: a chave de cada pessoa

- Botão **IA** na barra de título: ajuda para criar a chave em openrouter.ai/keys, campo para colar, **Testar e salvar** (confere com o OpenRouter via `GET /key`, que não gasta consulta), **Remover chave** e contador de uso.
- A chave fica **só no computador** da pessoa, protegida pelo **DPAPI do Windows**; nunca vai ao Firebase, a arquivos de configuração, a logs, nem aparece na tela (só `sk-or-…abcd`). Sem chave, a IA fica desativada e o resto funciona.
- Chamada direta ao OpenRouter, modelo `openrouter/free`, timeout (45 s), cancelamento, até 2 *retries* com *backoff* para 408/429/5xx/rede/resposta vazia, validação e sanitização da resposta (`Tradução:`, cercas de código, `<think>`).
- **Limite local de 20 consultas/dia** por computador (dia UTC, como o OpenRouter conta; configurável em `Ai.DailyLimit`). O limite real é o da conta OpenRouter: segundo a documentação deles, modelos `:free` têm 20 req/min e 50/dia por conta (1.000/dia após comprar US$ 10 em créditos); saldo negativo devolve 402. O app traduz esses casos em mensagens claras.
- Contexto **compacto** (só os registros das palavras do texto, no máximo 80) e só quando você clica em **Refinar com IA** — botão que só aparece se o motor local deixou palavras sem registro ou ambiguidade real. Português → Rhagho'oriano via IA é **descartado** se contiver palavra fora do dicionário. A conjugação da IA só é conferida; as regras oficiais prevalecem.
- Sugestões da IA (sinônimos, conferência de conjugação) só entram no formulário e só são gravadas se um usuário autenticado salvar. Esses recursos exigem login (cadastro) **e** chave pessoal.

## Autenticação e autorização

- Firebase Authentication, **somente e-mail e senha**; todas as contas são administradoras (sem níveis).
- Visitante: Tradutor, ver/pesquisar/filtrar/ordenar o Dicionário. Autenticado: criar, editar, excluir, aceitar sugestões de IA, importar.
- **A interface apenas esconde botões.** A autorização real está em dois lugares: `DictionaryService` (recusa sem sessão) e as Security Rules do Firestore (escrita só com `request.auth`, com validação de formato e autoria).
- A senha é enviada uma única vez (TLS) ao Firebase e apagada da memória do formulário; só o *refresh token* é guardado, protegido pelo **DPAPI do Windows**.

## Firestore, SQLite e sincronização

- `dictionary/{id}` (um documento por registro), `deletions/{id}` (marcadores de exclusão) e `meta/dictionary` (`revision`, incrementado em todo commit de escrita).
- Abertura do app: SQLite → dados locais (carga inicial se vazio) → conexão → restaura sessão → compara `revision` (1 leitura) → incremental/completa → libera.
- Offline: consulta e tradução usam o cache; **escritas exigem conexão** e só são dadas como concluídas após o Firestore confirmar (depois atualizam o SQLite).
- Edição simultânea: `update`/`delete` usam `currentDocument.updateTime`; se outra pessoa alterou antes, aparece o aviso de conflito com opção de recarregar — nada é sobrescrito em silêncio.
- **Histórico de traduções, tema, uso da IA e relatório de importação ficam só no SQLite local.** O dicionário deve ser editado pelo app: documentos criados à mão no console não têm `syncStamp` e só chegam aos outros computadores na carga completa semanal.

## Importação do dicionário inicial

O arquivo "Dicionário rhagho'oriano atual" está embutido no app (`data/`) e é lido por `DictionaryFileParser` sem descartar nem corrigir nada. O relatório de validação fica salvo localmente e pode ser revisto em **Modificação → Importação**. Resultado real: [docs/relatorio-importacao.md](docs/relatorio-importacao.md) (870 entradas, 0 erros, 177 avisos, 94 informações — incluindo **6 blocos verbais inteiros duplicados** no arquivo).

## Regras linguísticas (fonte oficial: "Regra de flexão verbal rhagho'oriano")

Quatro flexões; o verbo **não** flexiona por pessoa/número/modo; a base é o infinitivo e também o presente.

| Tempo | Regra | `amar` (`mor'hk`) |
|---|---|---|
| presente | base | `mor'hk` |
| passado | base + `'ta` | `mor'hk'ta` |
| futuro | base + `'vil` | `mor'hk'vil` |
| imperativo | base + `'nux` | `mor'hk'nux` |

Implementado de forma determinística em `RhaghoLinguisticService` (a IA nunca decide sufixos). Frase de referência, testada: `nuruh hoari'vil naurn ton ido's` → **Eu irei caçar 5 vezes**. Mais em [docs/linguistica.md](docs/linguistica.md).

## Testes

```powershell
dotnet test tests\Rhaghoorian.Core.Tests            # 122
dotnet test tests\Rhaghoorian.Infrastructure.Tests  # 57  (SQLite real, Firestore/Auth/OpenRouter simulados, composição de DI)
dotnet test tests\Rhaghoorian.Presentation.Tests    # 35
cd firebase\rules-tests; npm i; npm test            # regras no emulador (requer firebase-tools + Java) — NÃO executado por mim
```

Cobrem: normalização de acentos, pesquisa, as quatro flexões e o reconhecimento de formas flexionadas, tradução palavra a palavra e de expressões, palavras inexistentes e sugestões, CRUD, autenticação, **sincronização incremental (só o delta é lido, exclusões, carga semanal, cota esgotada)**, proteção das operações administrativas, concorrência, importação/relatório, histórico, tema, startup offline, **IA com chave pessoal (formato, máscara, limite local, retries, 401/402/429, saída restrita)**.

## Solução de problemas

| Sintoma | Causa provável / ação |
|---|---|
| "Dicionário online ainda não inicializado" | Firestore vazio: entre e use **Modificação → Importação → Publicar**. |
| "O Firebase ainda não foi configurado" | `config/appsettings.local.json` ausente ou com `REPLACE_…`. |
| Login: "E-mail ou senha incorretos" | Conta não existe no Firebase Authentication (criada no console) ou senha errada. |
| Erro 403 ao salvar | Regras não publicadas ou sessão de outro projeto. `firebase deploy --only firestore:rules`. |
| "A cota diária gratuita do Firebase foi atingida" | Limite do Spark (ver tabela). Consulta/tradução seguem offline; tente de novo depois. |
| IA: "Configure sua chave" | Botão **IA** → colar a chave pessoal. |
| IA: "chave recusada" | Chave copiada incompleta ou revogada: crie outra em openrouter.ai/keys. |
| IA: "limite de N consultas hoje" | Freio local (`Ai.DailyLimit`, dia UTC). |
| IA: erro 402 / 429 | Saldo negativo na conta OpenRouter / limite de modelos gratuitos (por minuto ou por dia): aguarde ou adicione créditos. |
| "Banco local ilegível" | O arquivo foi movido para `rhagho.db.corrupt-AAAAMMDDHHMMSS` e recriado; o cache é refeito pela sincronização. |
| Build do App falha em `WindowsAppSDKSelfContained` | Remova `WindowsAppSDKSelfContained`, `SelfContained` e `RuntimeIdentifier` do `.csproj` e instale o *Windows App Runtime*. |

## Limitações conhecidas

- **Projeto MAUI não compilado** (ver aviso no topo); layout/estilo podem precisar de ajustes visuais.
- **Firestore real nunca testado**: o `commit` com máscara vazia + `increment`/`REQUEST_TIME` e a regra `syncStamp == request.time` seguem a documentação, mas não foram exercitados no servidor ou no emulador. Se a regra do `syncStamp` recusar escritas, é o primeiro ponto a investigar.
- Visitantes sem conta **podem** usar a IA no Tradutor (basta uma chave pessoal); cadastro de palavras com IA exige login.
- O limite local de IA é por computador; a mesma chave em dois computadores soma no OpenRouter, mas cada app conta o seu.
- Conjugação **portuguesa** para formas não cadastradas só cobre verbos **regulares**. Irregulares (ser, ir, ter…) dependem de estarem listados no dicionário (as do arquivo inicial estão).
- Ambiguidade de pessoa/tempo em Português → Rhagho'oriano (ex.: "amamos") escolhe a primeira leitura (presente) e mostra as outras.
- `openrouter/free` roteia entre modelos gratuitos: qualidade/latência variam e podem falhar; nada depende dele.
- Alfabeto especial **não** implementado (apenas o ponto de extensão `IRhaghoScript`). Autenticação só por e-mail/senha; sem recuperação de senha no app.
