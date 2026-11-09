#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=box
var box = new Box<int>(41);

// %% id=map
static T Twice<T>(T value, Func<T, T, T> add) => add(value, value);
var doubled = Twice(box.Value, (a, b) => a + b);
var mapped = box.Map(v => v + 1);

// %% id=show
Console.WriteLine($"{doubled} {mapped.Value}");

// %% types
record Box<T>(T Value)
{
    public Box<TOut> Map<TOut>(Func<T, TOut> f) => new(f(Value));
}
