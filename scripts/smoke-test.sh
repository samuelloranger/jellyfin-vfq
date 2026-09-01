#!/usr/bin/env bash
#
# Boots a real Jellyfin server in Docker, installs the built plugin, and asserts
# that a VFQ audio track is auto-selected in the PlaybackInfo response.
#
# Catches what `dotnet build` cannot: a removed or changed Jellyfin API surfacing
# as a MissingMethodException at plugin load, and pipeline changes that stop the
# middleware from seeing the response body.
#
# Usage: scripts/smoke-test.sh <docker-tag> [plugin-dll]
#
set -euo pipefail

TAG="${1:?usage: smoke-test.sh <docker-tag> [plugin-dll]}"
DLL="${2:-Jellyfin.Plugin.VFQ/bin/Release/net10.0/Jellyfin.Plugin.VFQ.dll}"

PLUGIN_GUID="4a7f5452-e36b-4c5d-bd87-cd4cd3f5b4dd"
PLUGIN_VERSION="9.9.9.9"
CONTAINER="vfq-smoke-$$"
PORT="${SMOKE_PORT:-18096}"
WORKDIR="$(mktemp -d)"
BASE="http://localhost:${PORT}"

log()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31mFAIL: %s\033[0m\n' "$*" >&2; exit 1; }

cleanup() {
  local code=$?
  if [ $code -ne 0 ]; then
    log "Server log tail"
    server_log | tail -60 || true
  fi
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  # The container writes as root, so remove the tree from inside a container.
  docker run --rm -v "$WORKDIR:/w" alpine sh -c 'rm -rf /w/..?* /w/.[!.]* /w/*' >/dev/null 2>&1 || true
  rmdir "$WORKDIR" 2>/dev/null || true
  exit $code
}
trap cleanup EXIT

[ -f "$DLL" ] || fail "plugin dll not found at $DLL"

log "Staging plugin and test media"
mkdir -p "$WORKDIR/config/plugins/VFQ_${PLUGIN_VERSION}" "$WORKDIR/cache" "$WORKDIR/media"
cp "$DLL" "$WORKDIR/config/plugins/VFQ_${PLUGIN_VERSION}/"

cat > "$WORKDIR/config/plugins/VFQ_${PLUGIN_VERSION}/meta.json" <<META
{
  "guid": "${PLUGIN_GUID}",
  "name": "VFQ Auto Selector",
  "version": "${PLUGIN_VERSION}",
  "targetAbi": "12.0.0.0",
  "framework": "net10.0",
  "overview": "smoke test",
  "description": "smoke test",
  "category": "General",
  "owner": "samuelloranger",
  "timestamp": "2026-01-01T00:00:00Z",
  "changelog": "smoke test",
  "assemblies": ["Jellyfin.Plugin.VFQ.dll"],
  "status": 0,
  "autoUpdate": false
}
META

# Two audio tracks: index 1 English, index 2 titled VFQ. Jellyfin's own default
# picks index 1, so index 2 in the response can only come from the plugin.
ffmpeg -y -loglevel error \
  -f lavfi -i "testsrc=size=320x240:rate=10:duration=5" \
  -f lavfi -i "sine=frequency=440:duration=5" \
  -f lavfi -i "sine=frequency=880:duration=5" \
  -map 0:v -map 1:a -map 2:a -c:v libx264 -preset ultrafast -c:a aac \
  -metadata:s:a:0 language=eng -metadata:s:a:0 title="English 5.1" \
  -metadata:s:a:1 language=fre -metadata:s:a:1 title="VFQ" \
  "$WORKDIR/media/Test Movie (2020).mkv"

chmod -R 777 "$WORKDIR"

log "Starting jellyfin/jellyfin:${TAG}"
docker run -d --name "$CONTAINER" -p "${PORT}:8096" \
  -v "$WORKDIR/config:/config" -v "$WORKDIR/cache:/cache" -v "$WORKDIR/media:/media" \
  "jellyfin/jellyfin:${TAG}" >/dev/null

# Jellyfin 12 runs a setup splash server that answers HTTP before the real host
# has loaded plugins, so waiting on the port alone races the thing under test.
# Gate on the log line instead, and bail out early if the container dies.
# Never pipe `docker logs` into `grep -q`: grep exits on the first match, docker
# takes SIGPIPE, and under `set -o pipefail` the successful match reads as a
# failed pipeline. Capture the log and match in-process instead.
server_log() { docker logs "$CONTAINER" 2>&1 || true; }

log_contains() {
  case "$(server_log)" in
    *"$1"*) return 0 ;;
    *)      return 1 ;;
  esac
}

