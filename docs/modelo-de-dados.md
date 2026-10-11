# Modelo de dados

## Registro do dicionário (`WordEntry`)

| Campo | Tipo | Obrigatório | Observações |
|---|---|:-:|---|
| `id` | string | ✔ | Estável. Importação: `imp-` + SHA-1 de (classe\|português\|rhagho\|n-ésima ocorrência). Novos: GUID. |
| `rhagho` | string | ✔ | Grafia **exatamente como cadastrada** (inclusive `’`/maiúsculas do arquivo original). |
| `portuguese` | string | ✔ | Significado direto. Verbos: infinitivo. |
| `partOfSpeech` | string | ✔ | Classe livre (lista dinâmica: Substantivo, Verbo, Pronome… e qualquer nova). |
| `etymology`, `example` | string | – | |
| `alternativeForms[]` | `{text, note?}` | – | Participam da pesquisa e da tradução Rhagho → Português. |
| `abbreviations[]` | string | – | |
| `synonyms[]` | `{text, acceptedFromAi}` | – | Manuais ou sugeridos pela IA e aceitos por usuário autenticado. |
| `verb` | `{infinitive, base, tense, personSlot?, isInfinitive}` | verbos | `base` = forma sem sufixo. `isInfinitive=false` para formas flexionadas importadas uma a uma. |
| `sortOrder`, `sourceLine` | int | – | Ordem/linha de origem (desempate determinístico entre homógrafos). |
| `createdAt/updatedAt`, `createdBy/updatedBy` | timestamp / uid | ✔ | Auditoria; as Security Rules exigem `updatedBy == auth.uid`. |
| `version` | int | ✔ | Incrementa a cada atualização. |
| `remoteUpdateTime` | string | (local) | `updateTime` do Firestore; **pré-condição** de `update`/`delete` (concorrência otimista). Não é gravado no documento. |

Novos verbos: o usuário informa infinitivo + forma base; as quatro formas (`base`, `base'ta`, `base'vil`, `base'nux`) são **geradas** a cada exibição e nunca digitadas.

## Firestore

```
dictionary/{id}   → campos acima + syncStamp (timestamp de servidor, REQUEST_TIME)
deletions/{id}    → { syncStamp }   marcador deixado ao excluir (no mesmo commit atômico)
meta/dictionary   → { revision: int }   incrementado atomicamente em todo commit de escrita
```

Regras (`firebase/firestore.rules`): leitura pública; escrita só com `request.auth != null`, com validação de formato, `syncStamp == request.time`, autoria (`createdBy` imutável, `updatedBy` = uid), `deletions` só aceita `{syncStamp}` e nunca é apagada, `meta.revision` só sobe de 1 em 1; qualquer outra coleção: negado. Não há `get()`/`exists()` (contariam leituras).

### Sincronização incremental (por quê e como)

O plano Spark dá 50.000 leituras/dia; baixar o dicionário inteiro a cada mudança escalaria mal. Fluxo (`SyncService`):

1. Lê `meta/dictionary` (1 leitura). `revision` igual à local → fim.
2. Cache vazio, sem cursor ou última carga completa com mais de 7 dias → **carga completa** (N leituras) e grava o cursor.
3. Senão → duas consultas (`runQuery`) com `syncStamp > cursor − 15 s`: uma em `dictionary` (inclusões/alterações) e outra em `deletions` (exclusões). Aplica exclusões primeiro, depois inclusões; o cursor só avança.
4. A margem de 15 s evita perder commits concorrentes; reaplicar um documento é idempotente.

Custo: proporcional ao que mudou, não ao tamanho do dicionário. Exclusão de uma palavra recriada depois funciona (a inclusão vence na mesma leva).

## SQLite local (`rhagho.db`, em `%LOCALAPPDATA%`)

| Tabela | Conteúdo |
|---|---|
| `words` | Cópia do dicionário + colunas de pesquisa **normalizadas** (`pt_key`, `rh_key`, `pos_key`, `rh_letters`, `pt_words`) e dados verbais (`verb_*`, `is_inflected`). |
| `rh_keys` | Índice `(key, word_id, words)`: grafia + formas alternativas normalizadas. |
| `alternative_forms`, `synonyms`, `abbreviations` | Filhas de `words` (cascata). |
| `translation_history` | Texto original, direção, resultado, data/hora. **Nunca vai ao Firestore**; máx. 1000 itens. |
| `settings` | Preferências locais (tema) e uso diário da IA (`ai.usage` = `AAAAMMDD:contagem`). |
| `sync_state` | Última `revision`, data da sincronização, **cursor** (maior `syncStamp` aplicado) e data da última carga completa. |
| `import_issues` | Relatório de validação da importação, para revisão posterior. |

Normalização (`TextNormalizer`): minúsculas, sem diacríticos, `’‘ʼ´` → `'`, aspas curvas → `"`, espaços colapsados. É só chave de comparação; o texto original nunca é alterado. A contagem de letras ignora acentos, apóstrofos e espaços.

Esquema versionado por `PRAGMA user_version`; arquivo corrompido é movido para `*.corrupt-<data>` e recriado (o cache é reconstruível pelo Firestore).

## Decisões de arquitetura

- **REST em vez de SDK** (ver README). Leituras sem token (regras públicas); escritas com ID token do Firebase Auth.
- **Sem backend**: Cloud Functions exigem o plano Blaze. A IA é chamada direto do app com a chave pessoal (DPAPI); prompts e sanitização vivem em `Core/Ai`.
- **`revision` + cursor incremental** permitem sincronizar com 1 leitura quando nada mudou e só com o delta quando mudou (cota gratuita do Spark).
- **Escrita remota primeiro, cache depois**: se o Firestore recusar/falhar, o cache não muda e o usuário vê o erro.
- **Regras linguísticas em um serviço** (`IRhaghoLinguisticService`), nunca em ViewModel/XAML; IA só como auxiliar validado.
- **Presentation sem MAUI**: permite testar toda a lógica de tela fora do Windows.
