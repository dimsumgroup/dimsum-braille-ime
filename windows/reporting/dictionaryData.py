"""Read descriptions and symbols from the Dimsum data."""

from functools import lru_cache
from pathlib import Path
import re
import unicodedata


LOCALES = ("zh", "zh_CN", "zh_HK", "zh_TW")
ESCAPES = {"0": "\0", "t": "\t", "n": "\n", "r": "\r", "f": "\f", "v": "\v"}


@lru_cache(maxsize=1)
def emoji_codepoints():
    path = Path(__file__).resolve().parent / "unicode" / "emoji-data.txt"
    points = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split("#", 1)[0].split(";")
        if len(fields) != 2 or fields[1].strip() != "Emoji":
            continue
        start, _, end = fields[0].strip().partition("..")
        points.update(range(int(start, 16), int(end or start, 16) + 1))
    return points


def is_emoji(symbol):
    return "\u20e3" in symbol or (
        bool(symbol) and not symbol[0].isascii() and ord(symbol[0]) in emoji_codepoints()
    )


def is_described_character(value):
    return len(value) == 1 and not is_emoji(value) and unicodedata.category(value)[0] in "LMN"


def read_symbol_rows(path):
    in_symbols = False
    for raw in path.read_text(encoding="utf-8-sig").split("\n"):
        line = raw.rstrip("\r")
        if line == "symbols:":
            in_symbols = True
        elif line == "complexSymbols:":
            in_symbols = False
        elif in_symbols and not line.startswith("#"):
            identifier, separator, fields = line.partition("\t")
            if not identifier or not separator:
                continue
            decoded = identifier
            if identifier.startswith("\\") and len(identifier) > 1:
                decoded = ESCAPES.get(identifier[1], identifier[1]) + identifier[2:]
            yield decoded, fields.split("\t", 1)[0]


def remove_legacy_symbol_block(contents):
    return re.sub(
        rb"(?ms)^# Dimsum Group: managed symbols begin\r?\n.*?"
        rb"^# Dimsum Group: managed symbols end(?:\r?\n|$)",
        b"",
        contents,
    )
