// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Operations;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Tuning;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Tuning;

public sealed class EvolutionStrategyTests
{
    private static double Sphere(IReadOnlyList<double> x, IReadOnlyList<double> centre) =>
        -x.Zip(centre, static (a, c) => (a - c) * (a - c)).Sum();

    private static List<double[]> Run(ulong seed, int generations, out EvolutionStrategy es)
    {
        double[] centre = [0.3, 0.7, 0.5, 0.9, 0.1];
        es = new EvolutionStrategy([0.5, 0.5, 0.5, 0.5, 0.5], 0.2, 8, seed);
        List<double[]> drawn = [];
        for (int g = 0; g < generations; g++)
        {
            IReadOnlyList<double[]> points = es.Ask();
            drawn.AddRange(points);
            es.Tell([.. points.Select(p => Sphere(p, centre))]);
        }
        return drawn;
    }

    [Fact]
    public void SameSeed_DrawsAndUpdatesIdentically()
    {
        List<double[]> a = Run(42, 10, out EvolutionStrategy esA);
        List<double[]> b = Run(42, 10, out EvolutionStrategy esB);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
        Assert.Equal(esA.Mean, esB.Mean);
        Assert.Equal(esA.Sigma, esB.Sigma);
        Assert.Equal(esA.History, esB.History);
    }

    [Fact]
    public void DifferentSeed_DrawsDifferently()
    {
        List<double[]> a = Run(1, 1, out _);
        List<double[]> b = Run(2, 1, out _);
        Assert.NotEqual(a[0], b[0]);
    }

    [Fact]
    public void Samples_StayInsideTheUnitCube_EvenFromACornerWithALargeStep()
    {
        EvolutionStrategy es = new([0.0, 1.0, 0.0, 1.0], 0.5, 16, 7);
        for (int g = 0; g < 20; g++)
        {
            IReadOnlyList<double[]> points = es.Ask();
            Assert.All(points, p => Assert.All(p, v => Assert.InRange(v, 0.0, 1.0)));
            // Fitness pushes outward, so the search keeps pressing against the bounds.
            es.Tell([.. points.Select(static p => p[0] - p[1] + p[2] - p[3])]);
            Assert.All(es.Mean, v => Assert.InRange(v, 0.0, 1.0));
        }
    }

    [Theory]
    [InlineData(1.3, 0.7)]
    [InlineData(-0.2, 0.2)]
    [InlineData(2.4, 0.4)]
    [InlineData(-1.25, 0.75)]
    [InlineData(0.5, 0.5)]
    public void Reflect_FoldsIntoTheUnitInterval(double input, double expected) =>
        Assert.Equal(expected, EvolutionStrategy.Reflect(input), 12);

    [Fact]
    public void ToyProblem_ConvergesToTheOptimum()
    {
        double[] centre = [0.3, 0.7, 0.5, 0.9, 0.1];
        Run(3, 80, out EvolutionStrategy es);
        double distance = Math.Sqrt(-Sphere(es.Mean, centre));
        Assert.True(distance < 0.01, $"Mean {string.Join(", ", es.Mean)} is {distance} from the optimum.");
        Assert.True(es.Sigma < 0.05, $"Step size {es.Sigma} did not shrink.");
        Assert.True(es.History[^1].BestFitness > es.History[0].BestFitness);
    }

    [Fact]
    public void ToyProblem_WithBadlyScaledCoordinates_Converges()
    {
        // One coordinate 100x more sensitive than another: the diagonal covariance must learn per-coordinate scales.
        double[] centre = [0.2, 0.8, 0.6, 0.4, 0.35, 0.65, 0.5, 0.25];
        double[] scale = [100, 1, 10, 1, 30, 1, 3, 1];
        EvolutionStrategy es = new([.. Enumerable.Repeat(0.5, 8)], 0.2, 10, 11);
        for (int g = 0; g < 200; g++)
        {
            IReadOnlyList<double[]> points = es.Ask();
            es.Tell([.. points.Select(p => -p.Select((v, j) => scale[j] * (v - centre[j]) * (v - centre[j])).Sum())]);
        }
        Assert.All(Enumerable.Range(0, 8), j => Assert.InRange(es.Mean[j], centre[j] - 0.02, centre[j] + 0.02));
    }

    [Fact]
    public void AskAndTell_MustAlternate()
    {
        EvolutionStrategy es = new([0.5, 0.5], 0.1, 4, 1);
        Assert.Throws<InvalidOperationException>(() => es.Tell([0, 0, 0, 0]));
        es.Ask();
        Assert.Throws<InvalidOperationException>(() => es.Ask());
        Assert.Throws<ArgumentException>(() => es.Tell([0, 0]));
    }
}

public sealed class TuningSpaceTests
{
    private static TuningSpace Space() => new(PlaybookLibrary.LoadAuthored().All, new OperationalOptions(), new FeatureOptions());

