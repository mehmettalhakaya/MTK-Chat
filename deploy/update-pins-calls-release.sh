#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
umask 077

# Update only the existing separate chat service. Site files, nginx, accounts,
# service configuration and application secrets are never rewritten here.
fail() { printf '%s\n' "$2" >&2; exit "$1"; }
[[ $# == 3 ]] || fail 19 'Usage: update-pins-calls-release.sh RELEASE ARCHIVE SHA256'
release_name=$1
archive=$2
expected_sha=$3
[[ $release_name =~ ^pins-voice-calls-[0-9]{8}-[0-9]{6}$ ]] || fail 20 'Unexpected release name.'
[[ $archive == /tmp/mtk-chat-pins-voice-calls.tar.gz ]] || fail 21 'Unexpected archive path.'
[[ $expected_sha =~ ^[0-9a-fA-F]{64}$ ]] || fail 22 'Invalid expected SHA-256.'
for command_name in realpath readlink sha256sum tar mysqldump stat systemctl curl; do
    command -v "$command_name" >/dev/null || fail 18 'A required release utility is unavailable.'
done
[[ -f $archive && ! -L $archive && $(realpath "$archive") == "$archive" ]] || fail 21 'Archive must be an ordinary file at the exact upload path.'
actual_sha=$(sha256sum "$archive")
[[ ${actual_sha%% *} == "${expected_sha,,}" ]] || fail 23 'Archive SHA-256 mismatch.'

base=/opt/mtk-chat
releases="$base/releases"
backups="$base/backups"
[[ -d $base && $(realpath "$base") == /opt/mtk-chat ]] || fail 24 'Unexpected chat base directory.'
[[ -d $releases && ! -L $releases && $(realpath "$releases") == /opt/mtk-chat/releases ]] || fail 24 'Unexpected release directory.'
[[ -d $backups && ! -L $backups && $(realpath "$backups") == /opt/mtk-chat/backups ]] || fail 25 'Unexpected backup directory.'
[[ -L "$base/current" ]] || fail 26 'Current release is not a symbolic link.'
previous=$(readlink -f "$base/current")
[[ -d $previous && ${previous%/*} == "$releases" && $previous != "$releases" ]] || fail 27 'Current release resolves outside the release directory.'
target="$releases/$release_name"
backup="$backups/$release_name-snapshot.sql"
key_backup="$backups/$release_name-invite-keys.tar.gz"
switch_link="$base/current-$release_name"
rollback_link="$base/rollback-$release_name"
for destination in "$target" "$backup" "$key_backup" "$switch_link" "$rollback_link"; do
    [[ ! -e $destination && ! -L $destination && $(realpath -m "$destination") == "$destination" ]] || fail 28 'A release destination already exists or resolves unexpectedly.'
done

# GNU tar's escaped listing makes control characters and backslashes visible.
# Release filenames need neither; reject them instead of interpreting an escaped
# name as a safe path. Reject every .. component, including the final component.
archive_entries=$(tar --list --gzip --quoting-style=escape --file="$archive") || fail 30 'Cannot list release archive.'
[[ -n $archive_entries ]] || fail 30 'Release archive is empty.'
while IFS= read -r entry; do
    [[ -n $entry && $entry != /* && $entry != *\\* && ! $entry =~ (^|/)\.\.(/|$) && ! $entry =~ [[:cntrl:]] ]] || fail 30 'Archive contains an unsafe path.'
done <<< "$archive_entries"
# Directories and ordinary files only: reject symbolic/hard links, devices,
# FIFOs and all other special entries before creating/extracting the target.
archive_types=$(tar --list --verbose --gzip --quoting-style=escape --file="$archive") || fail 30 'Cannot inspect release archive types.'
while IFS= read -r entry; do
    [[ ${entry:0:1} == '-' || ${entry:0:1} == d ]] || fail 30 'Archive contains a link or special file.'
done <<< "$archive_types"

# Socket authentication avoids loading site/app credentials. Only the existing
# chat snapshot table is backed up; the site's users and other tables are untouched.
mysqldump --protocol=socket --single-transaction --skip-lock-tables \
    --no-tablespaces --hex-blob --skip-comments mtk_db chat_state_snapshot > "$backup"
[[ -s $backup && $(stat -c '%a' "$backup") == 600 ]] || fail 31 'Chat snapshot backup is missing or not private.'
# Invitation URLs need their matching private protection keys for operator-led
# recovery. Preserve the existing key ring; never copy/print app or site secrets.
key_ring=/etc/mtk-chat/invite-keys
[[ -d $key_ring && ! -L $key_ring && $(realpath "$key_ring") == "$key_ring" ]] || fail 34 'Expected chat invitation key ring is unavailable.'
[[ $(stat -c '%a' "$key_ring") == 700 ]] || fail 35 'Chat invitation key ring is not private.'
tar -czf "$key_backup" -C /etc/mtk-chat invite-keys
[[ -s $key_backup && $(stat -c '%a' "$key_backup") == 600 ]] || fail 35 'Invitation key backup is missing or not private.'

# Recheck the exact uploaded bytes immediately before extraction. A fresh target
# plus the file/type validation prevents archive entries from escaping releases.
actual_sha=$(sha256sum "$archive")
[[ ${actual_sha%% *} == "${expected_sha,,}" ]] || fail 23 'Archive changed after validation.'
mkdir -m 755 "$target"
# Runtime files must remain readable by the existing unprivileged mtk-chat
# service. Use 022 only for extraction; snapshot/key backups stay under 077.
(umask 022; tar --no-same-owner --no-same-permissions -xzf "$archive" -C "$target")
[[ -f "$target/MTKChat.Server" && ! -L "$target/MTKChat.Server" && -f "$target/MTKChat.Server.dll" && ! -L "$target/MTKChat.Server.dll" ]] || fail 32 'Release server files are missing.'
chmod 755 "$target/MTKChat.Server"
ln -s "$target" "$switch_link"
mv -T "$switch_link" "$base/current"

healthy=0
if systemctl restart mtk-chat; then
    for attempt in {1..20}; do
        # A GUID-shaped route exercises authentication, not a 404 caused by
        # failed route binding. Do not send real credentials or read pin data.
        pin_status=$(curl -sS --max-time 2 --output /dev/null --write-out '%{http_code}' \
            http://127.0.0.1:5088/api/conversations/00000000-0000-0000-0000-000000000001/pins) || pin_status=000
        if systemctl is-active --quiet mtk-chat && \
            curl -fsS --max-time 2 http://127.0.0.1:5088/api/health >/dev/null && \
            curl -fsS --max-time 2 http://127.0.0.1:5088/invite/ >/dev/null && \
            [[ $pin_status == 401 ]]; then
            healthy=1
            break
        fi
        sleep 1
    done
fi
if [[ $healthy == 0 ]]; then
    # Roll back binaries ONLY. Automatically restoring a snapshot/key ring could
    # erase messages/invites created since backup. Old binaries may silently drop
    # the new Pins field on their next save; an operator must assess compatibility.
    printf '%s\n' 'WARNING: Health checks failed. Binary-only rollback follows; older snapshot writers may discard new Pins data. Database/key backups will NOT be restored automatically.' >&2
    ln -s "$previous" "$rollback_link"
    mv -T "$rollback_link" "$base/current"
    if ! systemctl restart mtk-chat; then
        printf '%s\n' 'WARNING: Previous binary was restored, but its service restart failed. Operator attention is required.' >&2
    fi
    fail 33 'Previous binary restored. Retained snapshot/key backups require an operator-reviewed recovery; no database or key-ring restore was performed.'
fi
printf 'Current chat release: %s\nPrevious release retained: %s\nChat snapshot backup: %s\nInvitation key backup: %s\n' "$target" "$previous" "$backup" "$key_backup"
curl -fsS --max-time 5 http://127.0.0.1:5088/api/health
# PowerShell-to-SSH stdin can append a CRLF after the final newline.
exit 0
