using System.Text;
using System.Text.RegularExpressions;
using Reverb.Format;
using Reverb.Kernel;
using Reverb.Testing;

namespace Reverb.Consistency.Tests;

/// <summary>One thing that happens to an open notebook: a new version of the file, or a widget change.</summary>
public abstract record Step;
public sealed record UpdateStep(string Text) : Step;
public sealed record WidgetStep(string WidgetId, double Value) : Step;

/// <summary>A generated scenario: the notebook it opens with and what happens to it.</summary>
public sealed record Scenario(int Seed, string Initial, IReadOnlyList<Step> Steps);

/// <summary>Random small notebooks and random edits to them.</summary>
public sealed partial class Fuzzer(int seed)
{
    sealed class FCell
    {
        public required string Id;
        public required List<string> Lines;
        public bool Disabled;
        public FCell Clone() => new() { Id = Id, Lines = [.. Lines], Disabled = Disabled };
    }

    readonly Random rng = new(seed);
    List<FCell> cells = [];
    string? types;
    int nextVar;
    int nextCell;

    static readonly string[] TypesVersions =
    [
        "record P(int A);",
        "record P(int A)\n{\n    public int Twice => A * 2;\n}",
    ];

    public Scenario Generate(int maxSteps)
    {
        if (rng.Next(3) == 0) types = TypesVersions[0];
        var count = rng.Next(3, 8);
        for (var i = 0; i < count; i++) cells.Add(NewCell(cells.Count));
        var initial = Render();

        var steps = new List<Step>();
        var stepCount = rng.Next(2, maxSteps + 1);
        for (var i = 0; i < stepCount; i++)
        {
            if (Edit() is { } step) steps.Add(step);
        }
        return new Scenario(seed, initial, steps);
    }

    // ---- notebook model -----------------------------------------------------------------------

    string Render() => NotebookText.Build(
        [.. cells.Select(c => string.Join("\n", c.Lines))],
        types,
        ids: [.. cells.Select(c => c.Id)],
        attributes: [.. cells.Select(c => c.Disabled ? "disabled" : "")]);

    [GeneratedRegex(@"var (\w+) = ")]
    private static partial Regex Declaration();

    /// <summary>Variables declared above position <paramref name="index"/>, with a rough kind.</summary>
    List<(string Name, string Kind)> VariablesBefore(int index)
    {
        var vars = new List<(string, string)>();
        foreach (var cell in cells.Take(index))
        {
            foreach (var line in cell.Lines)
            {
                var m = Declaration().Match(line);
                if (!m.Success) continue;
                var kind = line.Contains("Ui.Slider") ? "slider"
                    : line.Contains("new P(") ? "p"
                    : line.Contains("ToList()") ? "list"
                    : line.Contains("$\"") ? "string"
                    : "int";
                vars.Add((m.Groups[1].Value, kind));
            }
        }
        return vars;
    }

    FCell NewCell(int position)
    {
        var vars = VariablesBefore(position);
        var lines = new List<string> { NewLine(vars) };
        if (rng.Next(4) == 0) lines.Add(NewLine(vars));
        return new FCell { Id = $"k{nextCell++}", Lines = lines };
    }

    string NewLine(List<(string Name, string Kind)> vars)
    {
        var name = $"v{nextVar++}";
        var choice = rng.Next(10);
        switch (choice)
        {
            case 0 when vars.Count > 0:
                return $"Console.WriteLine({Pick(vars).Name});";
            case 1 when vars.Count > 0:
                var v = Pick(vars).Name;
                return $"{v}.Dump(\"{v}\");";
            case 2:
                return $"var {name} = $\"{Interpolation(vars)}\";";
            case 3:
                return $"var {name} = Enumerable.Range(0, {rng.Next(1, 5)}).Select(i => i + {IntExpr(vars)}).ToList();";
            case 4 when types is not null:
                return $"var {name} = new P({IntExpr(vars)});";
            case 5 when !cells.Any(c => c.Lines.Any(l => l.Contains("Ui.Slider"))) || rng.Next(3) == 0:
                return $"var {name} = Ui.Slider(\"{name}\", min: 0, max: 100, value: {rng.Next(0, 101)});";
            default:
                return $"var {name} = {IntExpr(vars)};";
        }
    }

