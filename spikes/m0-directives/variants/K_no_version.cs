#:package Reverb.Runtime
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=a
var x = Ui.Slider("x", value: 3);
x.Value.Dump("x");

// %% types
record R(int A);
