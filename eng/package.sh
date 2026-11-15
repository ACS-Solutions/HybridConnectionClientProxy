#!/usr/bin/env bash
# Publishes and packages the release assets into <output-dir>:
#   HybridConnectionClientProxy-<version>-linux-x64.tar.gz  framework-dependent single file (needs the .NET 11 runtime)
#   HybridConnectionClientProxy-<version>-win-x64.zip       self-contained single file
#   SHA256SUMS                                              sha256sum format, covering both archives
#
# Used by .github/workflows/release.yml; run it locally the same way:
#   eng/package.sh 1.2.3 dist
set -euo pipefail

version="${1:?usage: $0 <version> [output-dir]}"
out="${2:-dist}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
	echo "Version '$version' is not of the form 1.2.3 or 1.2.3-suffix" >&2
	exit 1
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/HybridConnectionClientProxy/HybridConnectionClientProxy.csproj"
name=HybridConnectionClientProxy

# File timestamps inside the archives come from the commit rather than the build time
epoch="${SOURCE_DATE_EPOCH:-$(git -C "$root" log -1 --format=%ct)}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
rm -rf "$out"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

publish() {
	local rid="$1"
	shift
	dotnet publish "$project" \
		--configuration Release \
		--runtime "$rid" \
		--output "$work/$rid" \
		-p:Version="$version" \
		-p:PublishSingleFile=true \
		-p:DebugType=embedded \
		-p:ContinuousIntegrationBuild=true \
		"$@"
	cp "$root/README.md" "$root/LICENSE.txt" "$work/$rid/"
}

# Fail if a publish produces anything other than the expected files (e.g. stray native libraries)
expect_files() {
	local dir="$1"
	shift
	local actual expected
	actual="$(cd "$dir" && find . -type f -printf '%P\n' | LC_ALL=C sort)"
	expected="$(printf '%s\n' "$@" | LC_ALL=C sort)"
	if [[ "$actual" != "$expected" ]]; then
		echo "Unexpected publish output in $dir:" >&2
		diff <(echo "$expected") <(echo "$actual") >&2 || true
		exit 1
	fi
}

# Use the property rather than the --self-contained switch: some SDKs read "--self-contained false" as true
publish linux-x64 -p:SelfContained=false
expect_files "$work/linux-x64" "$name" appsettings.json README.md LICENSE.txt
# A framework-dependent bundle is ~5 MB; a self-contained one is ~75 MB
if (( $(stat -c %s "$work/linux-x64/$name") > 25000000 )); then
	echo "linux-x64 binary is unexpectedly large; is it self-contained?" >&2
	exit 1
fi

publish win-x64 -p:SelfContained=true \
	-p:IncludeNativeLibrariesForSelfExtract=true \
	-p:EnableCompressionInSingleFile=true
expect_files "$work/win-x64" "$name.exe" appsettings.json appsettings.Windows.json README.md LICENSE.txt

linux_asset="$name-$version-linux-x64.tar.gz"
win_asset="$name-$version-win-x64.zip"

(
	cd "$work/linux-x64"
	chmod 0755 "$name"
	find . -type f -printf '%P\n' | LC_ALL=C sort \
		| tar --create --file=- --no-recursion \
			--mtime="@$epoch" --owner=0 --group=0 --numeric-owner \
			--files-from=- \
		| gzip -9 --no-name > "$out/$linux_asset"
)

python3 - "$work/win-x64" "$out/$win_asset" "$epoch" <<'PY'
import os, sys, time, zipfile

src, dest, epoch = sys.argv[1], sys.argv[2], int(sys.argv[3])
stamp = max(time.gmtime(epoch)[:6], (1980, 1, 1, 0, 0, 0))
with zipfile.ZipFile(dest, "w") as archive:
    for name in sorted(os.listdir(src)):
        info = zipfile.ZipInfo(name, stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with open(os.path.join(src, name), "rb") as f:
            archive.writestr(info, f.read(), compresslevel=9)
PY

(cd "$out" && sha256sum -- "$linux_asset" "$win_asset" > SHA256SUMS)

echo "Packaged $version:"
(cd "$out" && ls -l && cat SHA256SUMS)
