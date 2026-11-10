#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;
using static Reverb.Notebook;

// %% [markdown] id=intro
// # A tour of LINQ
//
// LINQ is a handful of methods that work on any sequence: `Where` filters, `Select` reshapes,
// `OrderBy` sorts, `GroupBy` buckets, `Join` matches two sequences up, and `Count`, `Sum` and
// friends boil a sequence down to one value.
//
// Each cell below shows one idea on a small, made-up library. Change a cell and run it
// (Ctrl+Enter): the cells that use what it made run again, and nothing else does.

// %% id=data
var books = new List<Book>
{
    new("A Field Guide to Puddles", 1, 1998, 212, ["nature", "humor"]),
    new("The Quiet Compiler", 2, 2004, 388, ["programming"]),
    new("Ninety Ways to Fold a Map", 3, 2011, 156, ["travel", "humor"]),
    new("Night Trains of the North", 3, 2016, 301, ["travel", "history"]),
    new("Practical Clouds", 1, 2019, 240, ["nature", "science"]),
    new("Loops and Other Circles", 2, 2021, 412, ["programming", "math"]),
    new("A Short History of Spoons", 4, 1987, 198, ["history"]),
    new("Ten Thousand Tests", 2, 2024, 455, ["programming"]),
};
var authors = new List<Author>
{
    new(1, "Mara Lind", "Sweden"),
    new(2, "Tomás Ferreira", "Portugal"),
    new(3, "Ada Okafor", "Nigeria"),
    new(4, "Henry Vale", "Canada"),
};

// %% [markdown] id=where-md
// ## Where: keep the items that match
//
// The slider sets the earliest year. Only the next cell reads it, so only that cell runs when it moves.

// %% id=since
var since = Ui.Slider("Published in or after", min: 1985, max: 2025, value: 2010);

// %% id=where
var recent = books.Where(b => b.Year >= since.Value).ToList();
recent.Dump($"{recent.Count} books since {since.Value}");

// %% [markdown] id=select-md
// ## Select: turn each item into something else
//
// Here, a short line of text per book. `Select` can also give you the position, with `(b, i) => ...`.

// %% id=select
books.Select((b, i) => $"{i + 1}. {b.Title} ({b.Pages} pages)").Dump("Titles");

// %% [markdown] id=order-md
// ## OrderBy and ThenBy: sort by one key, then another

// %% id=order
books.OrderBy(b => b.AuthorId).ThenByDescending(b => b.Year).Select(b => new TitleYear(b.Title, b.Year)).Dump("By author, newest first");

// %% [markdown] id=group-md
// ## GroupBy: put items in buckets
//
// Each group has a `Key` and is itself a sequence, so you can count or sum it. Here, books per decade.
//
// Anonymous types (`new { Decade = ..., Books = ... }`) are fine inside one cell. To hand a result to
// another cell, use a record from the types cell: Reverb points this out (RVB002) and has a fix
// that writes the record for you.

// %% id=decades
var decades = books
    .GroupBy(b => b.Year / 10 * 10)
    .OrderBy(g => g.Key)
    .Select(g => new Decade($"{g.Key}s", g.Count(), g.Sum(b => b.Pages)))
    .ToList();
decades.Dump("Books per decade");

// %% id=decade-chart
Chart.Bar(decades, x: d => d.Name, y: d => d.Books, title: "Books per decade");

// %% [markdown] id=join-md
// ## Join: match items from two sequences
//
// Books carry an `AuthorId`; `Join` pairs each book with the author whose `Id` matches.

// %% id=join
books.Join(authors, b => b.AuthorId, a => a.Id, (b, a) => new BookByAuthor(b.Title, a.Name, a.Country)).Dump("Who wrote what");

// %% [markdown] id=aggregate-md
// ## Count, Sum, Average, MaxBy: one value from many

// %% id=aggregate
Md($"""
    - {books.Count(b => b.Tags.Contains("programming"))} books about programming
    - {books.Sum(b => b.Pages):N0} pages in all, {books.Average(b => b.Pages):N0} on average
    - The longest is *{books.MaxBy(b => b.Pages)!.Title}*
    """);

// %% [markdown] id=selectmany-md
// ## SelectMany: flatten a sequence of sequences
//
// Every book has a few tags. `SelectMany` lists them all in one sequence, then `CountBy` counts each.

// %% id=tags
books.SelectMany(b => b.Tags).CountBy(t => t).OrderByDescending(p => p.Value).Select(p => new TagCount(p.Key, p.Value)).Dump("Tags");

// %% [markdown] id=lazy-md
// ## Queries are lazy until you ask for the results
//
// A query is a recipe, not a result: it runs each time you enumerate it. `ToList()` runs it once and
// keeps the answer. That's why the cells above end their queries with `ToList()` before other cells
// use them.

// %% id=lazy
var runs = 0;
var query = books.Where(b => { runs++; return b.Pages > 300; });
var first = query.Count();
var second = query.Count();
var once = query.ToList();
Console.WriteLine($"Counted {first} and {second} long books; the filter ran {runs} times for {books.Count} books.");

// %% types
record Book(string Title, int AuthorId, int Year, int Pages, string[] Tags);
record Author(int Id, string Name, string Country);
record TitleYear(string Title, int Year);
record Decade(string Name, int Books, int Pages);
record BookByAuthor(string Title, string Author, string Country);
record TagCount(string Tag, int Books);