    string IntExpr(List<(string Name, string Kind)> vars)
    {
        string Term()
        {
            if (vars.Count == 0 || rng.Next(3) == 0) return rng.Next(0, 50).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (name, kind) = Pick(vars);
            return kind switch
            {
                "int" => name,
                "list" => $"{name}.Count",
                "slider" => $"(int){name}.Value",
                "p" => $"{name}.A",
                _ => $"{name}.Length",
            };
        }
        return rng.Next(3) switch
        {
            0 => Term(),
            1 => $"{Term()} + {Term()}",
            _ => $"{Term()} * 2 + {Term()}",
        };
    }

    string Interpolation(List<(string Name, string Kind)> vars)
    {
        if (vars.Count == 0) return "s" + rng.Next(100);
        return string.Join("-", Enumerable.Range(0, rng.Next(1, 3)).Select(_ => "{" + Pick(vars).Name + "}"));
    }

    T Pick<T>(IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    // ---- edits --------------------------------------------------------------------------------

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex IntLiteral();

    Step? Edit()
    {
        if (cells.Count == 0)
        {
            cells.Add(NewCell(0));
            return new UpdateStep(Render());
        }

        switch (rng.Next(11))
        {
            case 0 or 1: // change a literal
            {
                var cell = Pick(cells);
                var i = rng.Next(cell.Lines.Count);
                var literals = IntLiteral().Matches(cell.Lines[i]);
                if (literals.Count == 0) return null;
                var m = literals[rng.Next(literals.Count)];
                cell.Lines[i] = cell.Lines[i][..m.Index] + rng.Next(0, 60) + cell.Lines[i][(m.Index + m.Length)..];
                break;
            }
            case 2: // add a cell
            {
                var at = rng.Next(cells.Count + 1);
                cells.Insert(at, NewCell(at));
                break;
            }
            case 3: // delete a cell
                cells.RemoveAt(rng.Next(cells.Count));
                break;
            case 4: // rename a variable everywhere
            {
                var vars = VariablesBefore(cells.Count);
                if (vars.Count == 0) return null;
                var old = Pick(vars).Name;
                var renamed = $"{old}r{nextVar++}";
                var word = new Regex($@"\b{old}\b");
                foreach (var c in cells) for (var i = 0; i < c.Lines.Count; i++) c.Lines[i] = word.Replace(c.Lines[i], renamed);
                break;
            }
            case 5: // types cell
                types = types is null ? TypesVersions[0] : types == TypesVersions[0] ? TypesVersions[1] : TypesVersions[0];
                break;
            case 6: // move a widget
            {
                var sliders = cells.Where(c => c.Lines.Any(l => l.Contains("Ui.Slider"))).ToList();
                if (sliders.Count == 0) return null;
                return new WidgetStep($"{Pick(sliders).Id}:0", rng.Next(0, 101));
            }
            case 7: // break or fix a cell
            {
                var cell = Pick(cells);
                if (cell.Lines.Remove("var oops = ;")) break;
                cell.Lines.Add("var oops = ;");
                break;
            }
            case 8: // disable or enable
                Pick(cells).Disabled ^= true;
                break;
            case 9: // swap two neighbours (may break declare-before-use; that's fine)
            {
                if (cells.Count < 2) return null;
                var i = rng.Next(cells.Count - 1);
                (cells[i], cells[i + 1]) = (cells[i + 1], cells[i]);
                break;
            }
            default: // rewrite a cell
            {
                var i = rng.Next(cells.Count);
                var fresh = NewCell(i);
                cells[i] = new FCell { Id = cells[i].Id, Lines = fresh.Lines };
                break;
            }
        }
        return new UpdateStep(Render());
    }
}

/// <summary>Runs a scenario reactively and compares the result with a fresh run-all.</summary>
public static class Checker
{
    public sealed record Result(bool Consistent, string Difference);

    public static async Task<Result> CheckAsync(Scenario scenario, bool checkEveryStep = false)
    {
        var reactive = new KernelSession();
        try
        {
            await reactive.OpenAsync(NotebookDocument.Parse(scenario.Initial));
            var text = scenario.Initial;
            for (var i = 0; i < scenario.Steps.Count; i++)
            {
                switch (scenario.Steps[i])
                {
                    case UpdateStep u:
                        text = u.Text;
                        await reactive.UpdateAsync(NotebookDocument.Parse(text));
                        break;
                    case WidgetStep w:
                        await reactive.SetWidgetAsync(w.WidgetId, w.Value);
                        break;
                }
                if (checkEveryStep || i == scenario.Steps.Count - 1)
                {
                    var diff = await CompareWithFreshAsync(reactive, text);
                    if (diff is not null) return new Result(false, $"after step {i + 1}: {diff}");
                }
            }
            if (scenario.Steps.Count == 0 && await CompareWithFreshAsync(reactive, text) is { } d0)
                return new Result(false, d0);
            return new Result(true, "");
        }
        finally
        {
            await reactive.DisposeAsync();
        }
    }

