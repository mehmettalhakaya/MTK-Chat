#!/usr/bin/env bash
set -euo pipefail
# Update only the existing separate chat service. No site files, nginx, accounts
# or secrets change; chat membership/message state is backed up before restart.
release_name=${1:?release name required}
archive=${2:?archive path required}
expected_sha=${3:?sha256 required}
[[ $release_name =~ ^group-invites-[0-9]{8}-[0-9]{6}$ ]] || exit 20
[[ $archive == /tmp/mtk-chat-group-invites.tar.gz ]] || exit 21
[[ $expected_sha =~ ^[0-9a-fA-F]{64}$ ]] || exit 22
[[ $(sha256sum "$archive" | cut -d' ' -f1) == "${expected_sha,,}" ]] || exit 23
base=/opt/mtk-chat
[[ $(realpath "$base/releases") == /opt/mtk-chat/releases ]] || exit 24
[[ $(realpath "$base/backups") == /opt/mtk-chat/backups ]] || exit 25
[[ -L "$base/current" ]] || exit 26
previous=$(readlink -f "$base/current")
[[ $previous == /opt/mtk-chat/releases/* && -d $previous ]] || exit 27
target="$base/releases/$release_name"
backup="$base/backups/$release_name-snapshot.sql"
[[ ! -e $target && ! -e $backup ]] || exit 28
[[ $(realpath -m "$target") == "$target" ]] || exit 29
tar -tzf "$archive" | while IFS= read -r entry; do
    [[ $entry != /* && $entry != *'../'* ]] || exit 30
done
# Socket authentication avoids reading/copying application credentials. Only the
# chat_* snapshot table is dumped; the site's users and other tables are untouched.
(umask 077; mysqldump --protocol=socket --single-transaction --skip-lock-tables \
    --no-tablespaces --hex-blob --skip-comments mtk_db chat_state_snapshot > "$backup")
[[ -s $backup ]] || exit 31
# Recoverable invitation URLs additionally need their private protection key ring.
# Back up only this separate chat-owned path, never site credentials/agent secrets.
key_ring=/etc/mtk-chat/invite-keys
key_backup="$base/backups/$release_name-invite-keys.tar.gz"
if [[ -d $key_ring ]]; then
    [[ ! -L $key_ring && $(realpath "$key_ring") == "$key_ring" ]] || exit 34
    [[ $(stat -c '%a' "$key_ring") == 700 && ! -e $key_backup ]] || exit 35
    (umask 077; tar -czf "$key_backup" -C /etc/mtk-chat invite-keys)
fi
mkdir -m 755 "$target"
tar --no-same-owner -xzf "$archive" -C "$target"
[[ -f "$target/MTKChat.Server" && -f "$target/MTKChat.Server.dll" ]] || exit 32
chmod 755 "$target/MTKChat.Server"
ln -s "$target" "$base/current-$release_name"
mv -T "$base/current-$release_name" "$base/current"
healthy=0
if systemctl restart mtk-chat; then
    for attempt in {1..20}; do
        if systemctl is-active --quiet mtk-chat && \
            curl -fsS --max-time 2 http://127.0.0.1:5088/api/health >/dev/null && \
            curl -fsS --max-time 2 http://127.0.0.1:5088/invite/ >/dev/null; then
            healthy=1; break
        fi
        sleep 1
    done
fi
if [[ $healthy == 0 ]]; then
    ln -s "$previous" "$base/rollback-$release_name"
    mv -T "$base/rollback-$release_name" "$base/current"
    systemctl restart mtk-chat
    # Do not overwrite potentially new messages automatically. Older single-link
    # binaries cannot preserve a multi-link snapshot; use the retained matched
    # snapshot/key backup for an operator-reviewed recovery, not blind DB restore.
    echo 'Chat health failed; previous binary restored. Snapshot/key backups retained; check state-format compatibility before further writes.' >&2
    exit 33
fi
printf 'Current chat release: %s\nPrevious release retained: %s\nChat snapshot backup: %s\n' "$target" "$previous" "$backup"
curl -fsS --max-time 5 http://127.0.0.1:5088/api/health
# PowerShell-to-SSH stdin can append a CRLF after the script's final newline.
# Stop explicitly after successful validation rather than interpreting that tail.
exit 0