wait_for_log() {
  local pattern="$1" what="$2" attempts="${3:-120}"
  for _ in $(seq 1 "$attempts"); do
    if log_contains "$pattern"; then
      return 0
    fi
    if [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" != "true" ]; then
      fail "the container exited before $what"
    fi
    sleep 2
  done
  fail "timed out waiting for $what"
}

log "Checking the plugin loaded"
wait_for_log "Loaded plugin: VFQ Auto Selector" "the plugin to load" 45
wait_for_log "VFQ Auto Selector: started listening" "the hosted service to start" 45

# A type or method the plugin depends on that Jellyfin removed shows up here.
BINDING_ERRORS=$(server_log \
  | grep -iE "MissingMethodException|TypeLoadException|MissingFieldException" \
  | grep -ic vfq || true)
if [ "${BINDING_ERRORS:-0}" -gt 0 ]; then
  fail "the plugin threw a binding exception against this Jellyfin version"
fi

log "Waiting for the API"
wait_for_log "Kestrel is listening" "Kestrel to bind"
for _ in $(seq 1 60); do
  curl -sf "$BASE/System/Info/Public" >/dev/null 2>&1 && break
  sleep 2
done
# The setup app answers camelCase; the main host answers PascalCase.
SERVER_VERSION=$(curl -s "$BASE/System/Info/Public" | python3 -c '
import json, sys
d = json.load(sys.stdin)
print(d.get("Version") or d.get("version") or "")' 2>/dev/null || true)
[ -n "${SERVER_VERSION:-}" ] || fail "the API did not come up"
log "Server reports version ${SERVER_VERSION}"

log "Completing the startup wizard"
AUTH='Authorization: MediaBrowser Client="smoke", Device="smoke", DeviceId="smoke1", Version="1.0.0"'
api() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

[ "$(api -X POST "$BASE/Startup/Configuration" -H "$AUTH" -H 'Content-Type: application/json' \
     -d '{"UICulture":"en-US","MetadataCountryCode":"CA","PreferredMetadataLanguage":"en"}')" = 204 ] \
  || fail "Startup/Configuration rejected"
# GET first: it initializes the default user that the POST then renames.
[ "$(api "$BASE/Startup/User" -H "$AUTH")" = 200 ] || fail "Startup/User GET rejected"
[ "$(api -X POST "$BASE/Startup/User" -H "$AUTH" -H 'Content-Type: application/json' \
     -d '{"Name":"admin","Password":"smokepass123"}')" = 204 ] || fail "Startup/User POST rejected"
[ "$(api -X POST "$BASE/Startup/Complete" -H "$AUTH")" = 204 ] || fail "Startup/Complete rejected"

read -r TOKEN USER_ID <<<"$(curl -s -X POST "$BASE/Users/AuthenticateByName" -H "$AUTH" \
  -H 'Content-Type: application/json' -d '{"Username":"admin","Pw":"smokepass123"}' \
  | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d["AccessToken"],d["User"]["Id"])')"
[ -n "${TOKEN:-}" ] || fail "authentication failed"

TAUTH="Authorization: MediaBrowser Token=\"$TOKEN\", Client=\"smoke\", Device=\"smoke\", DeviceId=\"smoke1\", Version=\"1.0.0\""

log "Adding the library and waiting for the scan"
[ "$(api -X POST "$BASE/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
     -H "$TAUTH" -H 'Content-Type: application/json' \
     -d '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}],"EnableRealtimeMonitor":false,"MetadataCountryCode":"CA"}}')" = 204 ] \
  || fail "adding the library failed"

ITEM_ID=""
for _ in $(seq 1 60); do
  ITEM_ID=$(curl -s "$BASE/Items?userId=${USER_ID}&recursive=true&includeItemTypes=Movie" -H "$TAUTH" \
    | python3 -c 'import json,sys;i=json.load(sys.stdin)["Items"];print(i[0]["Id"] if i else "")')
  [ -n "$ITEM_ID" ] && break
  sleep 3
done
[ -n "$ITEM_ID" ] || fail "the test movie never appeared in the library"

default_index() {
  python3 -c '
import json,sys
d = json.load(sys.stdin)
sources = d.get("MediaSources") or d.get("mediaSources")
src = sources[0]
print(src.get("DefaultAudioStreamIndex", src.get("defaultAudioStreamIndex")))'
}

set_enabled() {
  [ "$(api -X POST "$BASE/Plugins/${PLUGIN_GUID}/Configuration" -H "$TAUTH" \
       -H 'Content-Type: application/json' \
       -d "{\"EnableAutoSelect\":$1,\"PreferHighestQuality\":true}")" = 204 ] \
    || fail "updating the plugin configuration failed"
}

assert_index() {
  local want="$1" got="$2" what="$3"
  if [ "$got" = "$want" ]; then
    printf '  ok   %-34s DefaultAudioStreamIndex = %s\n' "$what" "$got"
  else
    fail "$what: expected DefaultAudioStreamIndex $want, got $got"
  fi
}

log "Asserting VFQ selection"

assert_index 2 "$(curl -s "$BASE/Items/${ITEM_ID}/PlaybackInfo?userId=${USER_ID}" -H "$TAUTH" | default_index)" \
  "GET PlaybackInfo"

# Jellyfin compresses application/json by default, and the middleware buffers the
# response body. Regressions here are invisible without an encoding-aware check.
assert_index 2 "$(curl -s --compressed -X POST "$BASE/Items/${ITEM_ID}/PlaybackInfo?userId=${USER_ID}" \
  -H "$TAUTH" -H 'Content-Type: application/json' -H 'Accept-Encoding: gzip, br' -d '{}' | default_index)" \
  "POST PlaybackInfo, gzip"

assert_index 2 "$(curl -s -X POST "$BASE/Items/${ITEM_ID}/PlaybackInfo?userId=${USER_ID}" \
  -H "$TAUTH" -H 'Content-Type: application/json' \
  -H 'Accept: application/json; profile="CamelCase"' -d '{}' | default_index)" \
  "POST PlaybackInfo, camelCase"

# Negative control: with the plugin off, Jellyfin's own default must win. Without
# this the assertions above would pass even if Jellyfin started picking VFQ itself.
set_enabled false
assert_index 1 "$(curl -s "$BASE/Items/${ITEM_ID}/PlaybackInfo?userId=${USER_ID}" -H "$TAUTH" | default_index)" \
  "plugin disabled (baseline)"

set_enabled true
assert_index 2 "$(curl -s "$BASE/Items/${ITEM_ID}/PlaybackInfo?userId=${USER_ID}" -H "$TAUTH" | default_index)" \
  "plugin re-enabled"

log "Checking the config page is served"
[ "$(api "$BASE/web/ConfigurationPage?name=VFQ%20Auto%20Selector" -H "$TAUTH")" = 200 ] \
  || fail "the plugin configuration page is not being served"

log "PASS — plugin works on jellyfin/jellyfin:${TAG} (reported ${SERVER_VERSION})"
