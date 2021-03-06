// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Framework.Caching.Distributed;
using Microsoft.Framework.Internal;

namespace Microsoft.Framework.Caching.SqlServer
{
    internal class DatabaseOperations : IDatabaseOperations
    {
        /// <summary>
        /// Since there is no specific exception type representing a 'duplicate key' error, we are relying on
        /// the following message number which represents the following text in Microsoft SQL Server database.
        ///     "Violation of %ls constraint '%.*ls'. Cannot insert duplicate key in object '%.*ls'.
        ///     The duplicate key value is %ls."
        /// You can find the list of system messages by executing the following query:
        /// "SELECT * FROM sys.messages WHERE [text] LIKE '%duplicate%'"
        /// </summary>
        private const int DuplicateKeyErrorId = 2627;

        protected const string GetTableSchemaErrorText =
            "Could not retrieve information of table with schema '{0}' and " +
            "name '{1}'. Make sure you have the table setup and try again. " +
            "Connection string: {2}";

        public DatabaseOperations(
            string connectionString, string schemaName, string tableName, ISystemClock systemClock)
        {
            ConnectionString = connectionString;
            SchemaName = schemaName;
            TableName = tableName;
            SystemClock = systemClock;
            SqlQueries = new SqlQueries(schemaName, tableName);
        }

        protected SqlQueries SqlQueries { get; }

        protected string ConnectionString { get; }

        protected string SchemaName { get; }

        protected string TableName { get; }

        protected ISystemClock SystemClock { get; }

        public void DeleteCacheItem(string key)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(SqlQueries.DeleteCacheItem, connection);
                command.Parameters.AddCacheItemId(key);

                connection.Open();

