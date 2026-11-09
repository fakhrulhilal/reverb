#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=count
var count = args.Length;

// %% id=show
Console.WriteLine($"{count} argument(s): {string.Join(" ", args)}");
