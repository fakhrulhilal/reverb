#!/usr/bin/env dotnet
#:package Reverb.Runtime@0.1.0
// reverb-notebook v1

// %% setup
using Reverb;

// %% id=folder
// The notebook's own folder, whatever the current directory is.
Console.WriteLine(File.Exists(Path.Combine(Notebook.Folder, "folder.cs")));
