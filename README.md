# Rhagho'oriano

Tradutor e dicionário compartilhado para a língua construída **Rhagho'oriano**, em um aplicativo Windows nativo (.NET MAUI).

## Visão geral

O sistema reúne dois módulos:

- **Tradutor** — traduz **Português ⇄ Rhagho'oriano** com um motor local e determinístico, baseado no dicionário e nas regras verbais da língua. Mantém histórico local, sugere palavras semelhantes quando algo não existe no dicionário e permite cadastrar a palavra desconhecida com um clique.
- **Modificação** — administração do dicionário (criar, editar, excluir), importação do dicionário inicial com relatório de validação e revisão dos problemas encontrados.

Principais características:

- Dicionário **compartilhado** entre computadores, com **cache local** em SQLite: consulta e tradução funcionam offline.
- Pesquisa por português, Rhagho'oriano e formas alternativas, **ignorando acentos**; filtro por classe gramatical e ordenação alfabética ou por quantidade de letras.
- Flexão verbal **determinística** (presente, passado, futuro e imperativo), gerada automaticamente ao cadastrar um verbo.
- **IA opcional** como apoio, nunca como autoridade: só é consultada quando o motor local não resolve tudo, e sua resposta é validada.
- Temas **claro e escuro**, com preferência salva localmente.

## Arquitetura

```
┌────────────────────────────── Windows (.NET MAUI) ──────────────────────────────┐
│ Views (XAML) ── ViewModels (Presentation) ── Serviços de plataforma (MAUI)       │
│                      │                                                          │
│                 Core (regras, tradução, importação, serviços)                   │
│                      │                                                          │
│  Infrastructure:  SQLite local │ Firebase Auth/Firestore (REST) │ Cliente IA    │
└──────────────────────┬──────────────────────────────────────────┬───────────────┘
                       │ HTTPS                                    │ HTTPS
        Firebase: Authentication + Firestore                      ▼
        (fonte da verdade do dicionário)                      OpenRouter
```

| Projeto | Responsabilidade |
|---|---|
| `Rhaghoorian.Core` | Modelos, normalização de texto, tokenizador, regras linguísticas (`IRhaghoLinguisticService`), importador, tradução, sugestões, sincronização e prompts de IA. Sem dependências externas. |
| `Rhaghoorian.Infrastructure` | SQLite, acesso ao Firestore e ao Firebase Authentication via REST, cliente de IA, configuração em camadas e composição de injeção de dependência. |
| `Rhaghoorian.Presentation` | Camada MVVM independente de MAUI: ViewModels, comandos assíncronos, tratamento central de erros e coordenação de abertura. |
| `Rhaghoorian.App` | Interface em XAML, temas e serviços específicos de plataforma (diálogos, navegação, armazenamento protegido, seletor de arquivos). |

Princípios: SOLID, injeção de dependência, `async/await` com cancelamento, *nullable reference types*, nenhuma regra linguística em ViewModels ou XAML, e mocks apenas nos testes.

## Estrutura do repositório

```
Rhaghoorian.sln
src/
  Rhaghoorian.Core/             regras, tradução, importação, serviços
  Rhaghoorian.Infrastructure/   SQLite, Firebase (REST), IA, configuração
  Rhaghoorian.Presentation/     ViewModels e coordenação de tela
  Rhaghoorian.App/              aplicativo MAUI (Windows)
tests/
  Rhaghoorian.Core.Tests/
  Rhaghoorian.Infrastructure.Tests/
  Rhaghoorian.Presentation.Tests/
  Rhaghoorian.TestSupport/      fakes e dados de teste (somente testes)
firebase/                       regras do Firestore, índices e testes de regras
config/                         configuração central (appsettings*.json)
data/                           dicionário inicial e regras verbais originais
scripts/                        setup.ps1, run.ps1, validate.ps1
docs/                           documentação técnica
```

## Tecnologias

.NET 9 · .NET MAUI (Windows) · C# · XAML · MVVM · SQLite (`Microsoft.Data.Sqlite`) · Firebase Authentication e Cloud Firestore (API REST) · OpenRouter · xUnit.

## Autenticação e permissões

- Firebase Authentication por **e-mail e senha**; não há cadastro dentro do aplicativo.
- **Visitante**: usa o Tradutor e consulta, pesquisa, filtra e ordena o Dicionário.
- **Usuário autenticado** (todos com permissão administrativa, sem níveis): cria, edita e exclui palavras, importa o dicionário e aceita sugestões de IA.
- A interface apenas oculta botões; a autorização é imposta nos serviços e nas Security Rules do Firestore.
- Senhas não são armazenadas; apenas o *refresh token* da sessão é guardado, protegido pelo DPAPI do Windows.

## Dados e sincronização

