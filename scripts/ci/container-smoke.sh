#!/usr/bin/env bash
set -euo pipefail

image="${1:?Usage: container-smoke.sh IMAGE PLATFORM}"
platform="${2:?Usage: container-smoke.sh IMAGE PLATFORM}"
container=""
work=$(mktemp -d)
cleanup() {
    result=$?
    if [[ -n "$container" ]]; then
        if [[ "$result" != 0 ]]; then docker logs "$container" >&2 || true; fi
        docker rm -f "$container" >/dev/null || true
    fi
    rm -rf "$work"
}
trap cleanup EXIT

# An explicit ephemeral token keeps generated setup credentials out of CI logs.
token=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
container=$(docker run -d --platform "$platform" -p 127.0.0.1::8080 \
    -e "Setup__Token=$token" "$image")
user=$(docker inspect --format '{{.Config.User}}' "$container")
[[ -n "$user" && "$user" != root && "$user" != 0 ]]
port=$(docker port "$container" 8080/tcp)
base="http://$port"
ready=false
for attempt in {1..90}; do
    if curl --silent --fail "$base/setup" -o "$work/setup.html"; then
        ready=true
        break
    fi
    sleep 1
done
[[ "$ready" == true ]]
python3 - "$work/setup.html" > "$work/assets" <<'PY'
from html.parser import HTMLParser
from pathlib import Path
import sys

class Scripts(HTMLParser):
    def __init__(self):
        super().__init__()
        self.sources = []

    def handle_starttag(self, tag, attrs):
        source = dict(attrs).get('src', '')
        if tag == 'script' and '_framework/blazor.web' in source:
            self.sources.append(source)

page = Path(sys.argv[1]).read_text()
assert 'Set up ISLA' in page, 'First-run setup page missing'
parser = Scripts()
parser.feed(page)
assert parser.sources, 'Blazor startup script missing from rendered page'
for source in parser.sources:
    assert not source.startswith(('http:', 'https:', '//')), source
    print(source.lstrip('/'))
PY
while IFS= read -r asset; do
    curl --silent --show-error --fail "$base/$asset" -o "$work/script.js"
    test -s "$work/script.js"
done < "$work/assets"
echo "Container startup and Blazor assets verified for $platform."
