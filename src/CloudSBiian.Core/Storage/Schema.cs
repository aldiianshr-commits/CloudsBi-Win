namespace CloudSBiian.Core.Storage;

/// <summary>Ordered schema migrations. Never edit an existing entry; append a new one.</summary>
internal static class Schema
{
    public static readonly string[] Migrations =
    [
        """
        CREATE TABLE accounts(
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            display_name TEXT NOT NULL,
            identity TEXT,
            root_path TEXT NOT NULL DEFAULT '/',
            backend TEXT,
            options_json TEXT NOT NULL DEFAULT '{}',
            enabled INTEGER NOT NULL DEFAULT 1,
            auto_connect INTEGER NOT NULL DEFAULT 1,
            created_at INTEGER NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE cache_files(
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            file_key TEXT NOT NULL,
            size INTEGER NOT NULL,
            identity TEXT NOT NULL,
            chunk_size INTEGER NOT NULL,
            last_access INTEGER NOT NULL,
            PRIMARY KEY(account_id, path));
        CREATE UNIQUE INDEX ix_cache_files_key ON cache_files(account_id, file_key);

        CREATE TABLE cache_chunks(
            account_id TEXT NOT NULL,
            file_key TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            length INTEGER NOT NULL,
            disk_bytes INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            last_access INTEGER NOT NULL,
            origin INTEGER NOT NULL DEFAULT 0,
            used INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(account_id, file_key, chunk_index));
        CREATE INDEX ix_cache_chunks_lru ON cache_chunks(last_access);

        CREATE TABLE cache_pins(
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            PRIMARY KEY(account_id, path));

        CREATE TABLE cache_exclusions(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT,
            pattern TEXT NOT NULL);

        CREATE TABLE dir_cache(
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            entries_json TEXT NOT NULL,
            fetched_at INTEGER NOT NULL,
            PRIMARY KEY(account_id, path));

        CREATE TABLE upload_queue(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            staging_file TEXT NOT NULL,
            size INTEGER NOT NULL,
            state TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_error TEXT,
            base_identity TEXT,
            next_attempt_at INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL);
        CREATE UNIQUE INDEX ix_upload_queue_path ON upload_queue(account_id, path);

        CREATE TABLE pred_file_stats(
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            size INTEGER NOT NULL DEFAULT 0,
            open_count INTEGER NOT NULL DEFAULT 0,
            read_bytes INTEGER NOT NULL DEFAULT 0,
            sequential_reads INTEGER NOT NULL DEFAULT 0,
            random_reads INTEGER NOT NULL DEFAULT 0,
            last_open INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(account_id, path));

        CREATE TABLE pred_co_access(
            account_id TEXT NOT NULL,
            path_a TEXT NOT NULL,
            path_b TEXT NOT NULL,
            count INTEGER NOT NULL DEFAULT 0,
            last_seen INTEGER NOT NULL,
            PRIMARY KEY(account_id, path_a, path_b));

        CREATE TABLE pred_folder_stats(
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            visit_count INTEGER NOT NULL DEFAULT 0,
            last_visit INTEGER NOT NULL,
            PRIMARY KEY(account_id, path));

        CREATE TABLE pred_trace(
            trace_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            path TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            first_ms INTEGER NOT NULL,
            PRIMARY KEY(trace_id, account_id, path, chunk_index));

        CREATE TABLE library(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            account_id TEXT,
            cloud_folder TEXT,
            executable TEXT NOT NULL,
            arguments TEXT NOT NULL DEFAULT '',
            working_dir TEXT,
            mode TEXT NOT NULL,
            cache_priority INTEGER NOT NULL DEFAULT 1,
            local_path TEXT,
            added_at INTEGER NOT NULL,
            last_launch_at INTEGER,
            last_exit_code INTEGER,
            last_status TEXT,
            last_pid INTEGER,
            last_pid_start INTEGER);

        CREATE TABLE activity_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts INTEGER NOT NULL,
            level TEXT NOT NULL,
            category TEXT NOT NULL,
            account_id TEXT,
            message TEXT NOT NULL);
        CREATE INDEX ix_activity_ts ON activity_log(ts);

        CREATE TABLE counters(
            name TEXT PRIMARY KEY,
            value INTEGER NOT NULL);
        """,
    ];
}
