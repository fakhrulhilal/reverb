#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=risky
static int ParseOrThrow(string s) =>
    int.TryParse(s, out var v) ? v : throw new BadInputException(s);

// %% id=attempt
string message;
try
{
    ParseOrThrow("nope");
    message = "parsed";
}
catch (BadInputException e)
{
    message = $"bad input: {e.Input}";
}

// %% id=show
Console.WriteLine(message);

// %% types
class BadInputException(string input) : Exception($"Can't parse '{input}'")
{
    public string Input { get; } = input;
}
