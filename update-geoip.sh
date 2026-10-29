#!/bin/sh
# Downloads the latest free DB-IP country database for attackmap.
set -eu
DEST=/opt/attackmap/dbip-country-lite.mmdb
TMP=$(mktemp /opt/attackmap/.geoip.XXXXXX)
trap 'rm -f "$TMP" "$TMP.mmdb"' EXIT

# Try this month's file first; early in the month it may not be out yet, so fall back to last month's.
for MONTH in $(date +%Y-%m) $(date -d "$(date +%Y-%m-01) -1 month" +%Y-%m); do
  if curl -fsSL -o "$TMP" "https://download.db-ip.com/free/dbip-country-lite-$MONTH.mmdb.gz"; then
    gunzip -c "$TMP" > "$TMP.mmdb"
    # A real database always contains this marker; don't replace the old one with a broken download.
    if grep -qa 'MaxMind.com' "$TMP.mmdb"; then
      chmod 644 "$TMP.mmdb"
      mv "$TMP.mmdb" "$DEST"
      echo "$(date '+%F %T') GeoIP database updated to $MONTH"
      exit 0
    fi
  fi
done
echo "$(date '+%F %T') ERROR: could not download the GeoIP database; kept the old one" >&2
exit 1
