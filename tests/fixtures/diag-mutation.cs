#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=list
var items = new List<string> { "a" };
var lookup = new Dictionary<string, int>();

// %% id=change
items.Add("b");
lookup["x"] = 1;

// %% id=read
Console.WriteLine(items.Count + lookup.Count);
