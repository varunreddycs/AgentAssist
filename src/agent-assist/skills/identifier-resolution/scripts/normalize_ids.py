"""Extract and normalise MedPack / MedQuote identifiers from a message.

Usage: normalize_ids.py <message text...>

Prints a JSON array of {"id", "base", "hyphenated"} in the order typed, without duplicates.
An identifier is a token of letters and digits, optionally joined by hyphens, with at least
two digits; "base" is the part before the first hyphen.
"""

import json
import re
import sys

TOKEN = re.compile(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*")


def normalize(text: str) -> list[dict[str, str | bool]]:
    seen: set[str] = set()
    result: list[dict[str, str | bool]] = []
    for match in TOKEN.finditer(text):
        token = match.group(0).upper()
        if sum(ch.isdigit() for ch in token) < 2 or token in seen:
            continue
        seen.add(token)
        result.append({"id": token, "base": token.split("-", 1)[0], "hyphenated": "-" in token})
    return result


def main(argv: list[str]) -> int:
    print(json.dumps(normalize(" ".join(argv))))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
