# Relatório de validação do dicionário inicial

Gerado pelo importador (`DictionaryFileParser`) sobre o arquivo **"Dicionário rhagho'oriano atual"** enviado, sem alterar nenhum valor.

**870 entradas** importadas. Problemas apontados: **0 erros · 177 avisos · 94 informações**. Nenhuma entrada foi descartada ou corrigida.

## Entradas por classe

| Classe | Entradas |
|---|---:|
| Verbo | 639 |
| Substantivo | 82 |
| Pronome | 40 |
| Adjetivo | 32 |
| Advérbio | 22 |
| Número | 17 |
| Preposição | 16 |
| Artigo | 8 |
| Outros | 7 |
| Gíria | 7 |

## Problemas por tipo

| Código | Ocorrências | O que significa |
|---|---:|---|
| `AcentuacaoMista` | 1 | Chaves portuguesas ora com acento, ora sem. |
| `ApostrofoTipografico` | 12 | Grafia Rhagho'oriana com apóstrofo curvo (’) em vez de reto ('). |
| `AspasTipograficas` | 30 | Linha escrita com aspas curvas “ ” em vez de aspas retas. |
| `CaixaIncomum` | 10 | Grafia Rhagho'oriana começando com maiúscula (ex.: "Aki"). |
| `ChaveRepetidaConflitante` | 7 | Mesma palavra portuguesa com valores Rhagho'orianos diferentes (homônimos entre classes ou conflito real). |
| `ChaveRepetidaVerbal` | 27 | Mesma forma verbal portuguesa servindo a mais de um tempo (ex.: "amamos" presente/passado). Esperado pela regra oficial. |
| `DirecaoInvertida` | 14 | Seção NÚMEROS escrita como "rhagho" : "número" (inversa do resto do arquivo). |
| `DuplicataExata` | 122 | Mesma entrada (classe + português + Rhagho) repetida. Todas as cópias foram mantidas. |
| `FormaVerbalIncompleta` | 3 | Bloco verbal com quantidade de formas diferente do padrão. |
| `RhaghoRepetido` | 41 | Mesma grafia Rhagho'oriana usada para vários significados (todas as pessoas do presente etc.; fora verbos). |
| `SecaoTodo` | 1 | Cabeçalho "# TODO: GPT bulk grammar" no meio dos verbos. |
| `TituloIgnorado` | 1 | Primeira linha do arquivo (título), não é entrada. |
| `VerboSemInfinitivo` | 2 | Formas flexionadas sem uma linha de infinitivo correspondente. |

## Principais achados para revisão humana

### Chaves portuguesas com valores diferentes (`ChaveRepetidaConflitante`)

- A chave portuguesa "que" aparece com valores diferentes: linha 39: zuf (Pronome); linha 1012: nah (Outros).
- A chave portuguesa "a" aparece com valores diferentes: linha 47: mark (Artigo); linha 993: a (Preposição).
- A chave portuguesa "caca" aparece com valores diferentes: linha 64: karik (Substantivo); linha 900: naurn (Verbo); linha 919: naurn'nux (Verbo).
- A chave portuguesa "erro" aparece com valores diferentes: linha 101: bnibnir (Substantivo); linha 270: bnibnir'hk (Verbo).
- A chave portuguesa "tempo" aparece com valores diferentes: linha 120: cris (Substantivo); linha 125: cris'kan (Substantivo).
- A chave portuguesa "e" aparece com valores diferentes: linha 162: gharf (Verbo); linha 992: ni (Preposição).
- A chave portuguesa "para" aparece com valores diferentes: linha 351: lohar (Verbo); linha 995: tou (Preposição).

### Blocos verbais fora do padrão (`FormaVerbalIncompleta`, `VerboSemInfinitivo`)

- (linha 924) Presente de "adorar" (alamor) tem 2 linhas; o padrão é infinitivo + 6 pessoas = 7.
- (linha 927) Presente de "querer" (uan) tem 2 linhas; o padrão é infinitivo + 6 pessoas = 7.
- (linha 894) Bloco de Imperative com base "nor'mor'hk" tem 2 linhas (esperado: 3 ou 4).
- (linha 263) Formas de Imperative com base "ableari" não têm um infinitivo correspondente no dicionário.
- (linha 397) Formas de Imperative com base "jalrh" não têm um infinitivo correspondente no dicionário.

### Outros pontos

- (linha 923) Cabeçalho de pendência "TODO: GPT bulk grammar": a classe gramatical atual (Verbo) foi mantida para as entradas seguintes.
- **Blocos verbais inteiros aparecem duas vezes** no arquivo: *amar* (linhas 429–453 e 820–844), *voltar* (535–558 e 668–691), *correr* (562–585 e 693–716), *andar* (589–612 e 718–741), *fazer* (616–639 e 743–766) e *contar* (643–666 e 768–791), além das linhas soltas *distrais* (243/246), *tem* (378/381) e *vem* (485/488). Todas as cópias foram importadas e marcadas como `DuplicataExata` (122 ocorrências); a lista do Dicionário mostrará, por exemplo, dois "amar" até que uma cópia seja excluída pela interface.
- A conjugação de **"ir"** no arquivo usa formas regulares (`iro, iras, ira…`) e o passado `iri…`; foram importadas como estão. A regra Rhagho'oriana não é afetada (todas apontam para a base `hoari`).
- Entradas com **apóstrofo curvo (’)** foram preservadas como escritas; a comparação/pesquisa trata ’ e ' como o mesmo caractere, e a flexão ('ta, 'vil, 'nux) usa o apóstrofo reto.
- A linha `“dormir” : “gahal”,` (aspas curvas) foi lida normalmente e sinalizada.

> A lista completa (cerca de 280 itens) fica no SQLite local e pode ser filtrada por gravidade em **Modificação → Importação**.
