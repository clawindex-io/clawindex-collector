using System.Net;
using System.Text.Json;
using Clawindex.Collector.Api.Economics;
using Clawindex.Collector.Api.Persistence;

namespace Clawindex.Collector.Tests;

// -------------------------------------------------------------------------
// Helpers shared across economics test classes
// -------------------------------------------------------------------------

file static class PricingTableFactory
{
    // Builds a PricingTable from inline JSON — the same schema as pricing.json.
    public static PricingTable From(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return new PricingTable(new System.IO.MemoryStream(bytes));
    }

    // A table with one known model (anthropic / test-model-x) at fixed prices.
    // input: $10 / 1M tokens  ($0.000010 per token)
    // output: $30 / 1M tokens ($0.000030 per token)
    // last_confirmed: 2026-07-01 — within the 90-day stale window as of 2026-07-11.
    public static PricingTable WithTestModel(string lastConfirmed = "2026-07-01") => From($$"""
        {
          "generated_at": "2026-07-11",
          "entries": [
            { "provider": "anthropic", "model": "test-model-x", "token_type": "input",  "price_per_million_tokens": 10.00, "effective_from": "2026-01-01", "last_confirmed": "{{lastConfirmed}}" },
            { "provider": "anthropic", "model": "test-model-x", "token_type": "output", "price_per_million_tokens": 30.00, "effective_from": "2026-01-01", "last_confirmed": "{{lastConfirmed}}" }
          ]
        }
        """);

    public static PricingTable Empty() => From("""{ "generated_at": "2026-07-11", "entries": [] }""");
}

file static class AggregateFactory
{
    public static AgentTokenAggregate Agent(
        string agentId,
        string? provider,
        string? model,
        long inputTokens,
        long outputTokens,
        long spanCount,
        long tokenBearingSpanCount,
        long errorTraceInputTokens  = 0,
        long errorTraceOutputTokens = 0,
        long errorTraceTokenBearing = 0) =>
        new(agentId, provider, model, inputTokens, outputTokens, spanCount,
            tokenBearingSpanCount, errorTraceInputTokens, errorTraceOutputTokens, errorTraceTokenBearing);

    public static TraceTokenAggregate Trace(
        string traceId,
        string? provider,
        string? model,
        long inputTokens,
        long outputTokens,
        long tokenBearingSpanCount) =>
        new(traceId, provider, model, inputTokens, outputTokens, tokenBearingSpanCount);
}

// -------------------------------------------------------------------------
// PricingTable unit tests
// -------------------------------------------------------------------------

public sealed class PricingTableTests
{
    [Fact]
    public void TryResolvePrice_KnownModel_ReturnsBothPrices()
    {
        var table = PricingTableFactory.WithTestModel();
        var ts = DateTimeOffset.UtcNow;

        var resolution = table.TryResolvePrice("anthropic", "test-model-x", ts);

        Assert.NotNull(resolution);
        Assert.Equal(10m / 1_000_000m, resolution.InputPricePerToken);
        Assert.Equal(30m / 1_000_000m, resolution.OutputPricePerToken);
    }

    [Fact]
    public void TryResolvePrice_UnknownModel_ReturnsNull()
    {
        var table = PricingTableFactory.WithTestModel();

        var resolution = table.TryResolvePrice("anthropic", "unknown-model", DateTimeOffset.UtcNow);

        Assert.Null(resolution);
    }

