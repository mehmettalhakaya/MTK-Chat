#!/usr/bin/env bash
set -euo pipefail
# Only the separate chat service is changed. Site files, nginx and secrets stay untouched.
release_name=${1:?release name required}
archive=${2:?archive path required}
expected_sha=${3:?sha256 required}
[[ $release_name =~ ^groups-navigation-[0-9]{8}-[0-9]{6}$ ]] || exit 20
[[ $archive == /tmp/mtk-chat-groups-navigation.tar.gz ]] || exit 21
[[ $expected_sha =~ ^[0-9a-fA-F]{64}$ ]] || exit 22
[[ $(sha256sum "$archive" | cut -d' ' -f1) == "${expected_sha,,}" ]] || exit 23
base=/opt/mtk-chat
[[ $(realpath "$base/releases") == /opt/mtk-chat/releases ]] || exit 24
[[ -L "$base/current" ]] || exit 25
previous=$(readlink -f "$base/current")
[[ $previous == /opt/mtk-chat/releases/* && -d $previous ]] || exit 26
target="$base/releases/$release_name"
[[ ! -e $target ]] || exit 27
[[ $(realpath -m "$target") == "$target" ]] || exit 28
# Reject path traversal; the archive is produced from our clean local runtime publish.
tar -tzf "$archive" | while IFS= read -r entry; do
    [[ $entry != /* && $entry != *'../'* ]] || exit 29
done
mkdir -m 755 "$target"
tar --no-same-owner -xzf "$archive" -C "$target"
[[ -f "$target/MTKChat.Server" && -f "$target/MTKChat.Server.dll" ]] || exit 30
chmod 755 "$target/MTKChat.Server"
# A rename of a validated sibling symlink atomically switches only this release.
ln -s "$target" "$base/current-$release_name"
mv -T "$base/current-$release_name" "$base/current"
healthy=0
if systemctl restart mtk-chat; then
    for attempt in {1..20}; do
        if systemctl is-active --quiet mtk-chat && curl -fsS --max-time 2 http://127.0.0.1:5088/api/health >/dev/null; then
            healthy=1; break
        fi
        sleep 1
    done
fi
if [[ $healthy == 0 ]]; then
    ln -s "$previous" "$base/rollback-$release_name"
    mv -T "$base/rollback-$release_name" "$base/current"
    systemctl restart mtk-chat
    echo "Chat health failed; restored previous release." >&2
    exit 31
fi
printf 'Current chat release: %s\nPrevious chat release retained: %s\n' "$target" "$previous"
curl -fsS --max-time 5 http://127.0.0.1:5088/api/health
