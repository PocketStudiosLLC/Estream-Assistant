#!/bin/bash
# Run the Windows build on Linux with Wine, for quick visual checks (dev PC only).
# Usage: tools/run-in-wine.sh [demo args]
#   e.g. tools/run-in-wine.sh --search "lays cucumber" --pick 2 --scan 060410079591 --tab 3
# Needs: wine (pacman -S wine) and the .NET SDK.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-estream}" WINEDEBUG=-all

dotnet publish "$root/src/EstreamAssistant.App" -c Release -r win-x64 -o "$root/publish/win-x64" | grep -E "error|warning" || true

if [ ! -d "$WINEPREFIX" ]; then wineboot -i >/dev/null 2>&1; fi
# WPF's GPU renderer draws a black window under Wine; use software rendering.
wine reg add 'HKCU\Software\Microsoft\Avalon.Graphics' /v DisableHWAcceleration /t REG_DWORD /d 1 /f >/dev/null 2>&1

cd "$root/publish/win-x64"
exec wine EstreamAssistant.exe "$@" 2> >(grep -v -E "MESA|pci id" >&2)