    [Fact]
    public void TryResolvePrice_NullProvider_ReturnsNull()
    {
        var table = PricingTableFactory.WithTestModel();

        Assert.Null(table.TryResolvePrice(null, "test-model-x", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryResolvePrice_NullModel_ReturnsNull()
    {
        var table = PricingTableFactory.WithTestModel();

        Assert.Null(table.TryResolvePrice("anthropic", null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryResolvePrice_CaseInsensitiveProviderAndModel()
    {
        var table = PricingTableFactory.WithTestModel();

        Assert.NotNull(table.TryResolvePrice("ANTHROPIC", "TEST-MODEL-X", DateTimeOffset.UtcNow));
        Assert.NotNull(table.TryResolvePrice("Anthropic", "Test-Model-X", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryResolvePrice_RecentLastConfirmed_IsNotStale()
    {
        // last_confirmed = 2026-07-01, today = 2026-07-11 => 10 days old => not stale
        var table = PricingTableFactory.WithTestModel(lastConfirmed: "2026-07-01");

        var resolution = table.TryResolvePrice("anthropic", "test-model-x", DateTimeOffset.UtcNow);

        Assert.NotNull(resolution);
        Assert.False(resolution.IsStale);
        Assert.Equal(new DateOnly(2026, 7, 1), resolution.PricedAsOf);
    }

    [Fact]
    public void TryResolvePrice_OldLastConfirmed_IsStale()
    {
        // last_confirmed = 2020-01-01 => well over 90 days ago => stale
        var table = PricingTableFactory.WithTestModel(lastConfirmed: "2020-01-01");

        var resolution = table.TryResolvePrice("anthropic", "test-model-x", DateTimeOffset.UtcNow);

        Assert.NotNull(resolution);
        Assert.True(resolution.IsStale);
        Assert.Equal(new DateOnly(2020, 1, 1), resolution.PricedAsOf);
    }

    [Fact]
    public void TryResolvePrice_MissingOutputEntry_ReturnsNull()
    {
        var table = PricingTableFactory.From("""
            {
              "generated_at": "2026-07-11",
              "entries": [
                { "provider": "anthropic", "model": "half-priced", "token_type": "input",
                  "price_per_million_tokens": 5.00, "effective_from": "2026-01-01", "last_confirmed": "2026-07-01" }
              ]
            }
            """);

        Assert.Null(table.TryResolvePrice("anthropic", "half-priced", DateTimeOffset.UtcNow));
    }
}

// -------------------------------------------------------------------------
// CostEstimator unit tests
// -------------------------------------------------------------------------

public sealed class CostEstimatorTests
{
    private static readonly DateTimeOffset Window = new(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EstimateAgent_KnownModelWithTokens_ComputesCorrectCost()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        // 1000 input tokens, 500 output tokens
        // cost = 1000 * (10/1M) + 500 * (30/1M) = 0.01 + 0.015 = 0.025
        var group = AggregateFactory.Agent("agent-1", "anthropic", "test-model-x",
            inputTokens: 1000, outputTokens: 500, spanCount: 2, tokenBearingSpanCount: 2);

        var result = estimator.EstimateAgent([group], totalSpanCount: 2, Window);

        Assert.Equal(0.025m, result.EstimatedCostUsd);
        Assert.Equal(2, result.CostedSpanCount);
        Assert.Equal(0, result.UncostedSpanCount);
        Assert.Equal(1.0, result.CostCoverage);
        Assert.False(result.PricingStale);
        Assert.NotNull(result.PricedAsOf);
    }

    [Fact]
    public void EstimateAgent_UnknownModel_IsUncosted()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Agent("agent-1", "openai", "gpt-unknown",
            inputTokens: 1000, outputTokens: 500, spanCount: 3, tokenBearingSpanCount: 3);

        var result = estimator.EstimateAgent([group], totalSpanCount: 3, Window);

        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.CostedSpanCount);
        Assert.Equal(3, result.UncostedSpanCount);
        Assert.Equal(0.0, result.CostCoverage);
    }

    [Fact]
    public void EstimateAgent_NullProviderOrModel_IsUncosted()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Agent("agent-1", null, null,
            inputTokens: 500, outputTokens: 200, spanCount: 5, tokenBearingSpanCount: 5);

        var result = estimator.EstimateAgent([group], totalSpanCount: 5, Window);

        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.CostedSpanCount);
        Assert.Equal(5, result.UncostedSpanCount);
    }

    [Fact]
    public void EstimateAgent_ZeroTokenBearingSpans_NoGuessing()
    {
        // Spans exist but all have null tokens (tokenBearingSpanCount = 0)
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Agent("agent-1", "anthropic", "test-model-x",
            inputTokens: 0, outputTokens: 0, spanCount: 4, tokenBearingSpanCount: 0);

        var result = estimator.EstimateAgent([group], totalSpanCount: 4, Window);

        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.CostedSpanCount);
        Assert.Equal(4, result.UncostedSpanCount);
        Assert.Equal(0.0, result.CostCoverage);
    }

    [Fact]
    public void EstimateAgent_ZeroTokens_ValidData_CostIsZero()
    {
        // Spans have token data that is explicitly 0 (reported by instrumentation)
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Agent("agent-1", "anthropic", "test-model-x",
            inputTokens: 0, outputTokens: 0, spanCount: 2, tokenBearingSpanCount: 2);

        var result = estimator.EstimateAgent([group], totalSpanCount: 2, Window);

        Assert.Equal(0m, result.EstimatedCostUsd);
        Assert.Equal(2, result.CostedSpanCount);
        Assert.Equal(1.0, result.CostCoverage);
    }

    [Fact]
    public void EstimateAgent_MixedCostedAndUncosted_PartialCoverageAndCost()
    {
        // 3 spans with known model + tokens, 2 spans with null tokens, 1 span with unknown model
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var knownGroup = AggregateFactory.Agent("a", "anthropic", "test-model-x",
            inputTokens: 3000, outputTokens: 1500, spanCount: 5, tokenBearingSpanCount: 3);
        var unknownGroup = AggregateFactory.Agent("a", "openai", "gpt-unknown",
            inputTokens: 200, outputTokens: 100, spanCount: 1, tokenBearingSpanCount: 1);

        var result = estimator.EstimateAgent([knownGroup, unknownGroup], totalSpanCount: 6, Window);

        // cost = 3000 * (10/1M) + 1500 * (30/1M) = 0.03 + 0.045 = 0.075
        Assert.Equal(0.075m, result.EstimatedCostUsd);
        Assert.Equal(3, result.CostedSpanCount);
        Assert.Equal(3, result.UncostedSpanCount);    // 2 no-token + 1 unknown-model
        Assert.Equal(0.5, result.CostCoverage);
    }

    [Fact]
    public void EstimateAgent_ErrorTraceCost_OnlyErrorTraceSpans()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        // Total: 2000 input, 1000 output. Error-trace portion: 500 input, 200 output.
        var group = AggregateFactory.Agent("a", "anthropic", "test-model-x",
            inputTokens: 2000, outputTokens: 1000, spanCount: 4, tokenBearingSpanCount: 4,
            errorTraceInputTokens: 500, errorTraceOutputTokens: 200, errorTraceTokenBearing: 2);

        var result = estimator.EstimateAgent([group], totalSpanCount: 4, Window);

        // total: 2000*(10/1M) + 1000*(30/1M) = 0.02 + 0.03 = 0.05
        Assert.Equal(0.05m, result.EstimatedCostUsd);
        // error trace: 500*(10/1M) + 200*(30/1M) = 0.005 + 0.006 = 0.011
        Assert.Equal(0.011m, result.EstimatedErrorTraceCostUsd);
    }

    [Fact]
    public void EstimateAgent_NoErrorTraceSpans_ErrorTraceCostIsNull()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Agent("a", "anthropic", "test-model-x",
            inputTokens: 1000, outputTokens: 500, spanCount: 2, tokenBearingSpanCount: 2,
            errorTraceInputTokens: 0, errorTraceOutputTokens: 0, errorTraceTokenBearing: 0);

        var result = estimator.EstimateAgent([group], totalSpanCount: 2, Window);

        Assert.NotNull(result.EstimatedCostUsd);
        Assert.Null(result.EstimatedErrorTraceCostUsd);
    }

    [Fact]
    public void EstimateAgent_StalePricing_FlagSet()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel(lastConfirmed: "2020-01-01"));
        var group = AggregateFactory.Agent("a", "anthropic", "test-model-x",
            inputTokens: 100, outputTokens: 50, spanCount: 1, tokenBearingSpanCount: 1);

        var result = estimator.EstimateAgent([group], totalSpanCount: 1, Window);

        Assert.True(result.PricingStale);
        Assert.Equal(new DateOnly(2020, 1, 1), result.PricedAsOf);
    }