    static async Task<string?> CompareWithFreshAsync(KernelSession reactive, string text)
    {
        var fresh = new KernelSession();
        try
        {
            fresh.SeedWidgetState(reactive.WidgetState);
            await fresh.OpenAsync(NotebookDocument.Parse(text));
            var a = Describe(reactive);
            var b = Describe(fresh);
            if (a == b) return null;
            return FirstDifference(a, b);
        }
        finally
        {
            await fresh.DisposeAsync();
        }
    }

    /// <summary>Everything the invariant covers: per-cell status and outputs, and every stored value.</summary>
    public static string Describe(KernelSession s)
    {
        var sb = new StringBuilder();
        foreach (var cell in s.Analysis!.Cells.Where(c => c.Kind == CellKind.Code))
        {
            sb.Append($"cell {cell.Key}: {s.StatusOf(cell.Key)}\n");
            foreach (var item in s.OutputsOf(cell.Key))
            {
                sb.Append(item switch
                {
                    StreamItem st => $"  {st.Stream}: {st.Text.Replace("\n", "\\n")}\n",
                    DisplayItem d => $"  display {d.Output.Title}: {d.Output.Text.Replace("\n", "\\n")}\n",
                    _ => "  ?\n",
                });
            }
        }
        foreach (var (name, value) in s.Store.Snapshot().OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"value {name} = {value?.GetType().FullName}: {TextFormatter.Format(value).Replace("\n", "\\n")}\n");
        return sb.ToString();
    }

    static string FirstDifference(string reactive, string fresh)
    {
        var a = reactive.Split('\n');
        var b = fresh.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<none>";
            var y = i < b.Length ? b[i] : "<none>";
            if (x != y) return $"reactive `{x}` but fresh run-all `{y}`\n--- reactive ---\n{reactive}--- fresh ---\n{fresh}";
        }
        return "(same text, different length)";
    }

    /// <summary>Drops steps one at a time while the scenario still fails, to get a small reproduction.</summary>
    public static async Task<Scenario> ShrinkAsync(Scenario failing)
    {
        var current = failing;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < current.Steps.Count; i++)
            {
                var steps = current.Steps.Where((_, j) => j != i).ToList();
                var candidate = current with { Steps = steps };
                // The last update step decides the final file; keep at least one step.
                if (steps.Count == 0) continue;
                if (!(await CheckAsync(candidate)).Consistent)
                {
                    current = candidate;
                    changed = true;
                    break;
                }
            }
        }
        return current;
    }

    public static string Serialize(Scenario s)
    {
        var sb = new StringBuilder();
        sb.Append($"#seed {s.Seed}\n#open\n").Append(s.Initial);
        foreach (var step in s.Steps)
        {
            switch (step)
            {
                case UpdateStep u: sb.Append("#update\n").Append(u.Text); break;
                case WidgetStep w: sb.Append($"#widget {w.WidgetId} {w.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"); break;
            }
        }
        return sb.ToString();
    }

    public static Scenario Deserialize(string text)
    {
        var seed = 0;
        string? initial = null;
        var steps = new List<Step>();
        var block = new StringBuilder();
        string? mode = null;

        void Close()
        {
            if (mode == "open") initial = block.ToString();
            else if (mode == "update") steps.Add(new UpdateStep(block.ToString()));
            block.Clear();
        }

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("#seed ", StringComparison.Ordinal)) { seed = int.Parse(line[6..], System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (line == "#open" || line == "#update") { Close(); mode = line[1..]; continue; }
            if (line.StartsWith("#widget ", StringComparison.Ordinal))
            {
                Close();
                mode = null;
                var parts = line.Split(' ');
                steps.Add(new WidgetStep(parts[1], double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            if (mode is not null) block.Append(line).Append('\n');
        }
        Close();
        // A replayed text can differ from the original by a trailing blank line, which doesn't change its meaning.
        return new Scenario(seed, initial ?? "", steps);
    }
}
