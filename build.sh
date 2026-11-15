#!/usr/bin/env bash
# Build the self-contained single-file app into dist/ow-shader-precache.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
./fetch-deps.sh
x86_64-w64-mingw32-g++ -O2 -municode -static -static-libgcc -static-libstdc++ \
    -o compiler/shadercompile.exe compiler/shadercompile.cpp -ld3d11
rm -rf dist
dotnet publish app/OwShaderPrecache.csproj -c Release -o dist/.publish
mv dist/.publish/ow-shader-precache dist/
rm -rf dist/.publish
ls -la dist
