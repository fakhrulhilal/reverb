#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=root
var root = 5;

// %% id=left
var left = root + 1;

// %% id=right
var right = root * 2;

// %% id=join
var joined = left + right;
joined.Dump("joined");
