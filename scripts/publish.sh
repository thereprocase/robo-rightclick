#!/usr/bin/env bash
# Build the release package: a self-contained single-file win-x64 exe, zipped with its
# licences and a SHA256. Runs on Linux; the result is cross-compiled and unverified on
# Windows until docs/testlog.md says otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."

project=src/RoboRightClick/RoboRightClick.csproj
publish_dir=artifacts/publish
font_license=Fonts/LICENSE-IBM-Plex-OFL.txt

# Directory.Build.props is the only place the version is written. Any semantic version the
# app itself orders (AppVersion.TryParse) may be packaged: 1.0.0-beta.2, 1.0.0-rc.1, 1.0.0.
# Build metadata ("+...") is not allowed in a package name. A Core test reads the next line
# and checks it against AppVersion.TryParse; keep it on one line.
version_pattern='^[0-9]+\.[0-9]+\.[0-9]+(-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?$'
version="$(dotnet msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
if ! [[ "$version" =~ $version_pattern ]]; then
  echo "publish: version '$version' is not a semantic version such as 1.0.0-beta.1 or 1.0.0" >&2
  exit 1
fi

# The single-file exe bundles the runtime pack of the SDK that builds it, so the SDK is pinned
# exactly (global.json, rollForward disable); both versions are printed with the hashes.
sdk_version="$(dotnet --version)"
runtime_version="$(dotnet msbuild "$project" -getProperty:BundledNETCoreAppPackageVersion | tr -d '[:space:]')"
command -v python3 >/dev/null 2>&1 || { echo "publish: python3 is needed to write the zip" >&2; exit 1; }

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
rm -rf "$staging" "$zip_path" "$zip_path.sha256" "artifacts/${name}.exe.sha256"
mkdir -p "$staging/Fonts"
cp "$publish_dir/RoboRightClick.exe" "$staging/RoboRightClick.exe"
cp "$publish_dir/$font_license" "$staging/$font_license"
cp LICENSE README.md "$staging/"

# The exe is deterministic (ContinuousIntegrationBuild), but a zip records each file's
# modified time. The entries take the commit's time instead, so rebuilding the same commit
# gives the same zip. SOURCE_DATE_EPOCH (the reproducible-builds convention) overrides it.
stamp="${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct)}"

# One zip writer for every builder, with every entry's metadata fixed: the builder's umask,
# owner, clock and zip tool used to end up in the entry headers. Entries are written in
# sorted order, files as 0644 regular files, times from the stamp above (UTC). Entry names
# are relative to the staging folder so the zip has no leading directories.
# The compressed bytes still depend on the zlib build (zlib and zlib-ng deflate differently),
# so the exe's own SHA256 is published too: it is the reproducibility check that holds on
# any builder with the pinned SDK.
entries=(RoboRightClick.exe LICENSE README.md "$font_license")
(cd "$staging" && python3 - "../${name}.zip" "$stamp" "${entries[@]}" <<'PY'
import sys
import time
import zipfile

zip_path, stamp, entries = sys.argv[1], int(sys.argv[2]), sorted(sys.argv[3:])
date_time = time.gmtime(stamp)[:6]
with zipfile.ZipFile(zip_path, "w") as archive:
    for entry in entries:
        info = zipfile.ZipInfo(entry, date_time=date_time)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.create_system = 3
        info.external_attr = (0o100644 & 0xFFFF) << 16
        with open(entry, "rb") as source:
            archive.writestr(info, source.read(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
PY
)
rm -rf "$staging"

# sha256sum records the name it is given, so run it from the zip's folder.
(cd artifacts && sha256sum "${name}.zip" > "${name}.zip.sha256")
exe_hash="$(sha256sum "$publish_dir/RoboRightClick.exe" | cut -d' ' -f1)"
printf '%s  RoboRightClick.exe\n' "$exe_hash" > "artifacts/${name}.exe.sha256"

echo "sdk:     $sdk_version (runtime pack $runtime_version)"
echo "zip:     $zip_path"
echo "sha256:  $zip_path.sha256"
cat "$zip_path.sha256"
echo "exe:     artifacts/${name}.exe.sha256"
cat "artifacts/${name}.exe.sha256"
