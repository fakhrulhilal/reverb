#!/usr/bin/env bash
# Installs the packed tool from a folder of .nupkg files, the way a user would, and checks that
# the commands work and that a new notebook runs with plain `dotnet run` against the packed
# Reverb.Runtime.
#
#   eng/smoke-test.sh <folder with the .nupkg files> <version>
set -euo pipefail

feed="$(cd "$1" && pwd)"
version="$2"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# A clean package cache, so nothing left over from earlier builds can stand in for the packages.
export NUGET_PACKAGES="$work/packages"
export REVERB_KERNEL_CACHE="$work/kernels"
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true

cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
# Keeps the repo's build settings out of the notebooks below.
echo "<Project />" > "$work/Directory.Build.props"
echo "<Project />" > "$work/Directory.Build.targets"

step() { echo; echo "== $*"; }
fail() { echo "FAIL: $*"; exit 1; }

step "install Reverb.Notebooks $version"
dotnet tool install Reverb.Notebooks --version "$version" --tool-path "$work/tools" --configfile "$work/nuget.config"
reverb="$work/tools/reverb"
cd "$work"

step "reverb --version"
"$reverb" --version | tee version.txt
grep -qF "$version" version.txt || fail "expected version $version"

step "reverb new"
"$reverb" new hello.cs --no-edit
grep -qF "#:package Reverb.Runtime@$version" hello.cs || fail "hello.cs should reference Reverb.Runtime@$version"

step "reverb check"
"$reverb" check hello.cs

step "reverb exec"
"$reverb" exec hello.cs

step "dotnet run (uses the packed Reverb.Runtime)"
dotnet run hello.cs

step "reverb exec with a NuGet package"
cat > packages.cs <<EOF
#:package Reverb.Runtime@$version
#:package Humanizer@2.14.1
// reverb-notebook v1

// %% setup
using Reverb;
using Humanizer;

// %% id=words
Console.WriteLine(3285.ToWords());
EOF
"$reverb" exec packages.cs | tee packages.txt
grep -qF "three thousand two hundred and eighty-five" packages.txt || fail "the Humanizer cell didn't print what it should"

step "reverb export --html"
"$reverb" export hello.cs --html
grep -qF 'id="reverb-notebook"' hello.html || fail "hello.html has no notebook data"

echo
echo "OK: Reverb.Notebooks and Reverb.Runtime $version work when installed from packages"
