#!/usr/bin/env bash
# Pack the stub runtime into a local feed and check that the sample
# notebook runs with plain `dotnet run`. Used locally and in CI.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cd "$here/.."
dotnet pack stub-runtime/Reverb.Runtime.csproj -c Release -o local-feed --nologo -v q
cd "$here"
out="$(dotnet run sales.cs 2>&1)" || { echo "$out"; exit 1; }
echo "$out" | grep -v NU1903 || true
expected="Region=East  Count=98  Revenue=68284.24000000002"
if ! echo "$out" | tr -d '\r' | grep -qF "$expected"; then
  echo "FAIL: expected line not found: $expected"
  exit 1
fi
echo "OK: sales.cs ran with dotnet run"
