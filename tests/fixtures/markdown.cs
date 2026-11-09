#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% [markdown] id=title
// # A notebook with prose
//
// Markdown cells are comment lines. Blank lines inside them are `//` lines.

// %% id=value
var value = 10;

// %% [markdown] id=middle
// ## Then some more text
// - with a list
// - and **bold**

// %% id=print
Console.WriteLine(value);

// %% [markdown] id=end
// The end.