- **Firestore** é a fonte compartilhada da verdade; **SQLite** guarda a cópia local, os índices de pesquisa, o histórico de traduções, o tema e o relatório de importação.
- A sincronização é **incremental**: um contador de revisão indica se algo mudou e, quando mudou, apenas as alterações e exclusões são baixadas.
- Escritas exigem conexão e só são confirmadas após o Firestore aceitar; em seguida o cache local é atualizado.
- **Concorrência otimista**: se duas pessoas editam o mesmo registro, a segunda recebe um aviso de conflito em vez de sobrescrever a primeira.
- O **histórico de traduções nunca é enviado** ao Firestore.

Detalhes em [docs/modelo-de-dados.md](docs/modelo-de-dados.md).

## Tradução e regras linguísticas

A fonte oficial das regras verbais é o arquivo *Regra de flexão verbal rhagho'oriano* (`data/`). O verbo possui quatro flexões e **não** flexiona por pessoa, número ou modo; a forma base é o infinitivo e também o presente.

| Tempo | Regra | `amar` → `mor'hk` |
|---|---|---|
| presente | base | `mor'hk` |
| passado | base + `'ta` | `mor'hk'ta` |
| futuro | base + `'vil` | `mor'hk'vil` |
| imperativo | base + `'nux` | `mor'hk'nux` |

Fluxo do tradutor: normalização → tokenização (preserva pontuação, aspas e quebras de linha) → expressões de várias palavras → consulta ao dicionário → regras verbais → resolução de ambiguidades. Palavras inexistentes nunca são inventadas: ficam sinalizadas, recebem sugestões e podem ser adicionadas ao dicionário.

Exemplo: `nuruh hoari'vil naurn ton ido's` → **Eu irei caçar 5 vezes**.

Mais em [docs/linguistica.md](docs/linguistica.md).

## IA opcional

- Cada pessoa usa a **própria chave do OpenRouter**, informada na tela de configurações de IA e guardada apenas no computador dela (DPAPI). Nada é enviado ao Firebase.
- O app só consulta a IA por ação explícita e envia um contexto compacto, apenas com os registros relevantes ao texto.
- As respostas são sanitizadas e validadas: tradução para Rhagho'oriano que contenha palavras fora do dicionário é descartada, e as regras verbais oficiais sempre prevalecem sobre qualquer sugestão.
- Sugestões (sinônimos, conferência de conjugação) só são gravadas por ação de um usuário autenticado.

## Importação do dicionário

O dicionário inicial acompanha o aplicativo e é lido sem descartar nem corrigir nenhuma entrada. A importação gera um relatório de validação (duplicatas, chaves repetidas, aspas e apóstrofos inconsistentes, formas verbais incompletas, registros conflitantes) que fica salvo localmente para revisão posterior em **Modificação → Importação**.

## Compilar e executar

Requisitos: Windows 10/11, .NET 9 SDK e a workload MAUI (`dotnet workload install maui`).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1   # verifica, restaura, compila e testa
powershell -File scripts\run.ps1                              # executa o aplicativo
powershell -File scripts\validate.ps1                         # build + testes + busca por segredos
```

As configurações ficam centralizadas em `config/`; valores específicos de cada instalação ficam em `config/appsettings.local.json`, que é ignorado pelo controle de versão (modelo em `config/appsettings.local.example.json`). Sem essas configurações o aplicativo abre e funciona offline com o dicionário incluído.

## Testes

```powershell
dotnet test tests\Rhaghoorian.Core.Tests
dotnet test tests\Rhaghoorian.Infrastructure.Tests
dotnet test tests\Rhaghoorian.Presentation.Tests
cd firebase\rules-tests; npm install; npm test     # regras do Firestore no emulador (requer firebase-tools e Java)
```

As suítes cobrem normalização de acentos, pesquisa, flexão verbal e reconhecimento de formas flexionadas, tradução de palavras e expressões, palavras inexistentes e sugestões, CRUD, autenticação e proteção de operações administrativas, sincronização incremental, concorrência, importação, histórico, tema, abertura offline e a camada de IA.

## Documentação

| Documento | Conteúdo |
|---|---|
| [docs/modelo-de-dados.md](docs/modelo-de-dados.md) | Registro do dicionário, coleções do Firestore, tabelas do SQLite, normalização, sincronização e decisões de arquitetura. |
| [docs/linguistica.md](docs/linguistica.md) | Regras verbais, fluxo de tradução, tratamento de palavras inexistentes, dados do arquivo inicial e ponto de extensão do alfabeto especial. |

## Extensibilidade

- **Alfabeto especial do Rhagho'oriano**: ainda não implementado. A interface `IRhaghoScript` marca o único ponto onde uma conversão futura deve entrar — na borda da interface —, sem alterar o armazenamento, as regras linguísticas ou a IA.
- **Novas classes gramaticais**: as listas de filtro e de cadastro vêm do próprio banco, sem alterações de código.
