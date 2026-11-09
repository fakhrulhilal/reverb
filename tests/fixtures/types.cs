#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=shapes
List<IShape> shapes = [new Circle(1), new Rect(2, 3), new Circle(0.5)];

// %% id=areas
var areas = shapes.Select(s => new Area(s.Name, Math.Round(s.Area, 3))).ToList();
areas.Dump();

// %% id=largest
var largest = areas.MaxBy(a => a.Value)!;
Console.WriteLine($"Largest: {largest.Shape} ({Color.Red})");

// %% types
interface IShape
{
    string Name { get; }
    double Area { get; }
}

record Circle(double Radius) : IShape
{
    public string Name => "circle";
    public double Area => Math.PI * Radius * Radius;
}

record Rect(double W, double H) : IShape
{
    public string Name => "rect";
    public double Area => W * H;
}

record Area(string Shape, double Value);

enum Color { Red, Green }

struct Point
{
    public int X;
    public int Y;
}
