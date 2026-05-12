using Microsoft.Data.Sqlite;

namespace Clawindex.Collector.Api.Persistence;

public sealed class EventRepository(IConfiguration configuration)
{
    private readonly string _connectionString = BuildConnectionString(configuration);

    public async Task InitializeAsync()
    {
        var dbPath = GetDatabasePath(configuration);
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS events (
              event_id TEXT PRIMARY KEY,
              schema_version TEXT NOT NULL,
              event_type TEXT NOT NULL,
              occurred_at TEXT NOT NULL,
              received_at TEXT NOT NULL,
              source_system TEXT NOT NULL,
              source_component TEXT NULL,
              source_version TEXT NULL,
              trace_id TEXT NULL,
              span_id TEXT NULL,
              task_id TEXT NULL,
              agent_id TEXT NULL,
              session_id TEXT NULL,
              raw_json TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              projected_at TEXT NULL
            );
            """;

        await command.ExecuteNonQueryAsync();
        await EnsureProjectedAtColumnAsync(connection);
    }

    public async Task InsertAsync(AcceptedEvent acceptedEvent)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = CreateInsertCommand(connection, acceptedEvent);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertBatchAsync(IReadOnlyCollection<AcceptedEvent> events)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var acceptedEvent in events)
        {
            await using var command = CreateInsertCommand(connection, acceptedEvent);
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<AcceptedEvent?> GetByIdAsync(string eventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, schema_version, event_type, occurred_at, received_at,
                   source_system, source_component, source_version,
                   trace_id, span_id, task_id, agent_id, session_id,
                   raw_json, payload_json
            FROM events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return ReadAcceptedEvent(reader);
    }

    public async Task<IReadOnlyList<AcceptedEvent>> GetUnprojectedAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, schema_version, event_type, occurred_at, received_at,
                   source_system, source_component, source_version,
                   trace_id, span_id, task_id, agent_id, session_id,
                   raw_json, payload_json
            FROM events
            WHERE projected_at IS NULL
            ORDER BY received_at, occurred_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<AcceptedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadAcceptedEvent(reader));
        }

        return events;
    }

    public async Task MarkProjectedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET projected_at = $projected_at
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$projected_at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events;";
        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt32(count);
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, AcceptedEvent acceptedEvent)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO events (
              event_id, schema_version, event_type, occurred_at, received_at,
              source_system, source_component, source_version,
              trace_id, span_id, task_id, agent_id, session_id,
              raw_json, payload_json
            )
            VALUES (
              $event_id, $schema_version, $event_type, $occurred_at, $received_at,
              $source_system, $source_component, $source_version,
              $trace_id, $span_id, $task_id, $agent_id, $session_id,
              $raw_json, $payload_json
            );
            """;

        command.Parameters.AddWithValue("$event_id", acceptedEvent.EventId);
        command.Parameters.AddWithValue("$schema_version", acceptedEvent.SchemaVersion);
        command.Parameters.AddWithValue("$event_type", acceptedEvent.EventType);
        command.Parameters.AddWithValue("$occurred_at", acceptedEvent.OccurredAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$received_at", acceptedEvent.ReceivedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$source_system", acceptedEvent.SourceSystem);
        command.Parameters.AddWithValue("$source_component", ToDbValue(acceptedEvent.SourceComponent));
        command.Parameters.AddWithValue("$source_version", ToDbValue(acceptedEvent.SourceVersion));
        command.Parameters.AddWithValue("$trace_id", ToDbValue(acceptedEvent.TraceId));
        command.Parameters.AddWithValue("$span_id", ToDbValue(acceptedEvent.SpanId));
        command.Parameters.AddWithValue("$task_id", ToDbValue(acceptedEvent.TaskId));
        command.Parameters.AddWithValue("$agent_id", ToDbValue(acceptedEvent.AgentId));
        command.Parameters.AddWithValue("$session_id", ToDbValue(acceptedEvent.SessionId));
        command.Parameters.AddWithValue("$raw_json", acceptedEvent.RawJson);
        command.Parameters.AddWithValue("$payload_json", acceptedEvent.PayloadJson);

        return command;
    }

    private static object ToDbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static AcceptedEvent ReadAcceptedEvent(SqliteDataReader reader)
    {
        return new AcceptedEvent(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.GetString(5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            ReadNullableString(reader, 12),
            reader.GetString(13),
            reader.GetString(14));
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task EnsureProjectedAtColumnAsync(SqliteConnection connection)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(events);";

        var hasColumn = false;
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "projected_at", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE events ADD COLUMN projected_at TEXT NULL;";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabasePath(configuration)
        };

        return builder.ToString();
    }

    private static string GetDatabasePath(IConfiguration configuration)
    {
        return Environment.GetEnvironmentVariable("CLAWINDEX_DB_PATH")
            ?? configuration["Clawindex:DatabasePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "clawindex-collector.db");
    }
}
