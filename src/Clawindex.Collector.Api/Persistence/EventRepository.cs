using Microsoft.Data.Sqlite;

namespace Clawindex.Collector.Api.Persistence;

public sealed class EventRepository(IConfiguration configuration)
{
    private const string SelectSpanStateSql =
        """
        SELECT span_id, trace_id, parent_span_id, task_id, agent_id, span_name, span_kind, status,
               started_at, ended_at, source_start_event_id, source_end_event_id, attributes_json, updated_at
        FROM span_state
        """;

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
              projection_status TEXT NOT NULL DEFAULT 'pending',
              projected_at TEXT NULL,
              exported_at TEXT NULL,
              projection_attempts INTEGER NOT NULL DEFAULT 0,
              projection_errors TEXT NULL
            );
            """;

        await command.ExecuteNonQueryAsync();
        await InitializeSpanStateAsync(connection);
        await EnsureProjectionColumnsAsync(connection);
        await BackfillProjectionStatusAsync(connection);
        await ResetInProgressProjectionsAsync(connection);
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

    public async Task<IReadOnlyList<AcceptedEvent>> GetUnprojectedAsync(
        int limit,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
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
            WHERE projection_status IN ('pending', 'failed')
              AND projection_attempts < $max_attempts
            ORDER BY received_at, occurred_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$max_attempts", maxAttempts);

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
            SET projection_status = 'projected',
                projected_at = $projected_at,
                exported_at = $projected_at,
                projection_errors = NULL
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$projected_at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkProjectionAttemptAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET projection_status = 'in_progress',
                projection_attempts = projection_attempts + 1,
                projection_errors = NULL
            WHERE event_id = $event_id
              AND projection_status IN ('pending', 'failed');
            """;
        command.Parameters.AddWithValue("$event_id", eventId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkProjectionFailedAsync(
        string eventId,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET projection_status = 'failed',
                projection_errors = $projection_errors
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$projection_errors", error);

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

    public async Task<ProjectionState?> GetProjectionStateAsync(string eventId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, projection_status, projected_at, exported_at, projection_attempts, projection_errors
            FROM events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProjectionState(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableDateTimeOffset(reader, 2),
            ReadNullableDateTimeOffset(reader, 3),
            reader.GetInt32(4),
            ReadNullableString(reader, 5));
    }

    public async Task UpsertTraceStateAsync(TraceState traceState, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO trace_state (
              trace_id, root_span_id, task_id, agent_id, status, started_at, ended_at, updated_at
            )
            VALUES (
              $trace_id, $root_span_id, $task_id, $agent_id, $status, $started_at, $ended_at, $updated_at
            )
            ON CONFLICT(trace_id) DO UPDATE SET
              root_span_id = COALESCE(trace_state.root_span_id, excluded.root_span_id),
              task_id = COALESCE(trace_state.task_id, excluded.task_id),
              agent_id = COALESCE(trace_state.agent_id, excluded.agent_id),
              status = CASE WHEN trace_state.status IN ('completed', 'error') THEN trace_state.status ELSE excluded.status END,
              started_at = MIN(trace_state.started_at, excluded.started_at),
              ended_at = COALESCE(trace_state.ended_at, excluded.ended_at),
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$trace_id", traceState.TraceId);
        command.Parameters.AddWithValue("$root_span_id", traceState.RootSpanId);
        command.Parameters.AddWithValue("$task_id", ToDbValue(traceState.TaskId));
        command.Parameters.AddWithValue("$agent_id", ToDbValue(traceState.AgentId));
        command.Parameters.AddWithValue("$status", traceState.Status);
        command.Parameters.AddWithValue("$started_at", traceState.StartedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ended_at", ToDbValue(traceState.EndedAt?.ToUniversalTime().ToString("O")));
        command.Parameters.AddWithValue("$updated_at", traceState.UpdatedAt.ToUniversalTime().ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertSpanStateAsync(SpanState spanState, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO span_state (
              span_id, trace_id, parent_span_id, task_id, agent_id, span_name, span_kind, status,
              started_at, ended_at, source_start_event_id, source_end_event_id, attributes_json, updated_at
            )
            VALUES (
              $span_id, $trace_id, $parent_span_id, $task_id, $agent_id, $span_name, $span_kind, $status,
              $started_at, $ended_at, $source_start_event_id, $source_end_event_id, $attributes_json, $updated_at
            )
            ON CONFLICT(span_id) DO UPDATE SET
              trace_id = excluded.trace_id,
              parent_span_id = COALESCE(span_state.parent_span_id, excluded.parent_span_id),
              task_id = COALESCE(span_state.task_id, excluded.task_id),
              agent_id = COALESCE(span_state.agent_id, excluded.agent_id),
              span_name = excluded.span_name,
              span_kind = excluded.span_kind,
              status = CASE WHEN span_state.status IN ('completed', 'error') THEN span_state.status ELSE excluded.status END,
              started_at = MIN(span_state.started_at, excluded.started_at),
              source_start_event_id = span_state.source_start_event_id,
              attributes_json = excluded.attributes_json,
              updated_at = excluded.updated_at;
            """;
        AddSpanStateParameters(command, spanState);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseSpanStateAsync(
        string spanId,
        string sourceEndEventId,
        string status,
        DateTimeOffset endedAt,
        string attributesJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE span_state
            SET status = CASE WHEN status IN ('completed', 'error') THEN status ELSE $status END,
                ended_at = COALESCE(ended_at, $ended_at),
                source_end_event_id = COALESCE(source_end_event_id, $source_end_event_id),
                attributes_json = $attributes_json,
                updated_at = $updated_at
            WHERE span_id = $span_id;
            """;
        command.Parameters.AddWithValue("$span_id", spanId);
        command.Parameters.AddWithValue("$source_end_event_id", sourceEndEventId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ended_at", endedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$attributes_json", attributesJson);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TraceState?> GetTraceStateAsync(string traceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trace_id, root_span_id, task_id, agent_id, status, started_at, ended_at, updated_at
            FROM trace_state
            WHERE trace_id = $trace_id;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTraceState(reader) : null;
    }

    public async Task<SpanState?> GetSpanStateAsync(string spanId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SelectSpanStateSql + " WHERE span_id = $span_id;";
        command.Parameters.AddWithValue("$span_id", spanId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSpanState(reader) : null;
    }

    public async Task<SpanState?> FindBestOpenSpanAsync(
        string traceId,
        string? taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            SelectSpanStateSql +
            """
             WHERE trace_id = $trace_id
               AND ($task_id IS NULL OR task_id = $task_id)
               AND status = 'open'
             ORDER BY CASE WHEN span_kind = 'tool.call' THEN 0 ELSE 1 END, updated_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$trace_id", traceId);
        command.Parameters.AddWithValue("$task_id", ToDbValue(taskId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSpanState(reader) : null;
    }

    public async Task<IReadOnlyList<SpanState>> GetTraceSpansAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SelectSpanStateSql + " WHERE trace_id = $trace_id ORDER BY started_at, span_kind;";
        command.Parameters.AddWithValue("$trace_id", traceId);

        var spans = new List<SpanState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            spans.Add(ReadSpanState(reader));
        }

        return spans;
    }

    public async Task<IReadOnlyList<TraceState>> GetOpenTraceStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trace_id, root_span_id, task_id, agent_id, status, started_at, ended_at, updated_at
            FROM trace_state
            WHERE status = 'open'
            ORDER BY updated_at;
            """;

        var traces = new List<TraceState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            traces.Add(ReadTraceState(reader));
        }

        return traces;
    }

    public async Task<IReadOnlyList<SpanState>> GetOpenSpanStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SelectSpanStateSql + " WHERE status = 'open' ORDER BY updated_at;";

        var spans = new List<SpanState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            spans.Add(ReadSpanState(reader));
        }

        return spans;
    }

    public async Task MapEventToSpanAsync(
        string eventId,
        string traceId,
        string spanId,
        string relationshipType,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO event_span_map (event_id, trace_id, span_id, relationship_type, created_at)
            VALUES ($event_id, $trace_id, $span_id, $relationship_type, $created_at);
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$trace_id", traceId);
        command.Parameters.AddWithValue("$span_id", spanId);
        command.Parameters.AddWithValue("$relationship_type", relationshipType);
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EventSpanMap?> GetEventSpanMapAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, trace_id, span_id, relationship_type, created_at
            FROM event_span_map
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EventSpanMap(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)))
            : null;
    }

    public async Task<IReadOnlyList<AcceptedEvent>> GetMappedSpanEventsAsync(
        string spanId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.event_id, e.schema_version, e.event_type, e.occurred_at, e.received_at,
                   e.source_system, e.source_component, e.source_version,
                   e.trace_id, e.span_id, e.task_id, e.agent_id, e.session_id,
                   e.raw_json, e.payload_json
            FROM event_span_map m
            JOIN events e ON e.event_id = m.event_id
            WHERE m.span_id = $span_id
              AND m.relationship_type = 'span_event'
            ORDER BY e.occurred_at, e.received_at;
            """;
        command.Parameters.AddWithValue("$span_id", spanId);

        var events = new List<AcceptedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadAcceptedEvent(reader));
        }

        return events;
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, AcceptedEvent acceptedEvent)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO events (
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

    private static void AddSpanStateParameters(SqliteCommand command, SpanState spanState)
    {
        command.Parameters.AddWithValue("$span_id", spanState.SpanId);
        command.Parameters.AddWithValue("$trace_id", spanState.TraceId);
        command.Parameters.AddWithValue("$parent_span_id", ToDbValue(spanState.ParentSpanId));
        command.Parameters.AddWithValue("$task_id", ToDbValue(spanState.TaskId));
        command.Parameters.AddWithValue("$agent_id", ToDbValue(spanState.AgentId));
        command.Parameters.AddWithValue("$span_name", spanState.SpanName);
        command.Parameters.AddWithValue("$span_kind", spanState.SpanKind);
        command.Parameters.AddWithValue("$status", spanState.Status);
        command.Parameters.AddWithValue("$started_at", spanState.StartedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ended_at", ToDbValue(spanState.EndedAt?.ToUniversalTime().ToString("O")));
        command.Parameters.AddWithValue("$source_start_event_id", spanState.SourceStartEventId);
        command.Parameters.AddWithValue("$source_end_event_id", ToDbValue(spanState.SourceEndEventId));
        command.Parameters.AddWithValue("$attributes_json", spanState.AttributesJson);
        command.Parameters.AddWithValue("$updated_at", spanState.UpdatedAt.ToUniversalTime().ToString("O"));
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

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static TraceState ReadTraceState(SqliteDataReader reader)
    {
        return new TraceState(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            ReadNullableDateTimeOffset(reader, 6),
            DateTimeOffset.Parse(reader.GetString(7)));
    }

    private static SpanState ReadSpanState(SqliteDataReader reader)
    {
        return new SpanState(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            ReadNullableDateTimeOffset(reader, 9),
            reader.GetString(10),
            ReadNullableString(reader, 11),
            reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13)));
    }

    private static async Task EnsureProjectionColumnsAsync(SqliteConnection connection)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(events);";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        await AddColumnIfMissingAsync(connection, columns, "projection_status", "TEXT NOT NULL DEFAULT 'pending'");
        await AddColumnIfMissingAsync(connection, columns, "projected_at", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, columns, "exported_at", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, columns, "projection_attempts", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, columns, "projection_errors", "TEXT NULL");
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        HashSet<string> columns,
        string columnName,
        string columnDefinition)
    {
        if (columns.Contains(columnName))
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE events ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private static async Task ResetInProgressProjectionsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET projection_status = 'pending'
            WHERE projection_status = 'in_progress';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InitializeSpanStateAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS trace_state (
              trace_id TEXT PRIMARY KEY,
              root_span_id TEXT NOT NULL,
              task_id TEXT NULL,
              agent_id TEXT NULL,
              status TEXT NOT NULL,
              started_at TEXT NOT NULL,
              ended_at TEXT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS span_state (
              span_id TEXT PRIMARY KEY,
              trace_id TEXT NOT NULL,
              parent_span_id TEXT NULL,
              task_id TEXT NULL,
              agent_id TEXT NULL,
              span_name TEXT NOT NULL,
              span_kind TEXT NOT NULL,
              status TEXT NOT NULL,
              started_at TEXT NOT NULL,
              ended_at TEXT NULL,
              source_start_event_id TEXT NOT NULL,
              source_end_event_id TEXT NULL,
              attributes_json TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_span_state_trace_task_status
              ON span_state(trace_id, task_id, status);

            CREATE TABLE IF NOT EXISTS event_span_map (
              event_id TEXT PRIMARY KEY,
              trace_id TEXT NOT NULL,
              span_id TEXT NOT NULL,
              relationship_type TEXT NOT NULL,
              created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_event_span_map_span
              ON event_span_map(span_id, relationship_type);
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task BackfillProjectionStatusAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET projection_status = 'projected',
                exported_at = COALESCE(exported_at, projected_at)
            WHERE projected_at IS NOT NULL
              AND projection_status = 'pending';
            """;
        await command.ExecuteNonQueryAsync();
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
