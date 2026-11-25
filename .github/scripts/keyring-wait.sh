# Waits until org.freedesktop.secrets has an owner on the session bus (CI's keyring steps, Linux).
# gnome-keyring-daemon returns before it owns the name, so every script that starts one waits here
# before probing.
#
#   sh .github/scripts/keyring-wait.sh        inside dbus-run-session, after gnome-keyring-daemon
for _ in $(seq 1 100); do
  if dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
      org.freedesktop.DBus.NameHasOwner string:org.freedesktop.secrets 2>/dev/null | grep -q 'boolean true'; then
    exit 0
  fi
  sleep 0.1
done
echo "org.freedesktop.secrets has no owner" >&2
exit 1
