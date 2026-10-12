#!/usr/bin/env bash
# Build the Bortle NINA plugin and zip what goes into NINA's plugin folder:
#   %LOCALAPPDATA%\NINA\Plugins\3.0.0\Bortle\
# Only our two assemblies — NINA brings everything they reference.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build src/Bortle.NinaPlugin -c Release --nologo -v quiet
out=src/Bortle.NinaPlugin/bin/Release/net8.0-windows
version=$(sed -n 's/.*AssemblyVersion("\([0-9.]*\)").*/\1/p' src/Bortle.NinaPlugin/Properties/AssemblyInfo.cs)
mkdir -p dist/Bortle
cp "$out/Bortle.NinaPlugin.dll" "$out/Bortle.Capture.Core.dll" dist/Bortle/
rm -f "dist/Bortle-$version.zip"
if command -v zip >/dev/null; then
  (cd dist && zip -qr "Bortle-$version.zip" Bortle/)
else
  (cd dist && python3 -m zipfile -c "Bortle-$version.zip" Bortle/)
fi
rm -rf dist/Bortle
echo "dist/Bortle-$version.zip"
