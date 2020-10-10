﻿using LinqToDB.Data;
using System.Linq;
using System.Threading.Tasks;

namespace Vertex.Storage.Linq2db.Index.SQLite
{
    public class SQLiteIndexGenerator : IIndexGenerator
    {
        public Task CreateIndexIfNotExists(DataConnection conn, string table, string indexName, params string[] columns)
        {
            var sql = $"CREATE INDEX IF NOT EXISTS {indexName.Replace('.', '_')} ON \"{table}\" ({string.Join(',', columns.Select(c => $"\"{c}\""))});";
            return conn.ExecuteAsync(sql);
        }

        public Task CreateUniqueIndexIfNotExists(DataConnection conn, string table, string indexName, params string[] columns)
        {
            var sql = $"CREATE UNIQUE INDEX IF NOT EXISTS {indexName.Replace('.', '_')} ON \"{table}\" ({string.Join(',', columns.Select(c => $"\"{c}\""))});";
            return conn.ExecuteAsync(sql);
        }
    }
}
