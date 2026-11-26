#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"

case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) printf 'Unsupported Linux architecture: %s\n' "$(uname -m)" >&2; exit 1 ;;
esac

if [[ $# == 0 ]]; then
    output="dist/$rid"
    dotnet publish VISaveManager.csproj -c Release -r "$rid" --self-contained false -p:PublishAot=false -o "$output"
    printf 'Launch with ./%s/VISaveManager\n' "$output"
elif [[ $# == 1 && $1 == --release ]]; then
    output="dist/$rid-nativeaot"
    dotnet publish VISaveManager.csproj -c Release -r "$rid" -p:PublishAot=true -o "$output"
    archive="dist/VISaveManager-$rid.tar.gz"
    tar --exclude='./VISaveManager.dbg' -czf "$archive" -C "$output" .
    printf 'Built %s\n' "$archive"
else
    printf 'Usage: %s [--release]\n' "$0" >&2
    exit 2
fi
