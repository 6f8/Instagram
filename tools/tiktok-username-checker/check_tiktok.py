#!/usr/bin/env python3
"""TikTok username availability checker.

Checks candidate usernames against the public TikTok profile page and reports
which ones appear to be free. Uses only the Python standard library.

Examples:
    python check_tiktok.py yousef yousef.dev yousef_01
    python check_tiktok.py -f names.txt
    python check_tiktok.py --generate 4 --count 30 --chars abc123 --prefix yo
"""

import argparse
import json
import random
import re
import string
import sys
import time
import urllib.error
import urllib.request

PROFILE_URL = "https://www.tiktok.com/@{}?lang=en"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0 Safari/537.36"
)
# TikTok rules: 2-24 chars, letters/digits/underscore/period, no trailing period.
VALID_RE = re.compile(r"^[A-Za-z0-9_.]{2,24}$")
DATA_RE = re.compile(
    r'<script[^>]+id="__UNIVERSAL_DATA_FOR_REHYDRATION__"[^>]*>(.*?)</script>', re.S
)

AVAILABLE, TAKEN, INVALID, UNKNOWN = "AVAILABLE", "TAKEN", "INVALID", "UNKNOWN"
COLORS = {AVAILABLE: "\033[92m", TAKEN: "\033[91m", INVALID: "\033[93m", UNKNOWN: "\033[96m"}
RESET = "\033[0m"


def is_valid(name):
    return bool(VALID_RE.match(name)) and not name.endswith(".")


def fetch(name, timeout):
    req = urllib.request.Request(
        PROFILE_URL.format(name),
        headers={"User-Agent": USER_AGENT, "Accept-Language": "en-US,en;q=0.9"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def check(name, timeout=15, retries=3):
    """Return (status, detail) for one username."""
    if not is_valid(name):
        return INVALID, "does not match TikTok username rules"

    for attempt in range(retries):
        try:
            code, html = fetch(name, timeout)
        except Exception as e:  # network error
            detail = str(e)
        else:
            if code == 404:
                return AVAILABLE, "profile not found (404)"
            if code == 429:
                detail = "rate limited (429)"
            else:
                match = DATA_RE.search(html)
                if match:
                    try:
                        scope = json.loads(match.group(1))["__DEFAULT_SCOPE__"]
                        user = scope.get("webapp.user-detail", {})
                        status = user.get("statusCode")
                        if status == 0 or user.get("userInfo", {}).get("user"):
                            return TAKEN, "account exists"
                        if status == 10222:
                            return TAKEN, "private account"
                        if status == 10221:
                            return AVAILABLE, "no account with this name"
                        detail = f"statusCode={status}"
                    except (ValueError, KeyError) as e:
                        detail = f"could not parse page: {e}"
                elif "Couldn't find this account" in html:
                    return AVAILABLE, "no account with this name"
                else:
                    detail = f"HTTP {code}, unexpected page (captcha/block?)"
        time.sleep(5 * (attempt + 1))  # back off before retrying
    return UNKNOWN, detail


def generate(length, count, chars, prefix, suffix):
    pool = chars or (string.ascii_lowercase + string.digits)
    seen = set()
    while len(seen) < count:
        body = "".join(random.choice(pool) for _ in range(length))
        name = f"{prefix}{body}{suffix}"
        if is_valid(name):
            seen.add(name)
    return sorted(seen)


def main():
    p = argparse.ArgumentParser(description="Check which TikTok usernames are free.")
    p.add_argument("names", nargs="*", help="usernames to check (with or without @)")
    p.add_argument("-f", "--file", help="text file with one username per line")
    p.add_argument("--generate", type=int, metavar="LEN",
                   help="generate random candidates with LEN random characters")
    p.add_argument("--count", type=int, default=20, help="how many to generate (default 20)")
    p.add_argument("--chars", default="", help="characters to generate from (default a-z0-9)")
    p.add_argument("--prefix", default="", help="fixed text before generated part")
    p.add_argument("--suffix", default="", help="fixed text after generated part")
    p.add_argument("-d", "--delay", type=float, default=2.0,
                   help="seconds between requests (default 2; lower risks being blocked)")
    p.add_argument("-o", "--output", default="available.txt",
                   help="file to save available usernames (default available.txt)")
    args = p.parse_args()

    names = [n.strip().lstrip("@") for n in args.names]
    if args.file:
        with open(args.file, encoding="utf-8") as fh:
            names += [ln.strip().lstrip("@") for ln in fh if ln.strip()]
    if args.generate:
        names += generate(args.generate, args.count, args.chars, args.prefix, args.suffix)
    names = list(dict.fromkeys(n.lower() for n in names if n))  # dedupe, keep order
    if not names:
        p.error("give usernames, a --file, or --generate")

    available = []
    color = sys.stdout.isatty()
    for i, name in enumerate(names, 1):
        status, detail = check(name)
        tag = f"{COLORS[status]}{status:<9}{RESET}" if color else f"{status:<9}"
        print(f"[{i}/{len(names)}] {tag} @{name}  ({detail})", flush=True)
        if status == AVAILABLE:
            available.append(name)
            with open(args.output, "a", encoding="utf-8") as fh:
                fh.write(name + "\n")
        if i < len(names) and status != INVALID:
            time.sleep(args.delay)

    print(f"\nAvailable ({len(available)}): {', '.join('@' + n for n in available) or '-'}")
    if available:
        print(f"Saved to {args.output}. Confirm in the TikTok app before relying on it:")
        print("some names are banned/reserved and still can't be claimed.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nStopped.")
