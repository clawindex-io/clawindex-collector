using Clawindex.Collector.Api.Economics;
using Microsoft.Data.Sqlite;

namespace Clawindex.Collector.Api.Persistence;

public sealed class EventRepository(IConfiguration configuration)
{
    private const string SelectSpanStateSql =
        """
        SELECT span_id, trace_id, parent_span_id, agent_id, span_name, span_kind, status,
               started_at, ended_at, operation, provider, model, input_tokens, output_tokens,
               is_conformant, attributes_json, updated_at
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
              trace_id, root_span_id, agent_id, status, started_at, ended_at, updated_at
            )
            VALUES (
              $trace_id, $root_span_id, $agent_id, $status, $started_at, $ended_at, $updated_at
            )
            ON CONFLICT(trace_id) DO UPDATE SET
              root_span_id = COALESCE(trace_state.root_span_id, excluded.root_span_id),
              agent_id = COALESCE(trace_state.agent_id, excluded.agent_id),
              status = CASE WHEN trace_state.status = 'finalized' THEN 'finalized' ELSE excluded.status END,
              started_at = MIN(trace_state.started_at, excluded.started_at),
              ended_at = CASE WHEN trace_state.status = 'finalized' THEN trace_state.ended_at ELSE excluded.ended_at END,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$trace_id", traceState.TraceId);
        command.Parameters.AddWithValue("$root_span_id", ToDbValue(traceState.RootSpanId));
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
              span_id, trace_id, parent_span_id, agent_id, span_name, span_kind, status,
              started_at, ended_at, operation, provider, model, input_tokens, output_tokens,
              is_conformant, attributes_json, updated_at
            )
            VALUES (
              $span_id, $trace_id, $parent_span_id, $agent_id, $span_name, $span_kind, $status,
              $started_at, $ended_at, $operation, $provider, $model, $input_tokens, $output_tokens,
              $is_conformant, $attributes_json, $updated_at
            )
            ON CONFLICT(span_id) DO UPDATE SET
              trace_id = excluded.trace_id,
              parent_span_id = excluded.parent_span_id,
              agent_id = excluded.agent_id,
              span_name = excluded.span_name,
              span_kind = excluded.span_kind,
              status = excluded.status,
              started_at = excluded.started_at,
              ended_at = excluded.ended_at,
              operation = excluded.operation,
              provider = excluded.provider,
              model = excluded.model,
              input_tokens = excluded.input_tokens,
              output_tokens = excluded.output_tokens,
              is_conformant = excluded.is_conformant,
              attributes_json = excluded.attributes_json,
              updated_at = excluded.updated_at
            WHERE span_state.status = 'placeholder';
            """;
        AddSpanStateParameters(command, spanState);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertPlaceholderSpanIfAbsentAsync(SpanState placeholder, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO span_state (
              span_id, trace_id, parent_span_id, agent_id, span_name, span_kind, status,
              started_at, ended_at, operation, provider, model, input_tokens, output_tokens,
              is_conformant, attributes_json, updated_at
            )
            VALUES (
              $span_id, $trace_id, $parent_span_id, $agent_id, $span_name, $span_kind, $status,
              $started_at, $ended_at, $operation, $provider, $model, $input_tokens, $output_tokens,
              $is_conformant, $attributes_json, $updated_at
            );
            """;
        AddSpanStateParameters(command, placeholder);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TraceState?> GetTraceStateAsync(string traceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT trace_id, root_span_id, agent_id, status, started_at, ended_at, updated_at
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
            SELECT trace_id, root_span_id, agent_id, status, started_at, ended_at, updated_at
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

    // Invariant: all timestamp columns in span_state are canonical UTC "O" strings
    // (e.g. "2025-01-01T00:00:00.0000000+00:00"). The window comparison below relies on
    // lexicographic == chronological ordering, which holds only for same-format UTC strings.
    // Do not pass non-UTC DateTimeOffset values; callers must normalize to UTC first.
    // Multi-tenancy addition: prepend WHERE tenant_id = $tenant_id to the existing WHERE
    // clause — no other change to GROUP BY or SELECT is needed.
    public async Task<IReadOnlyList<AgentRollup>> GetAgentRollupsAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                agent_id,
                COUNT(*)                                                       AS span_count,
                COUNT(DISTINCT trace_id)                                       AS trace_count,
                COUNT(CASE WHEN status = 'error' THEN 1 END)                   AS error_count,
                COUNT(CASE WHEN status = 'error' THEN 1 END) * 1.0 / COUNT(*) AS error_rate,
                MAX(ended_at)                                                  AS last_seen,
                SUM(is_conformant) * 1.0 / COUNT(*)                            AS conformance_ratio
            FROM span_state
            WHERE agent_id IS NOT NULL
              AND ended_at >= $since
              AND ended_at < $until
            GROUP BY agent_id
            ORDER BY last_seen DESC;
            """;
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));

        var rollups = new List<AgentRollup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rollups.Add(new AgentRollup(
                AgentId: reader.GetString(0),
                SpanCount: reader.GetInt64(1),
                TraceCount: reader.GetInt64(2),
                ErrorCount: reader.GetInt64(3),
                ErrorRate: reader.GetDouble(4),
                LastSeen: DateTimeOffset.Parse(reader.GetString(5)),
                ConformanceRatio: reader.GetDouble(6)));
        }

        return rollups;
    }

    // Multi-tenancy addition: prepend WHERE tenant_id = $tenant_id before agent_id = $agent_id
    // — no other change to the SELECT or aggregation is needed.
    public async Task<AgentDetailRollup> GetAgentRollupAsync(
        string agentId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*)                                                       AS span_count,
                COUNT(DISTINCT trace_id)                                       AS trace_count,
                COUNT(CASE WHEN status = 'error' THEN 1 END)                   AS error_count,
                COUNT(CASE WHEN status = 'error' THEN 1 END) * 1.0 / COUNT(*) AS error_rate,
                MAX(ended_at)                                                  AS last_seen,
                SUM(is_conformant) * 1.0 / COUNT(*)                            AS conformance_ratio
            FROM span_state
            WHERE agent_id = $agent_id
              AND agent_id IS NOT NULL
              AND ended_at >= $since
              AND ended_at < $until;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var spanCount = reader.GetInt64(0);
        var traceCount = reader.GetInt64(1);
        var errorCount = reader.GetInt64(2);
        var errorRate = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3);
        var lastSeen = reader.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(4));
        var conformanceRatio = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5);

        return new AgentDetailRollup(spanCount, traceCount, errorCount, errorRate, lastSeen, conformanceRatio);
    }

    public async Task<IReadOnlyList<RecentTrace>> GetAgentRecentTracesAsync(
        string agentId,
        DateTimeOffset since,
        DateTimeOffset until,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.trace_id,
                   COUNT(*)                                      AS span_count,
                   COUNT(CASE WHEN s.status = 'error' THEN 1 END) AS error_count,
                   t.status                                      AS trace_status,
                   t.started_at                                  AS trace_started_at,
                   t.ended_at                                    AS trace_ended_at
            FROM span_state s
            LEFT JOIN trace_state t ON t.trace_id = s.trace_id
            WHERE s.agent_id = $agent_id AND s.agent_id IS NOT NULL
              AND s.ended_at >= $since AND s.ended_at < $until
            GROUP BY s.trace_id, t.status, t.started_at, t.ended_at
            ORDER BY t.started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var traces = new List<RecentTrace>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var traceId = reader.GetString(0);
            var agentSpanCount = reader.GetInt64(1);
            var errorCount = reader.GetInt64(2);
            var traceStatus = reader.IsDBNull(3) ? "open" : reader.GetString(3);
            var startedAt = reader.IsDBNull(4)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(4));
            var endedAtStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            var endedAt = endedAtStr is not null ? DateTimeOffset.Parse(endedAtStr) : (DateTimeOffset?)null;
            var durationMs = startedAt.HasValue && endedAt.HasValue
                ? (long)(endedAt.Value - startedAt.Value).TotalMilliseconds
                : (long?)null;

            traces.Add(new RecentTrace(traceId, traceStatus, startedAt, durationMs, agentSpanCount, errorCount));
        }

        return traces;
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

    // Returns one row per (agent_id, provider, model) across the window, with token totals
    // split by all spans vs spans that belong to traces with at least one error span.
    // Cost is computed at read time from these aggregates; nothing is written back to span_state.
    // Multi-tenancy addition: prepend WHERE s.agent_id IS NOT NULL AND tenant_id = $tenant_id.
    public async Task<IReadOnlyList<AgentTokenAggregate>> GetAgentTokenAggregatesAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.agent_id,
                s.provider,
                s.model,
                SUM(COALESCE(s.input_tokens,  0))                                                     AS input_tokens,
                SUM(COALESCE(s.output_tokens, 0))                                                     AS output_tokens,
                COUNT(*)                                                                               AS span_count,
                COUNT(CASE WHEN s.input_tokens IS NOT NULL THEN 1 END)                                AS token_bearing_span_count,
                SUM(CASE WHEN err.trace_id IS NOT NULL THEN COALESCE(s.input_tokens,  0) ELSE 0 END)  AS error_trace_input_tokens,
                SUM(CASE WHEN err.trace_id IS NOT NULL THEN COALESCE(s.output_tokens, 0) ELSE 0 END)  AS error_trace_output_tokens,
                COUNT(CASE WHEN err.trace_id IS NOT NULL AND s.input_tokens IS NOT NULL THEN 1 END)   AS error_trace_token_bearing_spans
            FROM span_state s
            LEFT JOIN (
                SELECT DISTINCT trace_id
                FROM span_state
                WHERE agent_id IS NOT NULL
                  AND status = 'error'
                  AND ended_at >= $since AND ended_at < $until
            ) err ON err.trace_id = s.trace_id
            WHERE s.agent_id IS NOT NULL
              AND s.ended_at >= $since AND s.ended_at < $until
            GROUP BY s.agent_id, s.provider, s.model;
            """;
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));

        var results = new List<AgentTokenAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentTokenAggregate(
                AgentId:                         reader.GetString(0),
                Provider:                        ReadNullableString(reader, 1),
                Model:                           ReadNullableString(reader, 2),
                InputTokens:                     reader.GetInt64(3),
                OutputTokens:                    reader.GetInt64(4),
                SpanCount:                       reader.GetInt64(5),
                TokenBearingSpanCount:           reader.GetInt64(6),
                ErrorTraceInputTokens:           reader.GetInt64(7),
                ErrorTraceOutputTokens:          reader.GetInt64(8),
                ErrorTraceTokenBearingSpanCount: reader.GetInt64(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<AgentTokenAggregate>> GetSingleAgentTokenAggregatesAsync(
        string agentId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.agent_id,
                s.provider,
                s.model,
                SUM(COALESCE(s.input_tokens,  0))                                                     AS input_tokens,
                SUM(COALESCE(s.output_tokens, 0))                                                     AS output_tokens,
                COUNT(*)                                                                               AS span_count,
                COUNT(CASE WHEN s.input_tokens IS NOT NULL THEN 1 END)                                AS token_bearing_span_count,
                SUM(CASE WHEN err.trace_id IS NOT NULL THEN COALESCE(s.input_tokens,  0) ELSE 0 END)  AS error_trace_input_tokens,
                SUM(CASE WHEN err.trace_id IS NOT NULL THEN COALESCE(s.output_tokens, 0) ELSE 0 END)  AS error_trace_output_tokens,
                COUNT(CASE WHEN err.trace_id IS NOT NULL AND s.input_tokens IS NOT NULL THEN 1 END)   AS error_trace_token_bearing_spans
            FROM span_state s
            LEFT JOIN (
                SELECT DISTINCT trace_id
                FROM span_state
                WHERE agent_id = $agent_id AND agent_id IS NOT NULL
                  AND status = 'error'
                  AND ended_at >= $since AND ended_at < $until
            ) err ON err.trace_id = s.trace_id
            WHERE s.agent_id = $agent_id AND s.agent_id IS NOT NULL
              AND s.ended_at >= $since AND s.ended_at < $until
            GROUP BY s.agent_id, s.provider, s.model;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));

        var results = new List<AgentTokenAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentTokenAggregate(
                AgentId:                         reader.GetString(0),
                Provider:                        ReadNullableString(reader, 1),
                Model:                           ReadNullableString(reader, 2),
                InputTokens:                     reader.GetInt64(3),
                OutputTokens:                    reader.GetInt64(4),
                SpanCount:                       reader.GetInt64(5),
                TokenBearingSpanCount:           reader.GetInt64(6),
                ErrorTraceInputTokens:           reader.GetInt64(7),
                ErrorTraceOutputTokens:          reader.GetInt64(8),
                ErrorTraceTokenBearingSpanCount: reader.GetInt64(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<TraceTokenAggregate>> GetAgentTraceTokenAggregatesAsync(
        string agentId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                trace_id,
                provider,
                model,
                SUM(COALESCE(input_tokens,  0))                       AS input_tokens,
                SUM(COALESCE(output_tokens, 0))                       AS output_tokens,
                COUNT(CASE WHEN input_tokens IS NOT NULL THEN 1 END)  AS token_bearing_span_count
            FROM span_state
            WHERE agent_id = $agent_id AND agent_id IS NOT NULL
              AND ended_at >= $since AND ended_at < $until
            GROUP BY trace_id, provider, model;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToUniversalTime().ToString("O"));

        var results = new List<TraceTokenAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TraceTokenAggregate(
                TraceId:               reader.GetString(0),
                Provider:              ReadNullableString(reader, 1),
                Model:                 ReadNullableString(reader, 2),
                InputTokens:           reader.GetInt64(3),
                OutputTokens:          reader.GetInt64(4),
                TokenBearingSpanCount: reader.GetInt64(5)));
        }

        return results;
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
        command.Parameters.AddWithValue("$agent_id", ToDbValue(spanState.AgentId));
        command.Parameters.AddWithValue("$span_name", spanState.SpanName);
        command.Parameters.AddWithValue("$span_kind", spanState.SpanKind);
        command.Parameters.AddWithValue("$status", spanState.Status);
        // Timestamps must be canonical UTC "O" strings so that lexicographic ordering == chronological ordering.
        // GetAgentRollupsAsync window comparisons depend on this invariant. Never write non-UTC timestamps.
        command.Parameters.AddWithValue("$started_at", spanState.StartedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ended_at", spanState.EndedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$operation", ToDbValue(spanState.Operation));
        command.Parameters.AddWithValue("$provider", ToDbValue(spanState.Provider));
        command.Parameters.AddWithValue("$model", ToDbValue(spanState.Model));
        command.Parameters.AddWithValue("$input_tokens", spanState.InputTokens.HasValue ? (object)spanState.InputTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$output_tokens", spanState.OutputTokens.HasValue ? (object)spanState.OutputTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$is_conformant", spanState.IsConformant ? 1 : 0);
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
            ReadNullableString(reader, 1),
            ReadNullableString(reader, 2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            ReadNullableDateTimeOffset(reader, 5),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    private static SpanState ReadSpanState(SqliteDataReader reader)
    {
        return new SpanState(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13),
            reader.GetInt32(14) != 0,
            reader.GetString(15),
            DateTimeOffset.Parse(reader.GetString(16)));
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
              root_span_id TEXT NULL,
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
              agent_id TEXT NULL,
              span_name TEXT NOT NULL,
              span_kind TEXT NOT NULL,
              status TEXT NOT NULL,
              started_at TEXT NOT NULL,
              ended_at TEXT NOT NULL,
              operation TEXT NULL,
              provider TEXT NULL,
              model TEXT NULL,
              input_tokens INTEGER NULL,
              output_tokens INTEGER NULL,
              is_conformant INTEGER NOT NULL,
              attributes_json TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_span_state_trace_status
              ON span_state(trace_id, status);

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
