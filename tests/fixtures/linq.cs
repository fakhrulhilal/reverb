#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% [markdown] id=intro
// # LINQ basics
// Query syntax and method syntax side by side.

// %% id=numbers
int[] numbers = [5, 3, 8, 1, 9, 2, 7];

// %% id=query
var evens = from n in numbers
            where n % 2 == 0
            orderby n
            select n;

// %% id=method
var odds = numbers.Where(n => n % 2 == 1).Order().ToArray();

// %% id=groups
var groups = numbers
    .GroupBy(n => n % 3)
    .Select(g => (Remainder: g.Key, Count: g.Count()))
    .OrderBy(t => t.Remainder)
    .ToList();

// %% id=show
Console.WriteLine(string.Join(",", evens));
Console.WriteLine(string.Join(",", odds));
groups.Dump("by remainder");
