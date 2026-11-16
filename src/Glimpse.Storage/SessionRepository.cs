using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Core;
using Microsoft.Data.Sqlite;

namespace Glimpse.Storage;

public sealed class SessionRepository : ISessionRepository
{
    private readonly string connectionString;
    private readonly SemaphoreSlim access = new(1);
    public SessionRepository(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var db = Open();
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        int existing = Convert.ToInt32(version.ExecuteScalar());
        if (existing > 1) throw new InvalidDataException("Database was created by a newer Glimpse version.");
        using var schema = db.CreateCommand();
        schema.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL,
                created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL,
                capture_rect_json TEXT NOT NULL, document_revision INTEGER NOT NULL,
                snapshot_limit INTEGER NOT NULL DEFAULT 9, schema_version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS content_blocks (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                content TEXT NOT NULL, normalized_text TEXT NOT NULL, content_hash TEXT NOT NULL,
                order_key TEXT NOT NULL, status TEXT NOT NULL, provenance_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ocr_snapshots (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                sequence_no INTEGER NOT NULL, data_json TEXT NOT NULL, UNIQUE(session_id, sequence_no));
            CREATE TABLE IF NOT EXISTS snapshot_blocks (
                snapshot_id TEXT NOT NULL REFERENCES ocr_snapshots(id) ON DELETE CASCADE,
                block_id TEXT NOT NULL REFERENCES content_blocks(id) ON DELETE CASCADE,
                bbox_json TEXT NOT NULL, ocr_text TEXT NOT NULL, relation_type TEXT NOT NULL,
                PRIMARY KEY(snapshot_id, block_id));
            CREATE TABLE IF NOT EXISTS capture_gaps (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                data_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_blocks_session_order ON content_blocks(session_id, order_key);
            CREATE INDEX IF NOT EXISTS idx_snapshots_session_seq ON ocr_snapshots(session_id, sequence_no DESC);
            PRAGMA user_version=1;
            """;
        schema.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Envelope.Json);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Envelope.Json)!;

    public async Task<List<SessionSummary>> ListAsync(CancellationToken cancellation = default)
    {
        await access.WaitAsync(cancellation);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,title,status,updated_at_ms,document_revision FROM sessions ORDER BY updated_at_ms DESC";
            using var reader = cmd.ExecuteReader();
            List<SessionSummary> result = [];
            while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4)));
            return result;
        }
        finally { access.Release(); }
    }
    public async Task<SessionData?> LoadAsync(Guid id, CancellationToken cancellation = default)
    {
        await access.WaitAsync(cancellation);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT title,status,created_at_ms,updated_at_ms,capture_rect_json,document_revision FROM sessions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            SessionData session;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return null;
                session = new(id, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    Read<PixelRect>(reader.GetString(4)), reader.GetInt32(5), [], [], []);
            }
            cmd.CommandText = "SELECT id,content,normalized_text,status,provenance_json FROM content_blocks WHERE session_id=$id ORDER BY order_key";
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) session.Blocks.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), Read<Provenance>(reader.GetString(4))));
            cmd.CommandText = "SELECT data_json FROM ocr_snapshots WHERE session_id=$id ORDER BY sequence_no";
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) session.Snapshots.Add(Read<Snapshot>(reader.GetString(0)));
            cmd.CommandText = "SELECT data_json FROM capture_gaps WHERE session_id=$id ORDER BY rowid";
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) session.Gaps.Add(Read<CaptureGap>(reader.GetString(0)));
            return session;
        }
        finally { access.Release(); }
    }
    public async Task SaveAsync(SessionData session, CancellationToken cancellation = default)
    {
        await access.WaitAsync(cancellation);
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            void Run(string sql, params (string Key, object Value)[] args)
            {
                cancellation.ThrowIfCancellationRequested();
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
                cmd.ExecuteNonQuery();
            }
            Run("""
                INSERT INTO sessions(id,title,status,created_at_ms,updated_at_ms,capture_rect_json,document_revision)
                VALUES($id,$title,$status,$created,$updated,$rect,$revision)
                ON CONFLICT(id) DO UPDATE SET title=$title,status=$status,updated_at_ms=$updated,
                capture_rect_json=$rect,document_revision=$revision
                """, ("$id", session.Id.ToString()), ("$title", session.Title), ("$status", session.Status),
                ("$created", session.CreatedAtMs), ("$updated", session.UpdatedAtMs),
                ("$rect", Json(session.Rect)), ("$revision", session.Revision));
            // Rebalance order keys atomically; block UUIDs and original evidence never change.
            for (int i = 0; i < session.Blocks.Count; i++)
            {
                var b = session.Blocks[i];
                Run("""
                    INSERT INTO content_blocks VALUES($id,$session,$text,$normalized,$hash,$order,$status,$source)
                    ON CONFLICT(id) DO UPDATE SET order_key=$order,provenance_json=$source,status=$status
                    """, ("$id", b.Id.ToString()), ("$session", session.Id.ToString()), ("$text", b.Text),
                    ("$normalized", b.Normalized), ("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.Normalized)))),
                    ("$order", i.ToString("D12")), ("$status", b.Status), ("$source", Json(b.Source)));
            }
            Run("DELETE FROM ocr_snapshots WHERE session_id=$id", ("$id", session.Id.ToString()));
            foreach (var s in session.Snapshots.TakeLast(9))
            {
                Run("INSERT INTO ocr_snapshots VALUES($id,$session,$seq,$json)", ("$id", s.Id.ToString()),
                    ("$session", session.Id.ToString()), ("$seq", s.Sequence), ("$json", Json(s)));
                for (int i = 0; i < s.BlockIds.Count; i++)
                    Run("INSERT INTO snapshot_blocks VALUES($snapshot,$block,$box,$text,$relation)",
                        ("$snapshot", s.Id.ToString()), ("$block", s.BlockIds[i].ToString()),
                        ("$box", Json(s.Observation.Blocks[i].Bounds)), ("$text", s.Observation.Blocks[i].Text), ("$relation", "observed"));
            }
            foreach (var gap in session.Gaps)
                Run("INSERT INTO capture_gaps VALUES($id,$session,$json) ON CONFLICT(id) DO UPDATE SET data_json=$json",
                    ("$id", gap.Id.ToString()), ("$session", session.Id.ToString()), ("$json", Json(gap)));
            cancellation.ThrowIfCancellationRequested();
            tx.Commit();
        }
        finally { access.Release(); }
    }
    public async Task DeleteAsync(Guid id, CancellationToken cancellation = default)
    {
        await access.WaitAsync(cancellation);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            cmd.ExecuteNonQuery();
        }
        finally { access.Release(); }
    }
    public async Task BackupAsync(string destination, CancellationToken cancellation = default)
    {
        await access.WaitAsync(cancellation);
        try
        {
            using var source = Open();
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
            target.Open();
            source.BackupDatabase(target);
        }
        finally { access.Release(); }
    }
}
