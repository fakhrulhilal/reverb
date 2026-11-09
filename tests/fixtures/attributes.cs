#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=load name="Load data"
var data = new[] { 1, 2, 3 };

// %% id=hidden hide_code
var hidden = data.Length;

// %% id=off disabled
var off = hidden * 100;

// %% id=show name="Show results" hide_code
Console.WriteLine(hidden);