    [Fact]
    public void EstimateAgent_PricedAsOf_EarliestDateAmongModels()
    {
        var table = PricingTableFactory.From("""
            {
              "generated_at": "2026-07-11",
              "entries": [
                { "provider": "anthropic", "model": "model-a", "token_type": "input",  "price_per_million_tokens": 10, "effective_from": "2025-01-01", "last_confirmed": "2026-06-01" },
                { "provider": "anthropic", "model": "model-a", "token_type": "output", "price_per_million_tokens": 30, "effective_from": "2025-01-01", "last_confirmed": "2026-06-01" },
                { "provider": "anthropic", "model": "model-b", "token_type": "input",  "price_per_million_tokens": 5,  "effective_from": "2024-01-01", "last_confirmed": "2026-01-15" },
                { "provider": "anthropic", "model": "model-b", "token_type": "output", "price_per_million_tokens": 15, "effective_from": "2024-01-01", "last_confirmed": "2026-01-15" }
              ]
            }
            """);
        var estimator = new CostEstimator(table);

        var groupA = AggregateFactory.Agent("a", "anthropic", "model-a", 100, 50, 1, 1);
        var groupB = AggregateFactory.Agent("a", "anthropic", "model-b", 100, 50, 1, 1);

        var result = estimator.EstimateAgent([groupA, groupB], totalSpanCount: 2, Window);

        // Should take the earliest (most conservative) date
        Assert.Equal(new DateOnly(2026, 1, 15), result.PricedAsOf);
    }

