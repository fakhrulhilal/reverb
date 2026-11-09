#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% [markdown] id=intro
// # Sales explorer
// Drag the slider to change the minimum order size.

// %% id=data
var rng = new Random(42);
string[] regions = ["West", "East", "Central"];
var orders = Enumerable.Range(1, 500)
    .Select(i => new Order(i, regions[i % 3], Math.Round(rng.NextDouble() * 1000, 2)))
    .ToList();

// %% id=threshold
var threshold = Ui.Slider("Minimum order", min: 0, max: 1000, value: 400);

// %% id=summary
var big = orders.Where(o => o.Total >= threshold.Value).ToList();
big.GroupBy(o => o.Region)
   .Select(g => new { Region = g.Key, Count = g.Count(), Revenue = Math.Round(g.Sum(o => o.Total), 2) })
   .Dump("Orders above threshold");

// %% types
record Order(int Id, string Region, double Total);