                command.ExecuteNonQuery();
            }
        }

        public async Task DeleteCacheItemAsync(string key)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(SqlQueries.DeleteCacheItem, connection);
                command.Parameters.AddCacheItemId(key);

                await connection.OpenAsync();

                await command.ExecuteNonQueryAsync();
            }
        }

        public virtual byte[] GetCacheItem(string key)
        {
            return GetCacheItem(key, includeValue: true);
        }

        public virtual async Task<byte[]> GetCacheItemAsync(string key)
        {
            return await GetCacheItemAsync(key, includeValue: true);
        }

        public void RefreshCacheItem(string key)
        {
            GetCacheItem(key, includeValue: false);
        }

        public async Task RefreshCacheItemAsync(string key)
        {
            await GetCacheItemAsync(key, includeValue: false);
        }

        public virtual void GetTableSchema()
        {
            // Try connecting to the database and check if its available.
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(SqlQueries.TableInfo, connection);
                connection.Open();

                var reader = command.ExecuteReader(CommandBehavior.SingleRow);
                if (!reader.Read())
                {
                    throw new InvalidOperationException(
                        string.Format(GetTableSchemaErrorText, SchemaName, TableName, ConnectionString));
                }
            }
        }

        public virtual async Task GetTableSchemaAsync()
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(SqlQueries.TableInfo, connection);
                await connection.OpenAsync();

                var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);

                if (!await reader.ReadAsync())
                {
                    throw new InvalidOperationException(
                        string.Format(GetTableSchemaErrorText, SchemaName, TableName, ConnectionString));
                }
            }
        }

        public virtual void DeleteExpiredCacheItems()
        {
            var utcNow = SystemClock.UtcNow;

            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(SqlQueries.DeleteExpiredCacheItems, connection);
                command.Parameters.AddWithValue("UtcNow", SqlDbType.DateTimeOffset, utcNow);

                connection.Open();

                var effectedRowCount = command.ExecuteNonQuery();
            }
        }

        public virtual void SetCacheItem(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            var utcNow = SystemClock.UtcNow;

            var absoluteExpiration = GetAbsoluteExpiration(utcNow, options);
            ValidateOptions(options.SlidingExpiration, absoluteExpiration);

            using (var connection = new SqlConnection(ConnectionString))
            {
                var upsertCommand = new SqlCommand(SqlQueries.SetCacheItem, connection);
                upsertCommand.Parameters
                    .AddCacheItemId(key)
                    .AddCacheItemValue(value)
                    .AddSlidingExpirationInSeconds(options.SlidingExpiration)
                    .AddAbsoluteExpiration(absoluteExpiration)
                    .AddWithValue("UtcNow", SqlDbType.DateTimeOffset, utcNow);

                connection.Open();

                try
                {
                    upsertCommand.ExecuteNonQuery();
                }
                catch (SqlException ex)
                {
                    if (IsDuplicateKeyException(ex))
                    {
                        // There is a possibility that multiple requests can try to add the same item to the cache, in
                        // which case we receive a 'duplicate key' exception on the primary key column.
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        public virtual async Task SetCacheItemAsync(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            var utcNow = SystemClock.UtcNow;

            var absoluteExpiration = GetAbsoluteExpiration(utcNow, options);
            ValidateOptions(options.SlidingExpiration, absoluteExpiration);

            using (var connection = new SqlConnection(ConnectionString))
            {
                var upsertCommand = new SqlCommand(SqlQueries.SetCacheItem, connection);
                upsertCommand.Parameters
                    .AddCacheItemId(key)
                    .AddCacheItemValue(value)
                    .AddSlidingExpirationInSeconds(options.SlidingExpiration)
                    .AddAbsoluteExpiration(absoluteExpiration)
                    .AddWithValue("UtcNow", SqlDbType.DateTimeOffset, utcNow);

                await connection.OpenAsync();

                try
                {
                    await upsertCommand.ExecuteNonQueryAsync();
                }
                catch (SqlException ex)
                {
                    if (IsDuplicateKeyException(ex))
                    {
                        // There is a possibility that multiple requests can try to add the same item to the cache, in
                        // which case we receive a 'duplicate key' exception on the primary key column.
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        protected virtual byte[] GetCacheItem(string key, bool includeValue)
        {
            var utcNow = SystemClock.UtcNow;

            string query;
            if (includeValue)
            {
                query = SqlQueries.GetCacheItem;
            }
            else
            {
                query = SqlQueries.GetCacheItemWithoutValue;
            }

            byte[] value = null;
            TimeSpan? slidingExpiration = null;
            DateTimeOffset? absoluteExpiration = null;
            DateTimeOffset expirationTime;
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(query, connection);
                command.Parameters
                    .AddCacheItemId(key)
                    .AddWithValue("UtcNow", SqlDbType.DateTimeOffset, utcNow);

                connection.Open();

                var reader = command.ExecuteReader(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleRow | CommandBehavior.SingleResult);

                if (reader.Read())
                {
                    var id = reader.GetFieldValue<string>(Columns.Indexes.CacheItemIdIndex);

                    expirationTime = reader.GetFieldValue<DateTimeOffset>(Columns.Indexes.ExpiresAtTimeIndex);

                    if (!reader.IsDBNull(Columns.Indexes.SlidingExpirationInSecondsIndex))
                    {
                        slidingExpiration = TimeSpan.FromSeconds(
                            reader.GetFieldValue<long>(Columns.Indexes.SlidingExpirationInSecondsIndex));
                    }

                    if (!reader.IsDBNull(Columns.Indexes.AbsoluteExpirationIndex))
                    {
                        absoluteExpiration = reader.GetFieldValue<DateTimeOffset>(
                            Columns.Indexes.AbsoluteExpirationIndex);
                    }

                    if (includeValue)
                    {
                        value = reader.GetFieldValue<byte[]>(Columns.Indexes.CacheItemValueIndex);
                    }
                }
                else
                {
                    return null;
                }
            }

            return value;
        }

        protected virtual async Task<byte[]> GetCacheItemAsync(string key, bool includeValue)
        {
            var utcNow = SystemClock.UtcNow;

            string query;
            if (includeValue)
            {
                query = SqlQueries.GetCacheItem;
            }
            else
            {
                query = SqlQueries.GetCacheItemWithoutValue;
            }

            byte[] value = null;
            TimeSpan? slidingExpiration = null;
            DateTimeOffset? absoluteExpiration = null;
            DateTimeOffset expirationTime;
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(query, connection);
                command.Parameters
                    .AddCacheItemId(key)
                    .AddWithValue("UtcNow", SqlDbType.DateTimeOffset, utcNow);

                await connection.OpenAsync();

                var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleRow | CommandBehavior.SingleResult);

                if (await reader.ReadAsync())
                {
                    var id = await reader.GetFieldValueAsync<string>(Columns.Indexes.CacheItemIdIndex);

                    expirationTime = await reader.GetFieldValueAsync<DateTimeOffset>(
                        Columns.Indexes.ExpiresAtTimeIndex);

                    if (!await reader.IsDBNullAsync(Columns.Indexes.SlidingExpirationInSecondsIndex))
                    {
                        slidingExpiration = TimeSpan.FromSeconds(
                            await reader.GetFieldValueAsync<long>(Columns.Indexes.SlidingExpirationInSecondsIndex));
                    }

                    if (!await reader.IsDBNullAsync(Columns.Indexes.AbsoluteExpirationIndex))
                    {
                        absoluteExpiration = await reader.GetFieldValueAsync<DateTimeOffset>(
                            Columns.Indexes.AbsoluteExpirationIndex);
                    }

                    if (includeValue)
                    {
                        value = await reader.GetFieldValueAsync<byte[]>(Columns.Indexes.CacheItemValueIndex);
                    }
                }
                else
                {
                    return null;
                }
            }

            return value;
        }

        protected bool IsDuplicateKeyException(SqlException ex)
        {
            if (ex.Errors != null)
            {
                return ex.Errors.Cast<SqlError>().Any(error => error.Number == DuplicateKeyErrorId);
            }
            return false;
        }

        protected DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset utcNow, DistributedCacheEntryOptions options)
        {
            // calculate absolute expiration
            DateTimeOffset? absoluteExpiration = null;
            if (options.AbsoluteExpirationRelativeToNow.HasValue)
            {
                absoluteExpiration = utcNow.Add(options.AbsoluteExpirationRelativeToNow.Value);
            }
            else if (options.AbsoluteExpiration.HasValue)
            {
                if (options.AbsoluteExpiration.Value <= utcNow)
                {
                    throw new InvalidOperationException("The absolute expiration value must be in the future.");
                }

                absoluteExpiration = options.AbsoluteExpiration.Value;
            }
            return absoluteExpiration;
        }

        protected void ValidateOptions(TimeSpan? slidingExpiration, DateTimeOffset? absoluteExpiration)
        {
            if (!slidingExpiration.HasValue && !absoluteExpiration.HasValue)
            {
                throw new InvalidOperationException("Either absolute or sliding expiration needs " +
                    "to be provided.");
            }
        }
    }
}