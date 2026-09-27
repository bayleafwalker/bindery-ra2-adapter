// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The distilled arm is only a distilled arm when it has something to distil: its teacher always plays the training
/// maps whatever <c>--maps</c> says, and a dataset below <see cref="Program.MinDistillExamples"/> skips the arm
/// instead of letting it escalate every decision to the LLM under a "distilled" label.
/// </summary>
public sealed class DistillTeacherMapsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"bindery-distil-maps-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void On_heldout_maps_the_teacher_still_plays_the_training_maps()
    {
        Assert.Equal(0, Program.Main(["run", "--arms", "llm,distilled", "--maps", "heldout", "--opponents", "live-rush", "--seeds", "1",
            "--benchmark", "contested", "--max-seconds", "300", "--llm-fake", "--no-decisions", "--out", dir]));

        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
        List<JsonElement> distilled = [.. results.RootElement.EnumerateArray().Where(static m => m.GetProperty("arm").GetString() == "distilled")];
        Assert.NotEmpty(distilled);
        Assert.All(distilled, static m => Assert.Equal("heldout", m.GetProperty("split").GetString()));
        string label = distilled[0].GetProperty("players").GetProperty("arm").GetProperty("labels").EnumerateArray()
            .Select(static l => l.GetString()!).Single(static l => l.StartsWith("distilled-from:", StringComparison.Ordinal));
        Assert.Contains("training maps", label, StringComparison.Ordinal);
        Assert.DoesNotContain("(0 examples)", label, StringComparison.Ordinal);
        int examples = int.Parse(label[(label.LastIndexOf('(') + 1)..label.IndexOf(" examples", StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(examples >= Program.MinDistillExamples, label);
        // Distilled decisions are answered from the dataset, not all escalated.
        JsonElement arm = distilled[0].GetProperty("players").GetProperty("arm");
        Assert.True(arm.GetProperty("distilledEscalations").GetInt32() < arm.GetProperty("distilledDecisions").GetInt32());
    }

    [Fact]
    public void A_dataset_below_the_minimum_skips_the_arm_with_a_reason()
    {
        Assert.NotNull(Program.DistillSkipReason(DecisionDataset.Empty, "test"));
        Assert.Contains("0 examples", Program.DistillSkipReason(DecisionDataset.Empty, "test"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_dataset_file_skips_the_distilled_arm_instead_of_running_it_as_an_llm_arm()
    {
        Directory.CreateDirectory(dir);
        string empty = Path.Combine(dir, "empty.ndjson");
        File.WriteAllText(empty, string.Empty);

        Assert.Equal(0, Program.Main(["run", "--arms", "distilled", "--maps", "heldout", "--opponents", "live-rush", "--seeds", "1",
            "--max-seconds", "60", "--llm-fake", "--no-decisions", "--dataset", empty, "--out", dir]));

        using JsonDocument results = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "results.json")));
        Assert.Empty(results.RootElement.EnumerateArray());
        Assert.Contains("**distilled**: skipped", File.ReadAllText(Path.Combine(dir, "report.md")), StringComparison.Ordinal);
    }
}
