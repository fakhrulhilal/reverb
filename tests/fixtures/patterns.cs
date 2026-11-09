#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=input
object value = 42;
var text = "17";

// %% id=match
var isInt = value is int number;
var parsed = int.TryParse(text, out var fromText);
if (value is not int guarded) throw new InvalidOperationException("not an int");

// %% id=use
var description = value switch
{
    int n when n > 40 => "big",
    int => "small",
    _ => "other",
};
Console.WriteLine($"{isInt} {parsed} {fromText} {guarded + 1} {description}");
