#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=data
Dictionary<string, int> stock = new() { ["apples"] = 3, ["pears"] = 0, ["plums"] = 7 };
HashSet<string> wanted = ["apples", "plums", "kiwis"];
int[] extra = [1, 2];
int[] more = [.. extra, 3, 4];

// %% id=available
var available = wanted.Where(w => stock.GetValueOrDefault(w) > 0).Order().ToList();

// %% id=show
Console.WriteLine(string.Join(",", available));
Console.WriteLine(more.Sum());
stock.OrderBy(kv => kv.Key).Dump();