    [Fact]
    public void EstimateAgent_EmptyGroups_ReturnsEmpty()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());

        var result = estimator.EstimateAgent([], totalSpanCount: 5, Window);

        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.CostedSpanCount);
        Assert.Equal(5, result.UncostedSpanCount);
        Assert.Equal(0.0, result.CostCoverage);
        Assert.Null(result.PricedAsOf);
        Assert.False(result.PricingStale);
    }

    [Fact]
    public void EstimateTrace_KnownModel_CorrectCost()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Trace("t1", "anthropic", "test-model-x",
            inputTokens: 2000, outputTokens: 800, tokenBearingSpanCount: 3);

        var result = estimator.EstimateTrace([group], traceSpanCount: 3, Window);

        // 2000*(10/1M) + 800*(30/1M) = 0.02 + 0.024 = 0.044
        Assert.Equal(0.044m, result.EstimatedCostUsd);
        Assert.Equal(3, result.CostedSpanCount);
        Assert.Equal(0, result.UncostedSpanCount);
        Assert.Equal(1.0, result.CostCoverage);
    }

    [Fact]
    public void EstimateTrace_UnknownModel_IsUncosted()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        var group = AggregateFactory.Trace("t1", "openai", "gpt-mystery",
            inputTokens: 1000, outputTokens: 500, tokenBearingSpanCount: 2);

        var result = estimator.EstimateTrace([group], traceSpanCount: 2, Window);

        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.CostedSpanCount);
        Assert.Equal(2, result.UncostedSpanCount);
        Assert.Equal(0.0, result.CostCoverage);
    }

    [Fact]
    public void EstimateTrace_PartialTokenCoverage_CoverageReflectsOnlyBearingSpans()
    {
        var estimator = new CostEstimator(PricingTableFactory.WithTestModel());
        // 2 token-bearing spans out of 4 total
        var group = AggregateFactory.Trace("t1", "anthropic", "test-model-x",
            inputTokens: 500, outputTokens: 250, tokenBearingSpanCount: 2);

        var result = estimator.EstimateTrace([group], traceSpanCount: 4, Window);

        Assert.Equal(2, result.CostedSpanCount);
        Assert.Equal(2, result.UncostedSpanCount);
        Assert.Equal(0.5, result.CostCoverage);
    }
}

// -------------------------------------------------------------------------
// Economics integration tests (real SQLite, real PricingTable)
// -------------------------------------------------------------------------

public sealed class EconomicsIntegrationTests
{
    private static readonly string KnownProvider = "anthropic";
    private static readonly string KnownModel    = "claude-3-5-sonnet-20241022";

