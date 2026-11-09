#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=name
var name = "Reverb";

// %% id=build
var json = $$"""
    { "name": "{{name}}", "length": {{name.Length}} }
    """;
var sb = new System.Text.StringBuilder();
for (var i = 0; i < 3; i++) sb.Append(name[i]);
var prefix = sb.ToString();

// %% id=show
Console.WriteLine(json);
Console.WriteLine(prefix);
