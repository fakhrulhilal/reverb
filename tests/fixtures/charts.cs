#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% [markdown] id=title
// # Charts

// %% id=data
var days = Enumerable.Range(1, 5).Select(i => new Day(new DateTime(2026, 3, i), i * 3.5, i % 2 == 0 ? "Even" : "Odd")).ToList();

// %% id=line
Chart.Line(days, x: d => d.Date, y: d => d.Total, title: "Daily total");

// %% id=bar
Chart.Bar(days, x: d => d.Date.Day, y: d => d.Total, series: d => d.Parity);

// %% id=scatter
var chart = Chart.Scatter(days, x: d => d.Total, y: d => d.Total * d.Total, yTitle: "Squared");
Console.WriteLine(chart.Output.Text.Length > 0);

// %% types
record Day(DateTime Date, double Total, string Parity);
