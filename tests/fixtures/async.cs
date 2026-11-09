#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=fetch
static async Task<int> SlowSquare(int x)
{
    await Task.Delay(1);
    return x * x;
}
var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(SlowSquare));

// %% id=sum
var total = results.Sum();
await Task.Yield();
Console.WriteLine(total);
