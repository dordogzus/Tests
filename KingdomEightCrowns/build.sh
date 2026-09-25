#!/usr/bin/env bash
# Builds both plugins and assembles the drop-in package under dist/.
#   ./build.sh                                   # compile against the committed interop stubs
#   ./build.sh -p:KingdomInteropDir=/path/to/Kingdom Two Crowns/BepInEx/interop   # against the real interop assemblies
set -euo pipefail
cd "$(dirname "$0")"
dotnet build src/KingdomEightCrowns/KingdomEightCrowns.csproj -c Release -o build/core "$@"
dotnet build src/KingdomEightCrowns.AppearanceFlow/KingdomEightCrowns.AppearanceFlow.csproj -c Release -o build/appearanceflow "$@"

version=$(sed -n 's/.*PluginVersion = "\(.*\)";/\1/p' src/KingdomEightCrowns.AppearanceFlow/KingdomEightCrowns.AppearanceFlow/Plugin.cs)
pkg="dist/KingdomEightCrowns-${version}-2.1.4-FULL"
rm -rf "$pkg" "$pkg.zip"
mkdir -p "$pkg/BepInEx/plugins/KingdomEightCrowns" "$pkg/BepInEx/config"
cp build/core/KingdomEightCrowns.dll build/appearanceflow/KingdomEightCrowns.AppearanceFlow.dll "$pkg/BepInEx/plugins/KingdomEightCrowns/"
cp package/BepInEx/config/*.cfg "$pkg/BepInEx/config/"
cp package/README-KINGDOM-EIGHT-CROWNS.txt "$pkg/"
(cd "$pkg" && zip -qr "../$(basename "$pkg").zip" .)
echo "Package: $pkg.zip"
