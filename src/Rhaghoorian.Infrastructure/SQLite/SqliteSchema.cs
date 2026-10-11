namespace Rhaghoorian.Infrastructure.SQLite;

/// <summary>
/// Esquema do cache local. As colunas *_key guardam a forma normalizada (TextNormalizer) para pesquisa sem acentos,
/// calculada em C# — o SQLite não precisa de collations/ICU. Dados originais permanecem intactos nas demais colunas.
/// </summary>
public static class SqliteSchema
{
    public static readonly string[] Statements =
    {
        @"CREATE TABLE IF NOT EXISTS words (
            id                 TEXT PRIMARY KEY NOT NULL,
            rhagho             TEXT NOT NULL,
            portuguese         TEXT NOT NULL,
            part_of_speech     TEXT NOT NULL,
            etymology          TEXT NULL,
            example            TEXT NULL,
            sort_order         INTEGER NOT NULL DEFAULT 0,
            source_line        INTEGER NULL,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL,
            created_by         TEXT NULL,
            updated_by         TEXT NULL,
            version            INTEGER NOT NULL DEFAULT 1,
            remote_update_time TEXT NULL,
            pt_key             TEXT NOT NULL,
            rh_key             TEXT NOT NULL,
            pos_key            TEXT NOT NULL,
            rh_letters         INTEGER NOT NULL,
            pt_words           INTEGER NOT NULL,
            verb_infinitive    TEXT NULL,
            verb_base          TEXT NULL,
            verb_tense         INTEGER NULL,
            verb_person        INTEGER NULL,
            verb_is_infinitive INTEGER NULL,
            is_inflected       INTEGER NOT NULL DEFAULT 0
        );",
        "CREATE INDEX IF NOT EXISTS ix_words_pt_key ON words(pt_key);",
        "CREATE INDEX IF NOT EXISTS ix_words_rh_key ON words(rh_key);",
        "CREATE INDEX IF NOT EXISTS ix_words_pos ON words(pos_key);",
        "CREATE INDEX IF NOT EXISTS ix_words_order ON words(sort_order);",

        @"CREATE TABLE IF NOT EXISTS rh_keys (
            key      TEXT NOT NULL,
            word_id  TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            words    INTEGER NOT NULL
        );",
        "CREATE INDEX IF NOT EXISTS ix_rh_keys_key ON rh_keys(key);",
        "CREATE INDEX IF NOT EXISTS ix_rh_keys_word ON rh_keys(word_id);",

        @"CREATE TABLE IF NOT EXISTS alternative_forms (
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            text    TEXT NOT NULL,
            note    TEXT NULL
        );",
        "CREATE INDEX IF NOT EXISTS ix_alt_word ON alternative_forms(word_id);",

        @"CREATE TABLE IF NOT EXISTS synonyms (
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            text    TEXT NOT NULL,
            accepted_from_ai INTEGER NOT NULL DEFAULT 0
        );",
        "CREATE INDEX IF NOT EXISTS ix_syn_word ON synonyms(word_id);",

        @"CREATE TABLE IF NOT EXISTS abbreviations (
            word_id TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            text    TEXT NOT NULL
        );",
        "CREATE INDEX IF NOT EXISTS ix_abbr_word ON abbreviations(word_id);",

        @"CREATE TABLE IF NOT EXISTS translation_history (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source_text TEXT NOT NULL,
            direction   INTEGER NOT NULL,
            result_text TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );",
        "CREATE INDEX IF NOT EXISTS ix_history_created ON translation_history(created_at DESC, id DESC);",

        @"CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );",

        @"CREATE TABLE IF NOT EXISTS sync_state (
            id           INTEGER PRIMARY KEY CHECK (id = 1),
            revision     INTEGER NULL,
            last_sync_at TEXT NULL,
            cursor       TEXT NULL,
            last_full_sync_at TEXT NULL
        );",
        "INSERT OR IGNORE INTO sync_state(id, revision, last_sync_at) VALUES (1, NULL, NULL);",

        @"CREATE TABLE IF NOT EXISTS import_issues (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            severity    TEXT NOT NULL,
            code        TEXT NOT NULL,
            line        INTEGER NULL,
            key         TEXT NULL,
            message     TEXT NOT NULL,
            imported_at TEXT NOT NULL
        );"
    };
}
