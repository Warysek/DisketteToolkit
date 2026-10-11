#!/bin/sh
# Builds the release ZIP from this repository:  sh tools/package.sh  ->  dist/DisketteToolkit-<version>.zip
# Expects Release builds of src/Gui and src/Cli (dotnet build -c Release) and docs/drives-*.png (tools/capabilities.py).
set -e
cd "$(dirname "$0")/.."
V=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
V=${V%.0}
D=dist/pkg/DisketteToolkit-$V
rm -rf dist/pkg && mkdir -p "$D/licence" "$D/lang"
cp src/Gui/bin/Release/net48/DisketteToolkit.exe src/Gui/bin/Release/net48/DisketteToolkit.exe.config \
   src/Cli/bin/Release/net48/dtk.exe src/Cli/bin/Release/net48/dtk.exe.config README.txt NAVOD.txt "$D/"
cp docs/drives-en.png "$D/Drives-EN.png"
cp docs/drives-cs.png "$D/Mechaniky-CZ.png"
cp LICENSE "$D/licence/LICENSE-GPL-3.0.txt"
cp third_party/freedos/COPYING "$D/licence/FreeDOS-GPL.txt"
cp third_party/freedos/README.txt "$D/licence/FreeDOS-boot-sector.txt"
cat > "$D/lang/README.txt" <<'TXT'
Sem patří další jazyky (xx.lang). Šablonu vytvoří Diskette Toolkit → Nastavení → „Vytvořit šablonu nového jazyka…“.
Additional languages (xx.lang) go here. Create a template via Diskette Toolkit → Settings → "Create a template for a new language…".
TXT
rm -f "dist/DisketteToolkit-$V.zip"
if command -v zip >/dev/null; then
  (cd dist/pkg && zip -qr "../DisketteToolkit-$V.zip" "DisketteToolkit-$V")
else  # Windows (GitHub Actions): 7-Zip is preinstalled
  (cd dist/pkg && 7z a -tzip -bso0 "../DisketteToolkit-$V.zip" "DisketteToolkit-$V")
fi
rm -rf dist/pkg
echo "dist/DisketteToolkit-$V.zip"
