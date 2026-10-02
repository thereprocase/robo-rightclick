#!/usr/bin/env bash
# Build the release package: a self-contained single-file win-x64 exe, zipped with its
# licences and a SHA256. Runs on Linux; the result is cross-compiled and unverified on
# Windows until docs/testlog.md says otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."

project=src/RoboRightClick/RoboRightClick.csproj
publish_dir=artifacts/publish
font_license=Fonts/LICENSE-IBM-Plex-OFL.txt

# Directory.Build.props is the only place the version is written.
version="$(dotnet msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+$ ]]; then
  echo "publish: version '$version' is not 1.0.0-beta.1-shaped" >&2
  exit 1
fi

# A stale file from an earlier run must never end up in the package.
rm -rf "$publish_dir"
mkdir -p artifacts

# Two deliberate departures from a plain "-r win-x64" publish, both to keep restore in
# locked mode without touching a lockfile:
#  - The runtime identifier (win-x64) and SelfContained come from the project file. Passing
#    -r on the command line also applies it to RoboRightClick.Core, whose lockfile has none.
#  - EnableSingleFileAnalyzer=false stops the SDK adding the Microsoft.NET.ILLink.Tasks
#    package, which neither lockfile lists. Single-file compatibility is exercised by the
#    publish itself; the analyzer only adds warnings.
dotnet publish "$project" -c Release --self-contained true \
  -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false \
  -p:DebugType=none -p:ContinuousIntegrationBuild=true \
  -o "$publish_dir"

# The package is exactly the exe plus the font licence the project copies beside it.
actual="$(cd "$publish_dir" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)"
expected="$(printf '%s\n%s\n' "$font_license" RoboRightClick.exe | LC_ALL=C sort)"
if [[ "$actual" != "$expected" ]]; then
  echo "publish: $publish_dir holds unexpected content. Expected:" >&2
  echo "$expected" >&2
  echo "Found:" >&2
  echo "$actual" >&2
  exit 1
fi

name="RoboRightClick-${version}-win-x64"
zip_path="artifacts/${name}.zip"
staging="artifacts/staging"
rm -rf "$staging" "$zip_path" "$zip_path.sha256"
mkdir -p "$staging/Fonts"
cp "$publish_dir/RoboRightClick.exe" "$staging/RoboRightClick.exe"
cp "$publish_dir/$font_license" "$staging/$font_license"
cp LICENSE README.md "$staging/"

# Entry names are relative to the staging folder so the zip has no leading directories.
entries=(RoboRightClick.exe LICENSE README.md "$font_license")
if command -v zip >/dev/null 2>&1; then
  (cd "$staging" && zip -X -q "../${name}.zip" "${entries[@]}")
else
  # "python3 -m zipfile -c" keeps only each file's base name, which would flatten Fonts/.
  (cd "$staging" && python3 - "../${name}.zip" "${entries[@]}" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as archive:
    for entry in sys.argv[2:]:
        archive.write(entry, entry)
PY
  )
fi
rm -rf "$staging"

# sha256sum records the name it is given, so run it from the zip's folder.
(cd artifacts && sha256sum "${name}.zip" > "${name}.zip.sha256")

echo "zip:    $zip_path"
echo "sha256: $zip_path.sha256"
cat "$zip_path.sha256"
