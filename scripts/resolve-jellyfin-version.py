#!/usr/bin/env python3
"""Resolves the newest usable Jellyfin package version and the matching image tag.

Emits `key=value` lines for consumption by a GitHub Actions step.

NuGet's flat-container index is SemVer-ordered, but the last entry is not
necessarily the one we want: `jellyfin.controller` carries a malformed
`12.0.0-rcrc3` that sorts above `12.0.0-rc7` under prerelease comparison. So
prereleases are accepted only when their tag is a recognised channel.
"""

import argparse
import json
import re
import sys
import urllib.request

INDEX_URL = "https://api.nuget.org/v3-flatcontainer/{package}/index.json"

VERSION_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-(.+))?$")
# Anything outside this set is a typo or a one-off upload, not a channel.
PRERELEASE_RE = re.compile(r"^(alpha|beta|rc)\.?(\d+)$", re.IGNORECASE)
CHANNEL_RANK = {"alpha": 0, "beta": 1, "rc": 2}


def parse(version):
    """Returns a sort key, or None when the version is not one we would ship against."""
    match = VERSION_RE.match(version)
    if match is None:
        return None

    major, minor, patch, revision, prerelease = match.groups()
    numeric = (int(major), int(minor), int(patch), int(revision or 0))

    if prerelease is None:
        # A stable release outranks every prerelease of the same number.
        return numeric + (3, 0)

    pre = PRERELEASE_RE.match(prerelease)
    if pre is None:
        return None

    return numeric + (CHANNEL_RANK[pre.group(1).lower()], int(pre.group(2)))


def docker_tag(version):
    """12.0.0-rc7 ships as the 12.0-rc7 image; 12.0.1 ships as 12.0.1."""
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)-(.+)", version)
    if match is None:
        return version
    return f"{match.group(1)}.{match.group(2)}-{match.group(4)}"


def newest(versions):
    ranked = [(parse(v), v) for v in versions]
    ranked = [(key, v) for key, v in ranked if key is not None]
    if not ranked:
        raise SystemExit("no usable versions found")
    return max(ranked)[1]


def pinned_version(csproj):
    with open(csproj) as handle:
        content = handle.read()
    match = re.search(r"<JellyfinVersion[^>]*>([^<]+)</JellyfinVersion>", content)
    if match is None:
        raise SystemExit(f"no <JellyfinVersion> found in {csproj}")
    return match.group(1).strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--csproj", default="Jellyfin.Plugin.VFQ/Jellyfin.Plugin.VFQ.csproj")
    parser.add_argument("--package", default="jellyfin.controller")
    parser.add_argument("--requested", default="", help="skip the lookup and use this version")
    parser.add_argument("--list", action="store_true", help="print every candidate, newest first")
    args = parser.parse_args()

    pinned = pinned_version(args.csproj)

    if args.requested:
        latest = args.requested
    else:
        with urllib.request.urlopen(INDEX_URL.format(package=args.package), timeout=30) as response:
            versions = json.load(response)["versions"]
        if args.list:
            ranked = sorted(
                ((parse(v), v) for v in versions if parse(v) is not None), reverse=True
            )
            for _, version in ranked:
                print(version)
            return
        latest = newest(versions)

    print(f"pinned={pinned}")
    print(f"latest={latest}")
    print(f"docker_tag={docker_tag(latest)}")
    print(f"changed={'false' if latest == pinned else 'true'}")


if __name__ == "__main__":
    sys.exit(main())
