#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=factor
var factor = 3;

// %% id=helpers
static int Square(int x) => x * x;
int Scale(int x) => x * factor;

// %% id=use
var squared = Square(4);
var scaled = Scale(5);
Func<int, int> squareIt = Square;
Console.WriteLine($"{squared} {scaled} {squareIt(6)}");
