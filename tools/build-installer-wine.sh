#!/bin/bash
# Build the Windows installer on the Linux dev PC (Inno Setup under Wine).
# Release builds are made by GitHub Actions (.github/workflows/release.yml); this is for local checks.
# One-time: install Inno Setup 6.7.4 into the Wine prefix:
#   wine innosetup-6.7.4.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER '/DIR=C:\InnoSetup6'
# Usage: tools/build-installer-wine.sh [version]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
ver="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$root/src/EstreamAssistant.App/EstreamAssistant.App.csproj")}"
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-estream}" WINEDEBUG=-all
dotnet test "$root/tests/EstreamAssistant.Tests" | tail -1
dotnet publish "$root/src/EstreamAssistant.App" -c Release -r win-x64 -p:Version="$ver" -o "$root/publish/win-x64" | grep -E " error |warning" || true
cd "$root/installer"
wine 'C:\InnoSetup6\ISCC.exe' /Q "/DAppVersion=$ver" EstreamAssistant.iss 2> >(grep -v -E "MESA|pci id" >&2)
ls -la "$root/dist"
