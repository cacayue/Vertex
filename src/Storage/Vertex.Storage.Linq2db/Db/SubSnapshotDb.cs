﻿using LinqToDB;
using LinqToDB.Data;
using Vertex.Storage.Linq2db.Entitys;

namespace Vertex.Storage.Linq2db.Db
{
    public class SubSnapshotDb : DataConnection
    {
        public SubSnapshotDb(string name) : base(name)
        {
            this.MappingSchema.EntityDescriptorCreatedCallback = (schema, entityDescriptor) =>
            {
                entityDescriptor.TableName = entityDescriptor.TableName.ToLower();
                foreach (var entityDescriptorColumn in entityDescriptor.Columns)
                {
                    entityDescriptorColumn.ColumnName = entityDescriptorColumn.ColumnName.ToLower();
                }
            };
        }
        public ITable<SubSnapshotEntity<PrimaryKey>> Table<PrimaryKey>() => GetTable<SubSnapshotEntity<PrimaryKey>>();
    }
}
