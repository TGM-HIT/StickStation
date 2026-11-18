#!/usr/bin/env python3
"""Generate combined release notes with per-item platform labels.

Usage: release_notes.py TAG [--repo owner/name] [--server-url URL]
Prints Markdown to stdout. The range is <previous v* tag reachable from TAG>..TAG,
or the whole history of TAG when there is no previous tag.
"""
import argparse
import re
import subprocess
import sys

MACOS = "macOS"
WINDOWS = "Windows"
BOTH = "macOS + Windows"

SEPARATOR = "\x1e"
FIELD = "\x1f"


def classify(paths):
    """Return the platform label for the files touched by one commit."""
    macos = windows = shared = False
    for path in paths:
        if path.startswith("macos/") or path == ".github/workflows/release-macos.yaml":
            macos = True
        elif path.startswith("windows/") or path == ".github/workflows/release-windows.yaml":
            windows = True
        else:
            shared = True
    if (macos and windows) or shared or not (macos or windows):
        return BOTH
    return MACOS if macos else WINDOWS


def git(*args):
    return subprocess.run(
        ["git", *args], check=True, capture_output=True, text=True, encoding="utf-8"
    ).stdout


def previous_tag(tag):
    try:
        out = git("describe", "--tags", "--abbrev=0", "--match", "v[0-9]*", f"refs/tags/{tag}^")
    except subprocess.CalledProcessError:
        return None
    return out.strip() or None


def read_commits(rev_range):
    fmt = f"{SEPARATOR}%h{FIELD}%s"
    out = git("log", "--no-merges", "--name-only", f"--format={fmt}", rev_range)
    commits = []
    for block in out.split(SEPARATOR)[1:]:
        header, _, files = block.partition("\n")
        sha, _, subject = header.partition(FIELD)
        paths = [line for line in files.splitlines() if line.strip()]
        commits.append((sha, subject.strip(), classify(paths)))
    return commits


def tag_message(tag):
    ref = f"refs/tags/{tag}"
    if git("cat-file", "-t", ref).strip() != "tag":
        return ""
    return git("for-each-ref", "--format=%(contents)", ref).strip()


def render(commits, summary="", previous=None, tag="", compare_base=""):
    lines = []
    if summary:
        lines += [summary, ""]
    lines += ["## What's changed", ""]
    if not commits:
        lines.append("No new non-merge commits in this release.")
    for label in (MACOS, WINDOWS, BOTH):
        for sha, subject, item_label in commits:
            if item_label == label:
                lines.append(f"- **[{label}]** {subject} (`{sha}`)")
    if previous and compare_base:
        lines += ["", f"[Full changelog]({compare_base}/compare/{previous}...{tag})"]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("tag")
    parser.add_argument("--repo", default="")
    parser.add_argument("--server-url", default="https://github.com")
    args = parser.parse_args()
    if not re.fullmatch(r"v\d+\.\d+\.\d+", args.tag):
        sys.exit(f"Expected a release tag like v1.2.3, got {args.tag}")

    previous = previous_tag(args.tag)
    rev_range = f"refs/tags/{previous}..refs/tags/{args.tag}" if previous else f"refs/tags/{args.tag}"
    compare_base = f"{args.server_url}/{args.repo}" if args.repo else ""
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdout.write(
        render(read_commits(rev_range), tag_message(args.tag), previous, args.tag, compare_base)
    )


if __name__ == "__main__":
    main()
