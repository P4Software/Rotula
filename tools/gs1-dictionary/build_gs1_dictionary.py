#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Rotula GS1 Application Identifier dictionary builder.

Reads the GS1 Barcode Syntax Dictionary (gs1-syntax-dictionary.txt, Apache-2.0,
https://github.com/gs1/gs1-syntax-dictionary) and writes gs1-dictionary.json, the file that Rotula.Core embeds
(src/Rotula.Core/Gs1/Data).

HOW TO RUN
    Double-click Rebuild-Gs1Dictionary.cmd (or this file). No switches are needed.
    On the first run a commented starter file "settings.json" is created next to this script. Open it, write the path of
    the downloaded gs1-syntax-dictionary.txt into it, and run again. The script has no built-in file paths and reads no
    other input than that file and descriptions.json (Rotula's own long names).

WHAT IT DOES
    * Expands every range (for example 3100-3105) into one entry per application identifier.
    * Splits the specification into components: character set, length, optional flag and the names of the linters.
    * Turns the attributes (req=, ex=, dlpkey) into lists.
    * Takes the FNC1 rule from the "*" flag (predefined length: no FNC1 separator after the value).
    * Records the release, the licence and the copyright lines of the source, because the result is a modified copy of it.

SAFE BY DEFAULT
    The source file is only read. The only file written is the output file named in the settings.
Requires Python 3.9+, nothing else. Optional switch for scripts: --no-pause.
"""
import json
import os
import re
import sys

SCHEMA_VERSION = 1
HERE = os.path.dirname(os.path.abspath(__file__))
SETTINGS_FILE = os.path.join(HERE, "settings.json")
TEMPLATE_FILE = os.path.join(HERE, "settings.template.json")

# One component of a specification: [N3],iso3166   N..20   X1   [X..17]
COMPONENT = re.compile(r"^(\[)?([NXYZ])(\d+)?(?:\.\.(\d+))?(\])?(?:,(.*))?$")


def load_settings():
    if not os.path.exists(SETTINGS_FILE):
        with open(TEMPLATE_FILE, "r", encoding="utf-8") as src, open(SETTINGS_FILE, "w", encoding="utf-8") as dst:
            dst.write(src.read())
        print("A settings file was created: " + SETTINGS_FILE)
        print("Open it, write the path of gs1-syntax-dictionary.txt into it, save, and run this again.")
        return None
    with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


def resolve(path):
    return path if os.path.isabs(path) else os.path.normpath(os.path.join(HERE, path))


def parse_component(text, line_no):
    m = COMPONENT.match(text)
    if not m:
        raise ValueError("line %d: cannot read the component '%s'" % (line_no, text))
    optional = m.group(1) is not None
    cset, fixed, upper = m.group(2), m.group(3), m.group(4)
    if upper is not None:
        low, high = 1, int(upper)
    elif fixed is not None:
        low = high = int(fixed)
    else:
        raise ValueError("line %d: component '%s' has no length" % (line_no, text))
    linters = [x for x in (m.group(6) or "").split(",") if x]
    return {"cset": cset, "min": low, "max": high, "optional": optional, "linters": linters}


def parse_alternatives(value):
    """'01+21,02' -> [['01','21'],['02']]: alternatives are separated by commas, the AIs of one alternative by plus signs."""
    return [alt.split("+") for alt in value.split(",") if alt]


def expand(ai_text):
    if "-" not in ai_text:
        return [ai_text]
    lo, hi = ai_text.split("-")
    if len(lo) != len(hi):
        raise ValueError("range '%s' mixes lengths" % ai_text)
    return [str(n).zfill(len(lo)) for n in range(int(lo), int(hi) + 1)]


def parse_line(line, line_no):
    body, _, title = line.partition("#")
    title = title.strip()
    tokens = body.split()
    ai_text = tokens[0]
    rest = tokens[1:]
    flags = ""
    # The flags column is optional; a specification always starts with a character-set letter or a bracket.
    if rest and rest[0][0] not in "NXYZ[":
        flags = rest[0]
        rest = rest[1:]
    components, req, ex, dlpkey = [], [], [], None
    for tok in rest:
        if tok.startswith("req="):
            req.append(parse_alternatives(tok[4:]))
        elif tok.startswith("ex="):
            ex.extend(x for x in tok[3:].split(",") if x)
        elif tok == "dlpkey":
            dlpkey = []
        elif tok.startswith("dlpkey="):
            # 'dlpkey=22,10,21|235': alternative sequences of qualifiers, each an ordered list.
            dlpkey = [seq.split(",") for seq in tok[7:].split("|")]
        else:
            components.append(parse_component(tok, line_no))
    if not components:
        raise ValueError("line %d: no specification" % line_no)
    return ai_text, flags, components, req, ex, dlpkey, title


def main():
    settings = load_settings()
    if settings is None:
        return 2
    dict_path = settings.get("dictionaryPath", "")
    if not dict_path or not os.path.exists(resolve(dict_path)):
        print("The dictionary file named in settings.json was not found: '%s'." % dict_path)
        print("Download a release of https://github.com/gs1/gs1-syntax-dictionary, unpack it, and write the path of "
              "gs1-syntax-dictionary.txt into settings.json.")
        return 1
    with open(resolve(dict_path), "r", encoding="utf-8") as f:
        lines = f.read().splitlines()

    descriptions = {}
    desc_path = resolve(settings.get("descriptionsPath", "descriptions.json"))
    if os.path.exists(desc_path):
        with open(desc_path, "r", encoding="utf-8") as f:
            for key, name in json.load(f).get("names", {}).items():
                for ai in expand(key):
                    descriptions[ai] = name

    release, copyrights = None, []
    for line in lines:
        m = re.match(r"#\s*Release:\s*(\S+)", line)
        if m:
            release = m.group(1)
        m = re.match(r"#\s*(Copyright .*)", line)
        if m:
            copyrights.append(m.group(1).strip())
    if release is None or release.upper() == "UNSET":
        print("The dictionary file does not state a release (found '%s'). Use a tagged release." % release)
        return 1

    entries, lines_read = [], 0
    for i, line in enumerate(lines, start=1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        lines_read += 1
        ai_text, flags, components, req, ex, dlpkey, title = parse_line(line, i)
        for ai in expand(ai_text):
            entries.append({
                "ai": ai,
                "title": title,
                "name": descriptions.get(ai, ""),
                # '*' marks a predefined-length AI: no FNC1 separator follows its value.
                "fixedLength": "*" in flags,
                # '?' marks an AI permitted as a GS1 Digital Link data attribute.
                "digitalLinkAttribute": "?" in flags,
                "components": components,
                "requires": req,
                "excludes": ex,
                "digitalLinkKey": dlpkey,
                # AIs 31nn-36nn and 39nn carry the number of decimals in their last digit.
                "decimalPlacesInAi": len(ai) == 4 and ai[:2] in ("31", "32", "33", "34", "35", "36", "39"),
            })

    seen = set()
    for e in entries:
        if e["ai"] in seen:
            print("Duplicate AI " + e["ai"])
            return 1
        seen.add(e["ai"])
    entries.sort(key=lambda e: (len(e["ai"]), e["ai"]))

    result = {
        "schemaVersion": SCHEMA_VERSION,
        "source": {
            "name": "GS1 Barcode Syntax Dictionary",
            "url": "https://github.com/gs1/gs1-syntax-dictionary",
            "release": release,
            "license": "Apache-2.0",
            "copyright": copyrights,
            "changes": "Converted from the text format to JSON by tools/gs1-dictionary/build_gs1_dictionary.py: ranges are "
                       "expanded to one entry per AI, each specification is split into components, and a 'name' field with "
                       "Rotula's own plain-English names was added. No identifier, format or relation was altered.",
        },
        "entryLines": lines_read,
        "aiCount": len(entries),
        "entries": entries,
    }
    out_path = resolve(settings.get("outputPath", "gs1-dictionary.json"))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
        f.write("\n")
    print("Release %s: %d dictionary lines, %d application identifiers. Written to %s" % (release, lines_read, len(entries), out_path))
    return 0


if __name__ == "__main__":
    code = 1
    try:
        code = main()
    except Exception as ex:  # a plain-language message instead of a stack trace
        print("The dictionary could not be built: %s" % ex)
    pause = True
    try:
        with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
            pause = json.load(f).get("pauseAtEnd", True)
    except Exception:
        pass
    if pause and "--no-pause" not in sys.argv:
        input("Press Enter to close this window.")
    sys.exit(code)