    // The real embedded pricing.json includes claude-3-5-sonnet-20241022 at
    // $3/1M input, $15/1M output, so all integration tests use this model.

    private static SpanState MakeSpanState(
        string spanId,
        string traceId,
        string? agentId,
        string status = "unset",
        bool isConformant = true,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? startedAt = null,
        string? provider = null,
        string? model = null,
        long? inputTokens = null,
        long? outputTokens = null)
    {
        var ended = endedAt ?? DateTimeOffset.UtcNow;
        return new SpanState(
            SpanId: spanId, TraceId: traceId, ParentSpanId: null, AgentId: agentId,
            SpanName: "test.span", SpanKind: "client", Status: status,
            StartedAt: startedAt ?? ended.AddSeconds(-1), EndedAt: ended,
            Operation: null, Provider: provider, Model: model,
            InputTokens: inputTokens, OutputTokens: outputTokens,
            IsConformant: isConformant, AttributesJson: "{}", UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static TraceState MakeTraceState(
        string traceId, string status, DateTimeOffset startedAt, DateTimeOffset? endedAt = null) =>
        new(traceId, null, null, status, startedAt, endedAt, DateTimeOffset.UtcNow);

    // -------------------------------------------------------------------------
    // Repository — GetAgentTokenAggregatesAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAgentTokenAggregates_ConformantSpansWithTokens_CorrectSums()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 1000, outputTokens: 400));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s2", "t1", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 500, outputTokens: 200));

        var aggregates = await fixture.Repository.GetAgentTokenAggregatesAsync(
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        var agg = Assert.Single(aggregates.Where(a => a.AgentId == agent));
        Assert.Equal(1500, agg.InputTokens);
        Assert.Equal(600,  agg.OutputTokens);
        Assert.Equal(2,    agg.TokenBearingSpanCount);
        Assert.Equal(2,    agg.SpanCount);
    }

    [Fact]
    public async Task GetAgentTokenAggregates_SpansWithNullTokens_NotTokenBearing()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);

        // One span with tokens, one without
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100, outputTokens: 50));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s2", "t1", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel));

        var aggregates = await fixture.Repository.GetAgentTokenAggregatesAsync(
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var agg = Assert.Single(aggregates.Where(a => a.AgentId == agent));
        Assert.Equal(2, agg.SpanCount);
        Assert.Equal(1, agg.TokenBearingSpanCount);
    }

    [Fact]
    public async Task GetAgentTokenAggregates_ErrorTraceSpans_SeparatedCorrectly()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        // error-trace: one error span + one unset span
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "e1", "error-trace", agent, status: "error", endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 200, outputTokens: 80));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "e2", "error-trace", agent, status: "unset", endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100, outputTokens: 40));
        // clean trace
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "c1", "clean-trace", agent, status: "unset", endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 500, outputTokens: 200));

        var aggregates = await fixture.Repository.GetAgentTokenAggregatesAsync(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var agg = Assert.Single(aggregates.Where(a => a.AgentId == agent));
        Assert.Equal(800, agg.InputTokens);   // 200+100+500
        Assert.Equal(320, agg.OutputTokens);  // 80+40+200
        Assert.Equal(300, agg.ErrorTraceInputTokens);  // 200+100 (both spans in error trace)
        Assert.Equal(120, agg.ErrorTraceOutputTokens); // 80+40
        Assert.Equal(2,   agg.ErrorTraceTokenBearingSpanCount);
    }

    [Fact]
    public async Task GetAgentTokenAggregates_PlaceholderSpans_ExcludedFromAggregates()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "real", "t1", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100, outputTokens: 50));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "placeholder", "t2", agentId: null, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 999, outputTokens: 888));

        var aggregates = await fixture.Repository.GetAgentTokenAggregatesAsync(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain(aggregates, a => a.AgentId == null);
        var agg = Assert.Single(aggregates);
        Assert.Equal(100, agg.InputTokens);
        Assert.Equal(50,  agg.OutputTokens);
    }

    [Fact]
    public async Task GetAgentTraceTokenAggregates_PerTraceGroups()
    {
        using var fixture = new RepositoryFixture();
        var agent = Guid.NewGuid().ToString();
        var inside = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "trace-a", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 300, outputTokens: 100));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s2", "trace-b", agent, endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 700, outputTokens: 250));

        var aggregates = await fixture.Repository.GetAgentTraceTokenAggregatesAsync(
            agent,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, aggregates.Count);
        var a = aggregates.Single(x => x.TraceId == "trace-a");
        var b = aggregates.Single(x => x.TraceId == "trace-b");
        Assert.Equal(300, a.InputTokens);
        Assert.Equal(700, b.InputTokens);
    }

    // -------------------------------------------------------------------------
    // Endpoint — GET /v1/agents (fleet) with economics fields
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AgentsEndpoint_ConformantSpansWithKnownModel_EconomicsFieldsPopulated()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        // 1M input + 0.5M output => cost = 1000000*(3/1M) + 500000*(15/1M) = 3 + 7.5 = $10.50
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel,
            inputTokens: 1_000_000, outputTokens: 500_000));

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-03-01T00:00:00Z&until=2026-04-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var agentEl = body.RootElement.EnumerateArray().Single(e => e.GetProperty("agent_id").GetString() == agent.ToString());

        Assert.Equal(10.50m, agentEl.GetProperty("estimated_cost_usd").GetDecimal());
        Assert.Equal(1.0,    agentEl.GetProperty("cost_coverage").GetDouble(), precision: 5);
        Assert.Equal(1,      agentEl.GetProperty("costed_span_count").GetInt64());
        Assert.Equal(0,      agentEl.GetProperty("uncosted_span_count").GetInt64());
        Assert.False(        agentEl.GetProperty("pricing_stale").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, agentEl.GetProperty("priced_as_of").ValueKind);
    }

    [Fact]
    public async Task AgentsEndpoint_SpanWithNoTokenData_EstimatedCostIsNull()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel));  // no tokens

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-04-01T00:00:00Z&until=2026-05-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var agentEl = body.RootElement.EnumerateArray().Single(e => e.GetProperty("agent_id").GetString() == agent.ToString());

        Assert.Equal(JsonValueKind.Null, agentEl.GetProperty("estimated_cost_usd").ValueKind);
        Assert.Equal(0.0,  agentEl.GetProperty("cost_coverage").GetDouble());
        Assert.Equal(0,    agentEl.GetProperty("costed_span_count").GetInt64());
        Assert.Equal(1,    agentEl.GetProperty("uncosted_span_count").GetInt64());
    }

    [Fact]
    public async Task AgentsEndpoint_SpanWithUnknownModel_IsUncostedNotZero()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent.ToString(), endedAt: inside,
            provider: "openai", model: "gpt-nonexistent",
            inputTokens: 500, outputTokens: 200));

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-05-01T00:00:00Z&until=2026-06-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var agentEl = body.RootElement.EnumerateArray().Single(e => e.GetProperty("agent_id").GetString() == agent.ToString());

        // Unknown model → uncosted, even though tokens were present
        Assert.Equal(JsonValueKind.Null, agentEl.GetProperty("estimated_cost_usd").ValueKind);
        Assert.Equal(0, agentEl.GetProperty("costed_span_count").GetInt64());
        Assert.Equal(1, agentEl.GetProperty("uncosted_span_count").GetInt64());
    }

    [Fact]
    public async Task AgentsEndpoint_MixedTokenCoverage_PartialCostAndCoverage()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "costed", "t1", agent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 1000, outputTokens: 500));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "uncosted", "t2", agent.ToString(), endedAt: inside));  // no tokens

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-06-01T00:00:00Z&until=2026-07-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var agentEl = body.RootElement.EnumerateArray().Single(e => e.GetProperty("agent_id").GetString() == agent.ToString());

        Assert.Equal(0.5,  agentEl.GetProperty("cost_coverage").GetDouble(), precision: 5);
        Assert.Equal(1,    agentEl.GetProperty("costed_span_count").GetInt64());
        Assert.Equal(1,    agentEl.GetProperty("uncosted_span_count").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, agentEl.GetProperty("estimated_cost_usd").ValueKind);
    }

    [Fact]
    public async Task AgentsEndpoint_SortByEstimatedCostDesc_OrdersCorrectly()
    {
        using var fixture = new CollectorFixture();
        var cheapAgent     = Guid.NewGuid();
        var expensiveAgent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "cheap", "t1", cheapAgent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100, outputTokens: 50));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "expensive", "t2", expensiveAgent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100_000, outputTokens: 50_000));

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-06-01T00:00:00Z&until=2026-08-01T00:00:00Z&sort=estimated_cost_desc");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var agents = body.RootElement.EnumerateArray().ToList();
        var firstCost  = agents.First().GetProperty("estimated_cost_usd").GetDecimal();
        var secondCost = agents.Skip(1).First().GetProperty("estimated_cost_usd").GetDecimal();
        Assert.True(firstCost >= secondCost);
    }

    [Fact]
    public async Task AgentsEndpoint_NullSortedLast_WhenSortByEstimatedCostDesc()
    {
        using var fixture = new CollectorFixture();
        var costlessAgent = Guid.NewGuid();
        var costedAgent   = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);

        // costlessAgent has no token data → estimated_cost_usd = null
        await fixture.Repository.UpsertSpanStateAsync(
            MakeSpanState("p1", "t1", costlessAgent.ToString(), endedAt: inside));
        // costedAgent has token data → has cost
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "p2", "t2", costedAgent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 100, outputTokens: 50));

        using var response = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-07-01T00:00:00Z&until=2026-08-01T00:00:00Z&sort=estimated_cost_desc");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var agents  = body.RootElement.EnumerateArray().ToList();
        var firstId = agents.First().GetProperty("agent_id").GetString();
        Assert.Equal(costedAgent.ToString(), firstId);
    }

    // -------------------------------------------------------------------------
    // Endpoint — GET /v1/agents/{id} with economics fields
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AgentDetailEndpoint_ConformantSpansWithTokens_RollupHasEconomics()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var tStart = new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(
            new TraceState("t1", null, agent.ToString(), "finalized", tStart, tStart.AddMinutes(5), DateTimeOffset.UtcNow));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 2000, outputTokens: 800));

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-03-01T00:00:00Z&until=2026-04-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rollup = body.RootElement.GetProperty("rollup");

        // cost = 2000*(3/1M) + 800*(15/1M) = 0.006 + 0.012 = $0.018
        Assert.Equal(0.018m, rollup.GetProperty("estimated_cost_usd").GetDecimal());
        Assert.Equal(1.0,    rollup.GetProperty("cost_coverage").GetDouble(), precision: 5);
        Assert.Equal(0,      rollup.GetProperty("uncosted_span_count").GetInt64());
    }

    [Fact]
    public async Task AgentDetailEndpoint_RecentTraces_PerTraceCostPopulated()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var tStart = new DateTimeOffset(2026, 4, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(
            new TraceState("t1", null, agent.ToString(), "finalized", tStart, tStart.AddMinutes(3), DateTimeOffset.UtcNow));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "s1", "t1", agent.ToString(), endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 500, outputTokens: 200));

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-04-01T00:00:00Z&until=2026-05-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var trace = body.RootElement.GetProperty("recent_traces").EnumerateArray().Single();

        // cost = 500*(3/1M) + 200*(15/1M) = 0.0015 + 0.003 = $0.0045
        Assert.Equal(0.0045m, trace.GetProperty("estimated_cost_usd").GetDecimal());
        Assert.Equal(1.0,     trace.GetProperty("cost_coverage").GetDouble(), precision: 5);
    }

    [Fact]
    public async Task AgentDetailEndpoint_ErrorTraceSpend_AttributedCorrectly()
    {
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);
        var tStart = new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero);

        await fixture.Repository.UpsertTraceStateAsync(
            new TraceState("error-trace", null, agent.ToString(), "finalized", tStart, tStart.AddMinutes(2), DateTimeOffset.UtcNow));
        await fixture.Repository.UpsertTraceStateAsync(
            new TraceState("clean-trace", null, agent.ToString(), "finalized", tStart.AddHours(1), tStart.AddHours(1).AddMinutes(2), DateTimeOffset.UtcNow));

        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "e1", "error-trace", agent.ToString(), status: "error", endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 1000, outputTokens: 500));
        await fixture.Repository.UpsertSpanStateAsync(MakeSpanState(
            "c1", "clean-trace", agent.ToString(), status: "unset", endedAt: inside,
            provider: KnownProvider, model: KnownModel, inputTokens: 2000, outputTokens: 1000));

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-05-01T00:00:00Z&until=2026-06-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var rollup = body.RootElement.GetProperty("rollup");

        // total: (1000+2000)*(3/1M) + (500+1000)*(15/1M) = 0.009 + 0.0225 = $0.0315
        Assert.Equal(0.0315m, rollup.GetProperty("estimated_cost_usd").GetDecimal());
        // error trace only: 1000*(3/1M) + 500*(15/1M) = 0.003 + 0.0075 = $0.0105
        Assert.Equal(0.0105m, rollup.GetProperty("estimated_error_trace_cost_usd").GetDecimal());
    }

    [Fact]
    public async Task AgentDetailEndpoint_UnknownAgent_EconomicsFieldsAreZeroed()
    {
        using var fixture = new CollectorFixture();
        var unknownAgent = Guid.NewGuid();

        using var response = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{unknownAgent}?since=2020-01-01T00:00:00Z&until=2020-02-01T00:00:00Z");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rollup = body.RootElement.GetProperty("rollup");

        Assert.Equal(JsonValueKind.Null, rollup.GetProperty("estimated_cost_usd").ValueKind);
        Assert.Equal(JsonValueKind.Null, rollup.GetProperty("estimated_error_trace_cost_usd").ValueKind);
        Assert.Equal(0.0, rollup.GetProperty("cost_coverage").GetDouble());
        Assert.Equal(0,   rollup.GetProperty("costed_span_count").GetInt64());
        Assert.Equal(0,   rollup.GetProperty("uncosted_span_count").GetInt64());
        Assert.Equal(JsonValueKind.Null, rollup.GetProperty("priced_as_of").ValueKind);
        Assert.False(rollup.GetProperty("pricing_stale").GetBoolean());
    }

    // -------------------------------------------------------------------------
    // Invariant: no inferred token counts anywhere in the stack
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoTokenData_NeverYieldsNonNullCost()
    {
        // Seed an agent with zero conformant token-bearing spans and verify that
        // estimated_cost_usd is always null — never inferred as a non-null value.
        using var fixture = new CollectorFixture();
        var agent = Guid.NewGuid();
        var inside = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            await fixture.Repository.UpsertSpanStateAsync(
                MakeSpanState($"s{i}", $"t{i}", agent.ToString(), endedAt: inside));
        }

        using var agentsResponse = await fixture.CreateClient().GetAsync(
            "/v1/agents?since=2026-06-01T00:00:00Z&until=2026-08-01T00:00:00Z");
        using var agentsBody = await JsonDocument.ParseAsync(await agentsResponse.Content.ReadAsStreamAsync());

        var agentEl = agentsBody.RootElement.EnumerateArray().Single(e => e.GetProperty("agent_id").GetString() == agent.ToString());
        Assert.Equal(JsonValueKind.Null, agentEl.GetProperty("estimated_cost_usd").ValueKind);

        using var detailResponse = await fixture.CreateClient().GetAsync(
            $"/v1/agents/{agent}?since=2026-06-01T00:00:00Z&until=2026-08-01T00:00:00Z");
        using var detailBody = await JsonDocument.ParseAsync(await detailResponse.Content.ReadAsStreamAsync());
        Assert.Equal(JsonValueKind.Null, detailBody.RootElement.GetProperty("rollup").GetProperty("estimated_cost_usd").ValueKind);
    }

    // -------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _dbPath;
        public EventRepository Repository { get; }

        public RepositoryFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"clawindex-econ-test-{Guid.NewGuid():N}.db");
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Clawindex:DatabasePath"] = _dbPath })
                .Build();
            Repository = new EventRepository(config);
            Repository.InitializeAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }
}
