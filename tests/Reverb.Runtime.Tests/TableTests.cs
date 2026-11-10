namespace Reverb.Runtime.Tests;

public class TableTests
{
    sealed record Row(string Name, string[] Tags, List<int> Numbers);

    [Fact]
    public void A_list_in_a_table_cell_shows_its_items()
    {
        var table = TextFormatter.Table(new[] { new Row("a", ["x", "y"], [.. Enumerable.Range(1, 12)]), new Row("b", [], [1]) })!;
        Assert.Equal(["a", "x, y", "1, 2, 3, 4, 5, 6, 7, 8, 9, 10, … (12 in all)"], table.Rows[0]);
        Assert.Equal(["b", "(empty)", "1"], table.Rows[1]);
    }
}
