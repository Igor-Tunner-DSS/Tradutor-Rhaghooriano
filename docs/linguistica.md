# Regras linguísticas implementadas

Fonte oficial: *Regra de flexão verbal rhagho'oriano* (`data/regra_flexao_verbal_rhagho_oriano.txt`; um teste lê esse arquivo e confere cada exemplo contra o gerador).

## Flexão (determinística — `RhaghoLinguisticService`)

- `Inflect(base, tempo)`; `Conjugate(base)`; `StripInflectionSuffix(forma)` remove `'ta`, `'vil` ou `'nux` (aceita `’`).
- Reconhecimento: `hoari'vil` → base `hoari`, **futuro**, infinitivo associado **ir** — mesmo que `hoari'vil` não esteja cadastrado individualmente.
- Cadastro: a forma base **não pode** terminar em `'ta/'vil/'nux`; as quatro formas são geradas.

## Tradução (`TranslationService`)

1. Normalização e tokenização que preserva pontuação, aspas, quebras de linha e caixa (maiúscula inicial da origem é respeitada).
2. Expressões de várias palavras primeiro (maior para menor), ex.: `até nunca mais` → `fushkarou'sar`.
3. Consulta ao dicionário (SQLite); números/dígitos pela seção NÚMEROS (`5` → `ton`).
4. Flexão: Português → Rhagho: forma regular → infinitivo (`amei`→`amar`) → base + sufixo; Rhagho → Português: sufixo → base → infinitivo → conjugação portuguesa **regular**, com a pessoa tirada do pronome sujeito da oração (`nuruh … 'ta` → "amei").
5. Ambiguidades: escolhe a leitura padrão e **mostra as outras**; só Português → Rhagho conta como ambiguidade que justifica oferecer IA.
6. Palavra inexistente: **não é inventada** — fica como digitada, é sinalizada, ganha sugestões (distância de Levenshtein, prefixo, formas alternativas; para formas verbais desconhecidas sugere a base conhecida mantendo o sufixo) e o botão **Adicionar ao dicionário** abre o formulário com a palavra preenchida.

A ordem das palavras segue o português (relaxada); nenhuma gramática rígida além do especificado foi inventada.

## Dados do arquivo inicial que a importação trata

Seções por cabeçalho `# CLASSE` (classe vem do cabeçalho; `# TODO…` mantém a classe anterior e é sinalizado); aspas retas **ou curvas**; seção NÚMEROS em ordem invertida (`"Aki" : "0"`); verbos agrupados em infinitivo + 6 pessoas (presente) + 6 (passado) + 6 (futuro) + 3–4 (imperativo). Ver [relatorio-importacao.md](relatorio-importacao.md).

## Alfabeto especial

Não implementado nesta versão. `IRhaghoScript` (`ToDisplay`/`FromDisplay`, hoje identidade) marca o único ponto onde uma conversão futura deve entrar: **na borda da interface**; SQLite, Firestore, regras e IA continuam usando sempre a forma latina.
