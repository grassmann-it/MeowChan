using System;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    internal static void CreateSourceContractSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS archive_source_contracts (
                source TEXT NOT NULL, board TEXT NOT NULL, thread TEXT NOT NULL,
                decimal_post_order INTEGER NOT NULL CHECK(decimal_post_order IN (0,1)),
                PRIMARY KEY(source,board,thread),
                FOREIGN KEY(source,board,thread) REFERENCES archive_threads(source,board,thread) ON DELETE CASCADE);
            """);
        command.ExecuteNonQuery();
    }

    internal static void WritePostOrder(SqliteConnection connection, SqliteTransaction transaction, ResourceId id, bool? decimalOrder)
    {
        if (decimalOrder == null) return;
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO archive_source_contracts(source,board,thread,decimal_post_order)
            VALUES($source,$board,$thread,$order)
            ON CONFLICT(source,board,thread) DO UPDATE SET decimal_post_order=excluded.decimal_post_order;
            """);
        AddId(command, id);
        command.Parameters.AddWithValue("$order", decimalOrder.Value ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static bool? ReadPostOrder(SqliteConnection connection, SqliteTransaction transaction, ResourceId id)
    {
        // Read-only library consumers can still inspect older catalogs without upgrading or plugins.
        if (GetPragma(connection, "user_version") < 3) return null;
        using SqliteCommand command = Command(connection, transaction, """
            SELECT decimal_post_order FROM archive_source_contracts WHERE source=$source AND board=$board AND thread=$thread;
            """);
        AddId(command, id);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value) == 1;
    }
}
