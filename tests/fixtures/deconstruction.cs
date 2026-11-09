#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=pair
var (first, second) = (3, 4);
(var x, var y) = (first * 2, second * 2);

// %% id=person
var person = new Person("Ada", 36);
var (name, age) = person;

// %% id=show
Console.WriteLine($"{first} {second} {x} {y} {name} {age}");

// %% types
record Person(string Name, int Age);
