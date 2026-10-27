using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Data;

public sealed record MigrationResult(int FromVersion, int ToVersion)
{
    public bool Changed => FromVersion != ToVersion;
}

/// <summary>Applies numbered schema migrations; the version lives in app_meta.schema_version.</summary>
public static class Migrator
{
    public const int LatestVersion = 8;

    public static MigrationResult Migrate(SqliteConnection conn)
    {
        Database.Exec(conn, "CREATE TABLE IF NOT EXISTS app_meta (key TEXT PRIMARY KEY, value TEXT)");
        var from = CurrentVersion(conn);
        var version = from;
        foreach (var (v, sql) in Migrations)
        {
            if (v <= version) continue;
            Database.InTransaction(conn, tx =>
            {
                foreach (var statement in sql)
                    Database.Exec(conn, statement, tx);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO app_meta(key, value) VALUES('schema_version', $v) " +
                                  "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$v", v.ToString());
                cmd.ExecuteNonQuery();
            });
            version = v;
        }
        return new MigrationResult(from, version);
    }

    public static int CurrentVersion(SqliteConnection conn)
    {
        var exists = Database.Scalar<long>(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_meta'");
        if (exists == 0) return 0;
        var value = Database.Scalar<string>(conn, "SELECT value FROM app_meta WHERE key='schema_version'");
        return value is null ? 0 : int.Parse(value);
    }

    private static readonly (int Version, string[] Statements)[] Migrations =
    [
        (1,
        [
            """
            CREATE TABLE book (
              id INTEGER PRIMARY KEY,
              file_path TEXT NOT NULL,
              file_hash TEXT NOT NULL UNIQUE,
              title TEXT, author TEXT,
              title_norm TEXT, author_norm TEXT,
              format TEXT NOT NULL,
              language TEXT,
              status TEXT NOT NULL,
              status_reason TEXT,
              embedding_model_id TEXT,
              added_at TEXT NOT NULL
            )
            """,
            """
            CREATE TABLE section (
              id INTEGER PRIMARY KEY,
              book_id INTEGER NOT NULL REFERENCES book(id) ON DELETE CASCADE,
              parent_id INTEGER REFERENCES section(id),
              title TEXT, ord INTEGER NOT NULL
            )
            """,
            """
            CREATE TABLE chunk (
              id INTEGER PRIMARY KEY,
              book_id INTEGER NOT NULL REFERENCES book(id) ON DELETE CASCADE,
              section_id INTEGER REFERENCES section(id),
              chunk_index INTEGER NOT NULL,
              text TEXT NOT NULL,
              token_count INTEGER NOT NULL,
              page_start INTEGER, page_end INTEGER,
              location TEXT,
              UNIQUE (book_id, chunk_index)
            )
            """,
            "CREATE INDEX idx_chunk_book ON chunk(book_id)",
            "CREATE INDEX idx_section_book ON section(book_id)",
            """
            CREATE VIRTUAL TABLE chunk_fts USING fts5(
              text, content='chunk', content_rowid='id',
              tokenize='unicode61 remove_diacritics 2')
            """,
            // Holds its own copy of the text (no external content): only CJK/Thai books are
            // present, so rows can be removed by rowid without knowing which chunks were added.
            "CREATE VIRTUAL TABLE chunk_fts_cjk USING fts5(text, tokenize='trigram')",
            """
            CREATE VIRTUAL TABLE chunk_vec USING vec0(
              chunk_id INTEGER PRIMARY KEY,
              book_id INTEGER PARTITION KEY,
              embedding FLOAT[1024] distance_metric=cosine)
            """,
            """
            CREATE TABLE ingest_job (
              book_id INTEGER PRIMARY KEY REFERENCES book(id) ON DELETE CASCADE,
              stage TEXT NOT NULL,
              next_chunk_index INTEGER NOT NULL DEFAULT 0,
              progress REAL NOT NULL DEFAULT 0,
              error TEXT, updated_at TEXT NOT NULL
            )
            """,
            """
            CREATE TABLE worker_control (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              paused INTEGER NOT NULL DEFAULT 0,
              cpu_cap_percent INTEGER NOT NULL DEFAULT 100
            )
            """,
            "INSERT INTO worker_control(id, paused, cpu_cap_percent) VALUES (1, 0, 100)",
        ]),
        // Starts of a book since it last made progress; two without progress means it crashes the worker (ST-11).
        (2, ["ALTER TABLE ingest_job ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0"]),
        // Search history (DCR-1, ST-23): the question, when, and the summary as plain text. No references to chunks, so the
        // history does not depend on the library and removing a book does not change it.
        (3,
        [
            """
            CREATE TABLE search_history (
              id INTEGER PRIMARY KEY,
              question TEXT NOT NULL,
              created_at TEXT NOT NULL,
              summary_status TEXT NOT NULL,
              summary_text TEXT,
              summary_model TEXT,
              summary_ms INTEGER
            )
            """,
            "CREATE INDEX search_history_status ON search_history(summary_status)",
        ]),
        // Conversation threads (DCR-2, ST-24): questions are grouped into threads. The history written before threads existed is
        // deleted, as the user decided; a question is a search_history row, in order of id, and belongs to one thread.
        (4,
        [
            "DELETE FROM search_history",
            """
            CREATE TABLE search_thread (
              id INTEGER PRIMARY KEY,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            )
            """,
            "ALTER TABLE search_history ADD COLUMN thread_id INTEGER REFERENCES search_thread(id) ON DELETE CASCADE",
            "CREATE INDEX search_history_thread ON search_history(thread_id)",
        ]),
        // Tags on books and the search filter (DCR-3, ST-25). tag_key is the lower-cased tag, so "Novels" and "novels" are one tag;
        // tag keeps the spelling the user typed. A book's tags go with it when it is removed. The history keeps the filter a
        // question was asked with (JSON), so a summary finished after a restart searches the same books.
        (5,
        [
            """
            CREATE TABLE book_tag (
              book_id INTEGER NOT NULL REFERENCES book(id) ON DELETE CASCADE,
              tag_key TEXT NOT NULL,
              tag TEXT NOT NULL,
              PRIMARY KEY (book_id, tag_key)
            )
            """,
            "CREATE INDEX book_tag_key ON book_tag(tag_key)",
            "ALTER TABLE search_history ADD COLUMN filter TEXT",
        ]),
        // A follow-up is rewritten to stand on its own (DCR-6); the history keeps the rewritten question, so a summary finished
        // after a restart searches and summarizes exactly what was asked, without asking the model again.
        (6,
        [
            "ALTER TABLE search_history ADD COLUMN searched_as TEXT",
        ]),
        // Where the reader stopped in each book (DCR-10, ST-35). The chapter is its number among the book's sections that have
        // text, not a section id, so a book indexed again falls back to the nearest place instead of a dangling id.
        (7,
        [
            """
            CREATE TABLE reading_position (
              book_id INTEGER PRIMARY KEY REFERENCES book(id) ON DELETE CASCADE,
              section_pos INTEGER NOT NULL,
              paragraph INTEGER NOT NULL,
              updated_at TEXT NOT NULL
            )
            """,
        ]),
        // A question is also searched in the other languages of the library (DCR-11); the history keeps those translations (a JSON
        // array of strings), so a summary finished after a restart searches exactly what was searched, without asking the model again.
        (8,
        [
            "ALTER TABLE search_history ADD COLUMN also_searched TEXT",
        ]),
    ];
}
