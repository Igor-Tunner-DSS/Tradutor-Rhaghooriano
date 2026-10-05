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
dictionary/{id}      → campos acima (tipos Firestore: stringValue, integerValue, timestampValue, arrayValue/mapValue…)
meta/dictionary      → { revision: int }   (incrementado atomicamente em todo commit de escrita)
rateLimits/{dia}_{hash} → { count, day, updatedAt, expiresAt }   (somente Admin SDK; TTL em expiresAt)
```

Regras (`firebase/firestore.rules`): leitura pública de `dictionary` e `meta`; escrita só com `request.auth != null`, com validação de formato, autoria (`createdBy` imutável, `updatedBy` = uid) e `meta.revision` só pode subir de 1 em 1; `rateLimits` e qualquer outra coleção: negado.

## SQLite local (`rhagho.db`, em `%LOCALAPPDATA%`)

| Tabela | Conteúdo |
|---|---|
| `words` | Cópia do dicionário + colunas de pesquisa **normalizadas** (`pt_key`, `rh_key`, `pos_key`, `rh_letters`, `pt_words`) e dados verbais (`verb_*`, `is_inflected`). |
| `rh_keys` | Índice `(key, word_id, words)`: grafia + formas alternativas normalizadas. |
| `alternative_forms`, `synonyms`, `abbreviations` | Filhas de `words` (cascata). |
| `translation_history` | Texto original, direção, resultado, data/hora. **Nunca vai ao Firestore**; máx. 1000 itens. |
| `settings` | Preferências locais (tema). |
| `sync_state` | Última `revision` baixada e data da sincronização. |
| `import_issues` | Relatório de validação da importação, para revisão posterior. |

Normalização (`TextNormalizer`): minúsculas, sem diacríticos, `’‘ʼ´` → `'`, aspas curvas → `"`, espaços colapsados. É só chave de comparação; o texto original nunca é alterado. A contagem de letras ignora acentos, apóstrofos e espaços.

Esquema versionado por `PRAGMA user_version`; arquivo corrompido é movido para `*.corrupt-<data>` e recriado (o cache é reconstruível pelo Firestore).

## Decisões de arquitetura

- **REST em vez de SDK** (ver README). Leituras sem token (regras públicas); escritas com ID token do Firebase Auth.
- **`revision`** permite sincronizar com 1 leitura quando nada mudou, em vez de baixar ~870 documentos.
- **Escrita remota primeiro, cache depois**: se o Firestore recusar/falhar, o cache não muda e o usuário vê o erro.
- **Regras linguísticas em um serviço** (`IRhaghoLinguisticService`), nunca em ViewModel/XAML; IA só como auxiliar validado.
- **Presentation sem MAUI**: permite testar toda a lógica de tela fora do Windows.
