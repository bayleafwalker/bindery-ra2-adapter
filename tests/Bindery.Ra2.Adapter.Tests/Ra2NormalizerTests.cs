// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class Ra2NormalizerTests
{
    [Theory]
    [InlineData(Ra2TelemetryEventTypes.ProductionChanged, "game.production.changed")]
    [InlineData(Ra2TelemetryEventTypes.ProductionCompleted, "game.production.completed")]
    public void NormalizesProductionEvents(string rawType, string expectedNormalizedType)
    {
        Ra2Normalizer normalizer = new();
        RawObservation observation = Observation(rawType);

        NormalizedObservation normalized = Assert.Single(normalizer.Normalize([observation]));

        Assert.Equal(expectedNormalizedType, normalized.EventType);
    }

    private static RawObservation Observation(string type)
    {
        using JsonDocument document = JsonDocument.Parse("{\"house\":\"Americans\",\"type\":\"GAPOWR\"}");
        return new RawObservation("event-1", "capture-1", 1, type, "adapter", "0.1.0", DateTimeOffset.UtcNow, document.RootElement.Clone(), "sha256:raw");
    }
}
