#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=source
var count = 1;
var anon = new { Name = "x" };

// %% id=reassign
count = 2;

// %% id=uses-anon
Console.WriteLine(anon.Name);

// %% id=fine
Console.WriteLine("this cell is fine");
