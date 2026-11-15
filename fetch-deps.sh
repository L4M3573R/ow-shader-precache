#!/usr/bin/env bash
# Fetch the pinned OWLib / TACTLib sources used to read Overwatch's game files.
# TACTLib must include the decryption key file for the current Overwatch build
# (TACTLib/Core/Product/Tank/CMF/ProCMF_<build>.cs): after a game patch, bump
# TACTLIB_COMMIT to a commit that has it and rebuild.
set -euo pipefail
cd "$(dirname "$0")"
OWLIB_COMMIT=546efa19516d7df7ba138802d221c73ad886df28
TACTLIB_COMMIT=f1855500441a7072dea26d75a2b120e43bb2b0d8

fetch() { # url dir commit
    if [ -d "$2/.git" ] && [ "$(git -C "$2" rev-parse HEAD)" = "$3" ]; then return; fi
    rm -rf "$2"; mkdir -p "$2"
    git -C "$2" init -q
    git -C "$2" fetch -q --depth 1 "$1" "$3"
    git -C "$2" checkout -q FETCH_HEAD
}
fetch https://github.com/overtools/OWLib third_party/OWLib "$OWLIB_COMMIT"
# OWLib expects TACTLib inside it; use a newer TACTLib than OWLib's pin.
fetch https://github.com/overtools/TACTLib third_party/OWLib/TACTLib "$TACTLIB_COMMIT"
echo "dependencies ready"
