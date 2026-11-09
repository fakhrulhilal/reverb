#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;
using System.Text;
using static System.Math;
using Inv = System.Globalization.CultureInfo;
using Pair = (int Left, int Right);

// %% id=values
Pair pair = (3, 4);
var hyp = Sqrt(pair.Left * pair.Left + pair.Right * pair.Right);

// %% id=show
var sb = new StringBuilder().Append(hyp);
Console.WriteLine(sb.ToString());
Console.WriteLine(hyp.ToString("F1", Inv.InvariantCulture));
