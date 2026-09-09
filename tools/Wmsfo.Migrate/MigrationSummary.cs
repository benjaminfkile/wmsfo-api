namespace Wmsfo.Migrate;

// Every step reports a small count triple: how many source rows it considered,
// how many it inserted (or updated), and how many it skipped as already
// present or invalid. The tests read this to prove that the second run has
// Inserted == 0 for every step.
public sealed class MigrationSummary
{
    public List<StepResult> Steps { get; } = new();
    public bool DryRun { get; init; }

    public StepResult AddStep(string name)
    {
        var s = new StepResult { Name = name };
        Steps.Add(s);
        return s;
    }

    public StepResult? Step(string name) => Steps.FirstOrDefault(s => s.Name == name);
    public int TotalInserted => Steps.Sum(s => s.Inserted);
    public int TotalSkipped => Steps.Sum(s => s.Skipped);
    public int TotalConsidered => Steps.Sum(s => s.Considered);
}

public sealed class StepResult
{
    public string Name { get; set; } = "";
    public int Considered { get; set; }
    public int Inserted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Notes { get; } = new();

    public override string ToString() =>
        $"{Name}: considered={Considered} inserted={Inserted} skipped={Skipped} failed={Failed}";
}
