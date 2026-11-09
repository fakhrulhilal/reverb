#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=one
Console.WriteLine(1 + 1);

// %% id=two
Console.WriteLine(string.Join(",", Enumerable.Range(1, 3)));

// %% id=three
Console.WriteLine(Math.Max(3, 7));
