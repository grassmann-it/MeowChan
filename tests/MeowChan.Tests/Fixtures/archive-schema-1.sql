-- Frozen archive schema 1 from abd20d1, before search and quote indexes.
CREATE TABLE archive_threads (
    source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
    url TEXT NOT NULL, title TEXT NOT NULL, media_directory TEXT NOT NULL,
    source_state INTEGER NOT NULL CHECK(source_state BETWEEN 0 AND 2),
    capture_enabled INTEGER NOT NULL CHECK(capture_enabled IN (0,1)),
    first_seen_at TEXT NOT NULL, last_captured_at TEXT, revision INTEGER NOT NULL,
    PRIMARY KEY(source,board,thread));
CREATE TABLE archive_posts (
    source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, post TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0), fields INTEGER NOT NULL,
    posted_at TEXT, subject TEXT, author TEXT, html TEXT,
    is_deleted INTEGER NOT NULL CHECK(is_deleted IN (0,1)),
    PRIMARY KEY(source,board,thread,post),
    FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread));
CREATE TABLE archive_media_directories (
    source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, directory TEXT NOT NULL,
    PRIMARY KEY(source,board,thread,directory),
    FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread));
CREATE TABLE archive_attachments (
    source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL, post TEXT NOT NULL,
    position INTEGER NOT NULL CHECK(position>=0), media_id TEXT, variant TEXT NOT NULL,
    url TEXT, original_name TEXT, planned_path TEXT, expected_size INTEGER CHECK(expected_size>=0),
    expected_md5 TEXT, is_deleted INTEGER NOT NULL CHECK(is_deleted IN (0,1)),
    PRIMARY KEY(source,board,thread,post,position),
    FOREIGN KEY(source,board,thread,post) REFERENCES archive_posts(source,board,thread,post));
CREATE INDEX archive_thread_order ON archive_threads(last_captured_at DESC,source,board,thread);
CREATE INDEX archive_post_order ON archive_posts(source,board,thread,ordinal,post);
CREATE TABLE media_decisions (
    source_id TEXT NOT NULL, board_id TEXT NOT NULL, thread_id TEXT NOT NULL,
    media_id TEXT NOT NULL, variant TEXT NOT NULL, download_uri TEXT NOT NULL,
    destination_path TEXT NOT NULL, expected_size INTEGER CHECK(expected_size IS NULL OR expected_size >= 0),
    expected_md5 TEXT, original_filename TEXT, legacy_path TEXT, extension TEXT,
    selection_json TEXT NOT NULL, decided_at_utc TEXT NOT NULL,
    PRIMARY KEY(source_id,board_id,thread_id,media_id,variant)
);
PRAGMA application_id=1329809486;
PRAGMA user_version=1;
