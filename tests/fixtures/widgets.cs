#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=controls
var size = Ui.Slider("Size", min: 1, max: 10, value: 3);
var region = Ui.Dropdown("Region", ["West", "East", "Central"], value: "East");
var query = Ui.Text("Search", value: "ab");
var showAll = Ui.Checkbox("Show all", value: true);
var rows = Ui.Number("Rows", value: 4, min: 1, max: 100);
var refresh = Ui.Button("Refresh");

// %% id=report
var line = $"{size.Value} {region.Value} {query.Value} {showAll.Value} {rows.Value + 1} {refresh.Value}";
Console.WriteLine(line);
