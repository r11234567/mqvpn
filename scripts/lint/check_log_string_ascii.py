#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# scripts/lint/check_log_string_ascii.py
#
# Reject non-ASCII characters inside C string literals.
#
# Why this is a lint and not a style preference: a string literal in this tree
# is UTF-8, because the sources are, and it is written to the console as those
# raw bytes. On Windows the console is whatever the system ANSI codepage says,
# CP936 for a Simplified Chinese install, so a U+2192 in a log message arrives
# as its three UTF-8 bytes E2 86 92, of which CP936 decodes E2 86 as a hanzi
# and leaves 92 dangling:
#
#     TUN mqvpn0 addr: 10.203.0.10 <mojibake> 10.203.0.1 /32
#
# The interface name on the neighbouring line renders correctly, because that
# one came from a Win32 API already encoded in the console's codepage. So a
# single log line can carry two encodings at once, and only the half that came
# from our own source is corrupted.
#
# SetConsoleOutputCP(CP_UTF8) would fix the console and nothing else -- logs
# are also redirected to files, captured by a GUI, and read by other tools,
# each of which decides an encoding for itself. Keeping printed strings inside
# ASCII is the only form that is correct for every consumer.
#
# Comments are deliberately exempt: they never reach a console, and the
# box-drawing and arrow characters in them carry real structure.
#
# Usage:
#   python3 scripts/lint/check_log_string_ascii.py [paths...]
#
# Exits 1 and lists every offending line if any string literal holds a
# non-ASCII character. Default paths are the shipped C sources.
import io
import os
import re
import sys

DEFAULT_ROOTS = ["src", "include", "tests", "tools", "benchmarks", "fuzz",
                 "android/sdk-native", "ios"]
EXTS = (".c", ".h", ".cpp", ".hpp", ".m")

BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
LINE_COMMENT = re.compile(r"//[^\n]*")
STRING_LITERAL = re.compile(r'"(?:[^"\\\n]|\\.)*"')

# What to write instead. Anything not listed still fails the lint; it just has
# no suggestion attached.
SUGGESTIONS = {
    "→": "->",
    "←": "<-",
    "↔": "<->",
    "—": "--",
    "–": "-",
    "…": "...",
    "×": "x",
    "≥": ">=",
    "≤": "<=",
    "“": '\\"',
    "”": '\\"',
}


def _blank(match):
    """Replace a comment with spaces, preserving offsets and line numbers."""
    return "".join("\n" if c == "\n" else " " for c in match.group(0))


def offenders(path):
    with io.open(path, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    if all(ord(c) < 128 for c in text):
        return []

    code = LINE_COMMENT.sub(_blank, BLOCK_COMMENT.sub(_blank, text))

    line_starts = [0] + [i + 1 for i, c in enumerate(text) if c == "\n"]

    def line_of(offset):
        lo, hi = 0, len(line_starts) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if line_starts[mid] <= offset:
                lo = mid
            else:
                hi = mid - 1
        return lo + 1

    lines = text.split("\n")
    found = {}
    for match in STRING_LITERAL.finditer(code):
        for offset in range(*match.span()):
            ch = text[offset]
            if ord(ch) < 128:
                continue
            found.setdefault(line_of(offset), set()).add(ch)

    return [(ln, sorted(chars), lines[ln - 1].strip())
            for ln, chars in sorted(found.items())]


def main():
    roots = sys.argv[1:] or DEFAULT_ROOTS
    bad = []
    for root in roots:
        if not os.path.exists(root):
            continue
        for dirpath, dirs, names in os.walk(root):
            dirs[:] = [d for d in dirs if d != "third_party"]
            for name in sorted(names):
                if name.endswith(EXTS):
                    path = os.path.join(dirpath, name)
                    for ln, chars, src in offenders(path):
                        bad.append((path, ln, chars, src))

    if not bad:
        return 0

    print("Non-ASCII characters in string literals (they are printed as raw")
    print("UTF-8 and are mojibake on any non-UTF-8 console):")
    print("")
    for path, ln, chars, src in bad:
        shown = " ".join("%s (U+%04X%s)"
                         % (c, ord(c),
                            " -> write %s" % SUGGESTIONS[c] if c in SUGGESTIONS else "")
                         for c in chars)
        print("  %s:%d" % (path, ln))
        print("      %s" % src[:120])
        print("      %s" % shown)
        print("")
    print("%d line(s). Comments are exempt -- only string literals matter."
          % len(bad))
    return 1


if __name__ == "__main__":
    sys.exit(main())