    [Fact]
    public void DefaultPoint_DecodesBackToTheAuthoredDefaults()
    {
        TuningSpace space = Space();
        TunedParameterSet set = space.Decode(space.DefaultPoint());
        IReadOnlyList<Playbook> tuned = set.ApplyTo(PlaybookLibrary.LoadAuthored().All);
        Assert.Equal(PlaybookLibrary.LoadAuthored().All.SelectMany(static p => p.Parameters.Select(static q => q.Default)),
            tuned.SelectMany(static p => p.Parameters.Select(static q => q.Default)));
        Assert.Equal(new OperationalOptions(), set.ApplyTo(new OperationalOptions()));
        Assert.Equal(new FeatureOptions(), set.ApplyTo(new FeatureOptions()));
    }

    [Fact]
    public void EveryPointTheSearchCanDraw_DecodesInsideDeclaredRanges()
    {
        TuningSpace space = Space();
        EvolutionStrategy es = new(space.DefaultPoint(), 0.5, 12, 99);
        for (int g = 0; g < 10; g++)
        {
            IReadOnlyList<double[]> points = es.Ask();
            foreach (double[] point in points)
            {
                TunedParameterSet set = space.Decode(point);
                foreach (Playbook playbook in set.ApplyTo(PlaybookLibrary.LoadAuthored().All))
                {
                    Assert.All(playbook.Parameters, q => Assert.InRange(q.Default, q.Min, q.Max));
                }
                OperationalOptions ops = set.ApplyTo(new OperationalOptions());
                foreach (OptionKnob knob in TuningKnobs.Operational) Assert.InRange(TuningKnobs.Get(ops, knob.Name), knob.Min, knob.Max);
                FeatureOptions features = set.ApplyTo(new FeatureOptions());
                foreach (OptionKnob knob in TuningKnobs.Features) Assert.InRange(TuningKnobs.Get(features, knob.Name), knob.Min, knob.Max);
                foreach (TuningDimension d in space.Dimensions.Where(static d => d.Integer))
                {
                    double v = set.Operational[d.Name];
                    Assert.Equal(Math.Round(v), v);
                }
            }
            es.Tell([.. points.Select(static p => p.Sum())]);
        }
    }

    [Fact]
    public void Space_SearchesOnlyConsumedPlaybookParameters()
    {
        TuningSpace space = Space();
        Assert.All(space.Dimensions.Where(static d => d.Scope == "playbook"), d => Assert.Contains(d.Name, TuningKnobs.ConsumedPlaybookParameters));
        Assert.Contains("allied-harass.harassIntervalSeconds", space.InertPlaybookParameters);
        Assert.Equal(space.Dimensions.Count, space.Dimensions.Select(static d => d.Key).Distinct().Count());
    }

    [Fact]
    public void ApplyTo_ClampsOutOfRangeValues_AndRejectsUnknownNames()
    {
        TunedParameterSet set = TunedParameterSet.None with
        {
            Playbooks = new Dictionary<string, IReadOnlyDictionary<string, double>> { ["allied-grizzly-timing"] = new Dictionary<string, double> { ["attackArmyValue"] = 99999 } },
            Operational = new Dictionary<string, double> { ["MaxDefenses"] = -5 },
        };
        Playbook grizzly = set.ApplyTo(PlaybookLibrary.LoadAuthored().All).Single(static p => p.Id == "allied-grizzly-timing");
        Assert.Equal(3000, grizzly.Parameters.Single().Default);
        Assert.Equal(0, set.ApplyTo(new OperationalOptions()).MaxDefenses);

        TunedParameterSet unknown = TunedParameterSet.None with { Operational = new Dictionary<string, double> { ["NoSuchKnob"] = 1 } };
        Assert.Throws<InvalidDataException>(() => unknown.ApplyTo(new OperationalOptions()));
        TunedParameterSet unknownParameter = TunedParameterSet.None with
        {
            Playbooks = new Dictionary<string, IReadOnlyDictionary<string, double>> { ["allied-boom"] = new Dictionary<string, double> { ["nope"] = 1 } },
        };
        Assert.Throws<InvalidDataException>(() => unknownParameter.ApplyTo(PlaybookLibrary.LoadAuthored().All));
    }

    [Fact]
    public void EmbeddedSet_ParsesAndIsAppliedOnlyWhenAdopted()
    {
        TunedParameterSet embedded = TunedParameterSet.Embedded;
        // Applying the embedded set must not throw: every name in it still exists.
        embedded.ApplyTo(PlaybookLibrary.LoadAuthored().All);
        embedded.ApplyTo(new OperationalOptions());
        embedded.ApplyTo(new FeatureOptions());
        IReadOnlyList<Playbook> expected = embedded.Adopted ? embedded.ApplyTo(PlaybookLibrary.LoadAuthored().All) : PlaybookLibrary.LoadAuthored().All;
        Assert.Equal(expected.SelectMany(static p => p.Parameters.Select(static q => q.Default)),
            PlaybookLibrary.LoadDefault().All.SelectMany(static p => p.Parameters.Select(static q => q.Default)));
        if (embedded.Provenance is not null) Assert.NotNull(embedded.Validation);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        TuningSpace space = Space();
        TunedParameterSet set = space.Decode([.. space.DefaultPoint().Select(static v => Math.Min(1, v + 0.1))]);
        TunedParameterSet back = TunedParameterSet.LoadJson(set.ToJson());
        Assert.Equal(set.ToJson(), back.ToJson());
    }
}
