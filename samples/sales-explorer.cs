#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;
using static Reverb.Notebook;
using System.Globalization;

// %% [markdown] id=intro
// # Sales explorer
//
// A year of made-up daily sales, read from `data/daily-sales.csv` next to this file. Pick a region
// and a smoothing window: only the cells that use them run again.

// %% id=load
var sales = File.ReadLines(Path.Combine(Notebook.Folder, "data", "daily-sales.csv"))
    .Skip(1)
    .Select(line => line.Split(','))
    .Select(f => new Sale(
        DateOnly.Parse(f[0], CultureInfo.InvariantCulture),
        f[1],
        f[2],
        int.Parse(f[3], CultureInfo.InvariantCulture),
        decimal.Parse(f[4], CultureInfo.InvariantCulture)))
    .ToList();
Md($"Loaded **{sales.Count:N0}** rows, {sales.Min(s => s.Date):MMM d} to {sales.Max(s => s.Date):MMM d, yyyy}.");

// %% id=controls
var region = Ui.Dropdown("Region", ["All", "West", "East", "Central"], value: "All");
var window = Ui.Slider("Smoothing (days)", min: 1, max: 30, value: 7);

// %% id=picked
var picked = sales.Where(s => region.Value == "All" || s.Region == region.Value).ToList();
var daily = picked
    .GroupBy(s => s.Date)
    .OrderBy(g => g.Key)
    .Select(g => new DayTotal(g.Key, g.Sum(s => s.Revenue)))
    .ToList();

// %% id=trend
// Each day's revenue averaged with the days before it, so the weekly ups and downs smooth out.
var days = (int)window.Value;
var smoothed = daily
    .Select((d, i) => new DayTotal(d.Date, Math.Round(daily.Skip(Math.Max(0, i - days + 1)).Take(Math.Min(days, i + 1)).Average(x => x.Revenue), 2)))
    .ToList();
Chart.Line(smoothed, x: d => d.Date, y: d => d.Revenue, title: $"Daily revenue, {days}-day average ({region.Value})");

// %% id=products
var byProduct = picked
    .GroupBy(s => s.Product)
    .Select(g => new ProductTotal(g.Key, g.Sum(s => s.Units), g.Sum(s => s.Revenue)))
    .OrderByDescending(p => p.Revenue)
    .ToList();
Chart.Bar(byProduct, x: p => p.Product, y: p => p.Revenue, title: "Revenue by product");

// %% id=months
picked
    .GroupBy(s => s.Date.Month)
    .Select(g => new MonthTotal(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(g.Key), g.Sum(s => s.Units), g.Sum(s => s.Revenue)))
    .Dump("By month");

// %% types
record Sale(DateOnly Date, string Region, string Product, int Units, decimal Revenue);
record DayTotal(DateOnly Date, decimal Revenue);
record ProductTotal(string Product, int Units, decimal Revenue);
record MonthTotal(string Month, int Units, decimal Revenue);
