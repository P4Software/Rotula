#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Rotula stock catalogue extractor.

Reads a ZebraDesigner stock export (.xlsx) and a ZipShip PDF seed (a JSON file) and rebuilds the
Rotula label stock catalogue (stock-catalog.json). Each entry lists the sources that state it, so
one source can be dropped later (excludeSources in the settings file, or --exclude-source).

HOW TO RUN
    Double-click Rebuild-StockCatalog.cmd (or this file). No switches are needed.
    On the first run a commented starter file "extract-settings.json" is created next to this
    script. Open it and write the full path of your export and of your seed file into it; the
    script reads no other input and has no built-in file paths.
    Optional switches for scripts: --exclude-source export|pdf, --no-pause.

SOURCES
    export       the ZebraDesigner stock export: part number, family, label width and
                 height, labels across, rows, gap between labels across, corner radius.
                 Units are decoded and cross-checked (see the comment above
                 read_designer_export). Internal fields (Id, DevMode, BgPic*, Prn*, flags,
                 author, print offsets, rotation, page size and label offsets) are never read
                 into the catalogue.
    pdf          the ZipShip catalogue PDF extraction (a JSON seed): core, labels per roll.
                 Only seed entries with a public https source URL are kept.

SAFE BY DEFAULT
    Spreadsheets are opened as read-only copies, so the originals are never changed.
    Output goes to its own folder; nothing in the Rotula repository is touched.
    Review the output, then copy stock-catalog.json to src/Rotula.Core/Stocks/stocks.json
    and run the tests (StockCatalogTests validates the file).
Requires Python 3.9+ and the openpyxl package (pip install openpyxl).
"""
import collections
import csv
import datetime as dt
import json
import os
import re
import shutil
import sys
import tempfile

SCHEMA_VERSION = 1
HERE = os.path.dirname(os.path.abspath(__file__))
SETTINGS_FILE = os.path.join(HERE, "extract-settings.json")

# Commented starter settings. JSON has no comments, so "_help" keys carry the text.
DEFAULT_SETTINGS = {
    "_help": "Settings for the Rotula stock catalogue extractor. Edit and save, then run again.",
    "designerExportPath": "",
    "_designerExportPath_help": "Full path of the ZebraDesigner stock export (.xlsx). Required when the export source is used.",
    "seedPath": "",
    "_seedPath_help": "Full path of the ZipShip PDF seed (a JSON array, or a catalogue with an 'entries' list). Leave empty to build from the export alone.",
    "excludeSources": [],
    "_excludeSources_help": "Sources are export and pdf. Put a name here to leave that source out of the build.",
    "outputFolder": "output",
    "_outputFolder_help": "Folder for the results. A relative path is relative to this script.",
    "pauseAtEnd": True,
    "_pauseAtEnd_help": "True keeps the window open until you press Enter, so the result can be read.",
}

# ------------------------------------------------------------------ helpers

def say(msg):
    print(msg, flush=True)


def load_settings():
    """Load settings, creating a commented starter file on first run."""
    if not os.path.exists(SETTINGS_FILE):
        with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
            json.dump(DEFAULT_SETTINGS, f, indent=2)
        say("First run: a settings file was created at\n  " + SETTINGS_FILE +
            "\nThe defaults will be used now.")
    with open(SETTINGS_FILE, encoding="utf-8") as f:
        s = json.load(f)
    merged = dict(DEFAULT_SETTINGS)
    merged.update(s)
    return merged


def num(txt):
    """Parse '1,000' or '4.5' into a float."""
    return float(txt.replace(",", ""))


def r1(x):
    """Round to 0.1 mm, half up (Python's round() would turn 107.95 into 107.9)."""
    from decimal import Decimal, ROUND_HALF_UP
    return float(Decimal(str(round(x, 6))).quantize(Decimal("0.1"), rounding=ROUND_HALF_UP))


def trim(x, nd):
    """Round to nd decimals and drop a pointless trailing '.0'."""
    v = round(x, nd)
    return int(v) if float(v).is_integer() else v


UNIT_MM = {"in": 25.4, "mm": 1.0, "cm": 10.0, "ft": 304.8, "m": 1000.0}


def pn_str(v):
    """Part numbers arrive as ints or text; always store text without a '.0'."""
    if isinstance(v, float) and v.is_integer():
        v = int(v)
    return str(v).strip()


# ----------------------------------------------------------------- the build

def close(a, b, tol):
    return a is None or b is None or abs(a - b) <= tol


# ------------------------------------------------- ZebraDesigner stock export
#
# The export is a table of ZebraDesigner stocks. Unit decoding (verified on all 848 rows,
# see the report): the base columns LabelSizeX/Y, GapX/Y, RadiusX/Y, PaperSizeX/Y and
# LabelOffsetX/Y are in MICROMETRES whatever the UnitType is. UnitType only says which
# unit ZebraDesigner shows (1 = inch, 2 = millimetre). The U* columns repeat the same
# values in tenths of that display unit and are used here only to cross-check.
# The size in the StockName text, e.g. '(102mm x 152mm)' or '(4in x 6in)', is the third,
# independent statement of the size. Only physical facts are taken; the internal
# ZebraDesigner fields (Id, DevMode, BgPic*, Prn*, flags, author, print offsets and
# rotation, paper size and label offsets) are never read into the catalogue.

PDF_ID = "zipship-catalogue-pdf"
EXPORT_ID = "zebradesigner-stock-export"
ALL_SOURCES = (PDF_ID, EXPORT_ID)

NAME_SIZE_RE = re.compile(r"\(\s*(\d*\.?\d+)\s*(in|mm)\s*x\s*(\d*\.?\d+)\s*(in|mm)\s*\)", re.I)

_FAMILY_FIX = {"POLYPRO": "PolyPro", "POLYE": "PolyE", "ULTRASOFT": "UltraSoft", "QUICKCLIP": "QuickClip",
               "MIL": "mil", "IQ": "IQ", "UV": "UV", "NEAR-IR": "Near-IR", "IR": "IR"}


def canon_family(fam):
    """'Z-PERFORM 1000D' -> 'Z-Perform 1000D'. Words that contain a digit keep their case."""
    out = []
    for tok in str(fam).strip().split():
        if tok.upper() in _FAMILY_FIX:
            out.append(_FAMILY_FIX[tok.upper()])
        elif re.search(r"\d", tok):
            out.append(tok.upper())
        elif tok.upper() == tok or tok.lower() == tok:
            out.append("-".join(p.capitalize() if len(p) > 1 else p.upper() for p in tok.split("-")))
        else:
            out.append(tok)
    return " ".join(out)


def read_designer_export(path):
    """Read the physical columns of the ZebraDesigner stock export. Returns (rows, family_sheet)."""
    import openpyxl
    tmpdir = tempfile.mkdtemp(prefix="rotula-zd-")
    copy = os.path.join(tmpdir, "copy.xlsx")
    shutil.copyfile(path, copy)
    wb = openpyxl.load_workbook(copy, read_only=True, data_only=True)
    names = {ws.title.lower(): ws for ws in wb.worksheets}
    ws = names.get("stock") or wb.worksheets[0]
    it = ws.iter_rows(values_only=True)
    header = [str(h).strip() if h is not None else "" for h in next(it)]
    wanted = ["StockNumber", "StockName", "StockType", "LabelSizeX", "LabelSizeY", "UnitType", "Sheets",
              "Orientation", "WebX", "WebY", "GapX", "GapY", "RadiusX", "RadiusY",
              "ULabelSizeX", "ULabelSizeY", "UGapX", "URadiusX"]
    missing = [w for w in wanted if w not in header]
    if missing:
        raise RuntimeError("The ZebraDesigner export is missing these columns: " + ", ".join(missing))
    pos = {w: header.index(w) for w in wanted}
    rows = []
    for r in it:
        if r is None or r[pos["StockNumber"]] in (None, ""):
            continue
        rows.append({w: r[i] if i < len(r) else None for w, i in pos.items()})
    fam = []
    if "stocktype" in names:
        fam = [str(r[0]).strip() for r in list(names["stocktype"].iter_rows(values_only=True))[1:] if r and r[0]]
    wb.close()
    shutil.rmtree(tmpdir, ignore_errors=True)
    return rows, fam


def build_export_entries(rows):
    """One record per part number from the export rows.

    The export lists many parts twice (once with an inch name, once with a millimetre
    name); the two rows are merged. Returns (records_by_part_number, stats).
    """
    stats = collections.Counter()
    fam_counts = collections.Counter()
    groups = collections.OrderedDict()
    for r in rows:
        groups.setdefault(pn_str(r["StockNumber"]).upper(), []).append(r)
    out = {}
    for key, grp in groups.items():
        recs = []
        for r in grp:
            m = NAME_SIZE_RE.search(r["StockName"] or "")
            unit = {1: "in", 2: "mm"}.get(r["UnitType"])
            if not m or unit is None:
                stats["rowsWithUnreadableName"] += 1
                continue
            a, ua, b, ub = m.groups()
            nx = num(a) * UNIT_MM[ua.lower()]
            ny = num(b) * UNIT_MM[ub.lower()]
            cx, cy = r["LabelSizeX"] / 1000.0, r["LabelSizeY"] / 1000.0
            # cross-check 1: micrometre columns against the size in the name
            ncols = int(r["WebX"] or 1)
            # In a few multi-across stocks the name shows the whole row (labels + gaps) while the
            # column holds one label; the page-width arithmetic tells the two cases apart.
            row_name = ncols > 1 and abs(nx - cx) > 0.15 and abs(nx - (ncols * cx + (ncols - 1) * r["GapX"] / 1000.0)) <= 0.3
            if row_name:
                stats["rowsWhereNameShowsWholeRow"] += 1
            x_ok = abs(nx - cx) <= 0.15 or row_name
            y_ok = abs(ny - cy) <= 0.15
            clamped = (not y_ok) and ny > 600     # the Y column tops out at 330 mm; the name keeps the roll length
            if x_ok and (y_ok or clamped):
                stats["rowsNameMatchesMicrometreColumns"] += 1
            else:
                stats["rowsNameDisagreesWithColumns"] += 1
            # cross-check 2: the U* columns are the same values in tenths of the display unit
            f = 100.0 if r["UnitType"] == 2 else 2540.0
            if abs(r["ULabelSizeX"] * f - r["LabelSizeX"]) <= 60 and abs(r["UGapX"] * f - r["GapX"]) <= 60 \
                    and abs(r["URadiusX"] * f - r["RadiusX"]) <= 60:
                stats["rowsUColumnsConfirmUnits"] += 1
            # Inch figures: as written when the name is in inches, otherwise exact from the name's mm
            # (not from the rounded 0.1 mm value, which would give 1.252 in for 31.75 mm).
            wi = None if row_name else (num(a) if ua.lower() == "in" else trim(nx / 25.4, 4))
            hi = None if clamped else (num(b) if ub.lower() == "in" else trim(ny / 25.4, 4))
            rec = {
                "family": r["StockType"], "unit": unit, "rotated": r["Orientation"] == 2,
                "nameOk": x_ok and (y_ok or clamped), "clamped": clamped,
                "xmm": r1(cx), "ymm": r1(cy) if not clamped else None,
                "xin": wi, "yin": hi,
                "lenM": round(ny / 1000.0, 3) if clamped else None,
                "columns": int(r["WebX"] or 1), "rows": int(r["WebY"] or 1),
                "gapX": r["GapX"] / 1000.0, "radX": r["RadiusX"] / 1000.0, "radY": r["RadiusY"] / 1000.0,
                "sheets": r["Sheets"] or 0, "nameText": r["StockName"],
            }
            recs.append(rec)
        if not recs:
            continue
        first = recs[0]
        notes = []
        size_conflict = False
        for o in recs[1:]:
            same = abs((o["xmm"] or 0) - (first["xmm"] or 0)) <= 0.15 and abs((o["ymm"] or 0) - (first["ymm"] or 0)) <= 0.15 \
                and o["columns"] == first["columns"]
            if not same:
                size_conflict = True
                notes.append("the export lists this part twice with different sizes (%s x %s mm and %s x %s mm)" %
                             (f_(first["xmm"]), f_(first["ymm"]), f_(o["xmm"]), f_(o["ymm"])))
                stats["partsDuplicatedWithDifferentSizes"] += 1
        radii = sorted({round(x["radX"], 2) for x in recs})
        if len(radii) > 1:
            stats["partsWithConflictingRadiusInTwoRows"] += 1
            notes.append("corner radius differs between the export's two rows (%s mm); not recorded" % " and ".join("%g" % v for v in radii))
            first = dict(first)
            first["radX"] = first["radY"] = None
        if len(recs) > 1:
            stats["partsListedTwice"] += 1
        # prefer the mixed-case family spelling when the two rows differ only in case
        fams = sorted({x["family"] for x in recs}, key=lambda s: (s == s.upper(), s))
        first = dict(first)
        first["family"] = canon_family(fams[0])
        first["nameOk"] = all(x["nameOk"] for x in recs)
        first["sizeConflict"] = size_conflict
        first["notes"] = notes
        first["pn"] = pn_str(grp[0]["StockNumber"])
        first["rowsInExport"] = len(recs)
        out[key] = first
        fam_counts[first["family"].upper()] += 1
    stats["distinctPartNumbers"] = len(out)
    stats["rows"] = len(rows)
    return out, stats, fam_counts


def _fmt_in(v):
    return "%g" % v


def export_to_dims(x):
    """Width/height/inch values in the catalogue's convention (width = across the web).

    A rotated stock (Orientation 2, all Z-Band wristbands) is stored with its long side
    in X; the catalogue keeps the short side as the width, which is how the labels run through the printer.
    """
    w_mm, h_mm = x["xmm"], x["ymm"]
    w_in, h_in = x["xin"], x["yin"]
    if x["rotated"]:
        w_mm, h_mm = h_mm, w_mm
        w_in, h_in = h_in, w_in
    if w_in is None and w_mm is not None:
        w_in = trim(w_mm / 25.4, 4)
    if h_in is None and h_mm is not None:
        h_in = trim(h_mm / 25.4, 4)
    return w_mm, h_mm, w_in, h_in


def media_from_family(fam):
    """Zebra series names end in D (direct thermal) or T (thermal transfer), e.g. 'Z-Select 4000D'."""
    m = re.search(r"\b\d{3,4}([DT])\b", fam, re.I)
    if m:
        return "direct thermal" if m.group(1).upper() == "D" else "thermal transfer"
    if re.search(r"\bdirect\b|\bz-?band\b|receipt", fam, re.I):
        return "direct thermal"
    return None


def apply_export(cat, exp, resolutions):
    """Fold the export records into the catalogue.

    For parts that exist already the export adds the physical values the other sources
    lack (corner radius, gap between labels across, rows) and acts as a third opinion on
    size; parts only in the export become new entries. `resolutions` collects what
    happened to every disagreement, for the report.
    """
    index = {}
    for e in cat:
        if e.get("brand") == "Zebra" and e.get("partNumber"):
            index[e["partNumber"].strip().upper()] = e
    ov = collections.Counter()
    disagreements = []
    new_entries = []
    for key, x in exp.items():
        w_mm, h_mm, w_in, h_in = export_to_dims(x)
        e = index.get(key)
        if e is None:
            ov["exportOnly"] += 1
            new_entries.append(_entry_from_export(x, w_mm, h_mm, w_in, h_in))
            continue
        ov["bothInExport"] += 1
        src = set(e.get("sources") or [])
        apply_family(e, x, disagreements, ov)
        # --- compare the size
        if x["clamped"]:
            same = abs(e["widthMm"] - w_mm) <= 0.6 if w_mm is not None else True
            if e.get("rollLengthM") is not None and x["lenM"] is not None:
                same = same and abs(e["rollLengthM"] - x["lenM"]) <= max(0.1, 0.01 * x["lenM"])
        else:
            same = abs(e["widthMm"] - w_mm) <= 0.6 and (e["heightMm"] is None or abs(e["heightMm"] - h_mm) <= 0.6)
        reasons = list(e.get("_reasons") or [])
        if same:
            ov["sizeAgrees"] += 1
            if PDF_ID in src:
                ov["sizeAgreesWithPdf"] += 1
            # The export's size is checked three ways, so where the PDF figure is only a rounded copy
            # (102 mm for 101.6 mm) the export's exact figure is used.
            if not x["clamped"] and x["nameOk"] and not x["sizeConflict"] and e.get("heightMm") is not None and \
                    (abs(e["widthMm"] - w_mm) > 0.06 or abs(e["heightMm"] - h_mm) > 0.06):
                e["name"] = rename_size(e["name"], w_in, h_in, w_mm, h_mm)
                e["widthMm"], e["heightMm"], e["widthIn"], e["heightIn"] = w_mm, h_mm, w_in, h_in
                e["notes"] = ((e.get("notes") or "") + "; size refined from the ZebraDesigner export (the other source rounds it)").lstrip("; ")
                ov["sizeRefinedFromExport"] += 1
            # extra physical facts
            if e.get("cornerRadiusMm") is None and x["radX"] is not None:
                e["cornerRadiusMm"] = round(x["radX"], 2)
            if x["radX"] is not None and x["radX"] != x["radY"]:
                e["notes"] = ((e.get("notes") or "") + "; corner radius is %.1f mm across and %.1f mm along in the export" % (x["radX"], x["radY"])).lstrip("; ")
            if e.get("rows") in (None, 1):
                e["rows"] = x["rows"]
            if x["columns"] > 1 and x["gapX"] > 0 and e.get("hGapMm") is None:
                e["hGapMm"] = round(x["gapX"], 2)
            if x["columns"] != (e.get("columns") or 1):
                reasons.append("the export says %d across, the catalogue says %d" % (x["columns"], e.get("columns") or 1))
                disagreements.append({"partNumber": e["partNumber"], "what": "labels across", "other": e.get("columns"), "export": x["columns"]})
            else:
                if any(r.startswith("N across") for r in reasons):
                    reasons = [r for r in reasons if not r.startswith("N across")]
                    e["notes"] = (e.get("notes") or "") + "; the export confirms the stated size is per label (%d across)" % x["columns"]
            reasons = [r for r in reasons if not r.startswith("size stated in one unit system only")]
            if not x["nameOk"] or x["sizeConflict"]:
                reasons.append("the export's own name and size columns disagree, or it lists the part with two different sizes")
            if not reasons:
                if PDF_ID in src:
                    e["verification"] = "2+ sources agree on size"
                elif not e.get("verified"):
                    e["verification"] = "2+ sources agree on size"
                e["verified"] = True
                e.pop("_reasons", None)
            else:
                e["verified"] = False if reasons else e.get("verified")
                e["_reasons"] = reasons
        else:
            ov["sizeDisagrees"] += 1
            exp_txt = "%s x %s mm" % (f_(w_mm), f_(h_mm) if h_mm is not None else "roll")
            if PDF_ID in src and x["nameOk"] and not x["clamped"]:
                # Two sources only: the export, whose name text, micrometre columns and unit columns
                # all agree, supplies the size. The PDF row is likely shifted, so its labels-per-roll
                # figure is not trusted either.
                old = "%s x %s mm" % (f_(e["widthMm"]), f_(e["heightMm"]))
                disagreements.append({"partNumber": e["partNumber"], "what": "size (PDF vs export)", "other": old, "export": exp_txt})
                e["widthMm"], e["heightMm"], e["widthIn"], e["heightIn"] = w_mm, h_mm, w_in, h_in
                # keep the display name in step with the size now used
                e["name"] = rename_size(e["name"], w_in, h_in, w_mm, h_mm)
                old_lpr = e.get("labelsPerRoll")
                e["labelsPerRoll"] = None
                e["notes"] = ((e.get("notes") or "") + "; CONFLICT: the ZipShip PDF extraction gave %s, the ZebraDesigner export gives %s (export used)%s" %
                              (old, exp_txt, "; PDF labels per roll %s dropped as unreliable" % old_lpr if old_lpr else "")).lstrip("; ")
                e["verified"] = False
                e["verification"] = "conflict"
                e["_reasons"] = ["size differs between the ZipShip PDF extraction and the ZebraDesigner export; export value used"]
                e["sources"] = list(dict.fromkeys(list(e["sources"]) + [EXPORT_ID]))
                e["stockFamily"] = x["family"]
                continue
            disagreements.append({"partNumber": e["partNumber"], "what": "size",
                                  "other": "%s x %s mm" % (f_(e["widthMm"]), f_(e["heightMm"])), "export": exp_txt})
            reasons.append("the ZebraDesigner export gives a different size (%s)" % exp_txt)
            e["notes"] = (e.get("notes") or "") + "; ZebraDesigner export disagrees on size (%s)" % exp_txt
            e["verified"] = False
            e["verification"] = "conflict"
            e["_reasons"] = reasons
        e["sources"] = list(dict.fromkeys(list(e["sources"]) + [EXPORT_ID]))
        e["stockFamily"] = x["family"]
        if e.get("media") is None:
            m = media_from_family(x["family"])
            if m:
                e["media"] = m
                e["notes"] = (e.get("notes") or "") + "; media taken from the family name"
        if x["notes"]:
            e["notes"] = (e.get("notes") or "") + "; " + "; ".join(x["notes"])
        if e.get("notes"):
            e["notes"] = e["notes"].lstrip("; ")
    cat.extend(new_entries)
    return ov, disagreements


def series_code(text):
    """Zebra series code such as (4000, D) from "Z-Select 4000D ..."; None when absent."""
    m = re.search(r"\b(\d{3,4})([DT])X?\b", text or "", re.I)
    if not m:
        return None
    mil = re.search(r"(\d+(?:\.\d+)?)\s*mil\b", text, re.I)
    return (m.group(1), m.group(2).upper(), float(mil.group(1)) if mil else None)


def apply_family(e, x, disagreements, ov):
    """Compare the series of an existing entry with the export's StockType.

    If both name a series code and the codes differ (e.g. PDF "Z-Select 4000T Removable"
    against export "8000D NEAR-IR") the sources disagree about which product the part
    number is. The export's family is used, the entry is marked unverified and the
    disagreement is reported. The export family COATED is a quirk of three rows, not a family.
    """
    fam = x["family"]
    if fam.upper() == "COATED":
        x["family"] = ""
        return
    a, b = series_code(e.get("series") or e.get("name")), series_code(fam)
    # compare the code and, only when both name one, the thickness in mil
    if a and b and (a[:2] != b[:2] or (a[2] is not None and b[2] is not None and a[2] != b[2])):
        old = e.get("series") or e["name"]
        m = re.search(r"[\d.]+ x [\d.]+ (?:in|mm)", e["name"])
        e["name"] = fam + (" " + m.group(0) if m else "")
        e["series"] = fam
        mdl = media_from_family(fam)
        notes = "; series differs: the PDF extraction says '%s', the ZebraDesigner export says '%s' (export used)" % (old, fam)
        e["notes"] = ((e.get("notes") or "") + notes).lstrip("; ")
        if mdl and mdl != e.get("media"):
            e["media"] = mdl
        e["verified"] = False
        e["verification"] = "conflict"
        e["_reasons"] = list(e.get("_reasons") or []) + ["series differs between the PDF extraction and the export"]
        disagreements.append({"partNumber": e["partNumber"], "what": "series", "other": old, "export": fam})
        ov["seriesDisagrees"] += 1


def rename_size(name, w_in, h_in, w_mm, h_mm):
    """Replace the first "W x H in" / "W x H mm" in a display name with the size now used."""
    def sub(m):
        if m.group(1) == "mm":
            return "%s x %s mm" % (f_(w_mm), f_(h_mm))
        return "%s x %s in" % (f_(w_in), f_(h_in))
    return re.sub(r"[\d.]+ x [\d.]+ (in|mm)", sub, name, count=1)


def f_(v):
    return "-" if v is None else ("%g" % v)


def _entry_from_export(x, w_mm, h_mm, w_in, h_in):
    """New catalogue entry for a part that only the ZebraDesigner export knows."""
    fam = x["family"]
    kind = ("wristband" if re.search(r"band", fam, re.I) else
            "receipt" if (re.search(r"receipt", fam, re.I) or (re.search(r"\bmil\b", fam, re.I) and x["clamped"])) else
            "tag" if (re.search(r"\btag\b", fam, re.I) or re.search(r"\bmil\b", fam, re.I)) else "label")   # a thick (mil) stock cut to size is a tag; on a long roll it is receipt paper
    linerless = bool(re.search(r"linerless|zeroliner", fam, re.I))
    continuous = x["clamped"]
    notes = list(x["notes"])
    if continuous:
        notes.append("continuous roll (no label height); the roll length comes from the stock name")
    if linerless:
        notes.append("linerless media (no backing paper)")
    notes.append("core size and labels per roll are not in the export")
    if x["columns"] > 1:
        notes.append("%d labels across; the size is per label (the export's page width = labels + gaps)" % x["columns"])
    size_txt = ("%s in x %s m" % (f_(w_in), f_(x["lenM"] and round(x["lenM"], 1))) if continuous
                else "%s x %s %s" % (f_(w_in if x["unit"] == "in" else w_mm), f_(h_in if x["unit"] == "in" else h_mm), x["unit"]))
    return {
        "id": "zebra-" + x["pn"], "brand": "Zebra", "partNumber": x["pn"], "gtin": None,
        "name": "%s %s" % (fam, size_txt), "friendlyName": None, "series": fam,
        "widthMm": w_mm, "heightMm": None if continuous else h_mm,
        "widthIn": w_in, "heightIn": None if continuous else h_in,
        "gapMm": None, "media": media_from_family(fam), "material": None, "coating": None, "finish": None,
        "adhesive": None, "closure": None, "sensing": None, "perforation": None,
        "coreMm": None, "rollDiameterMm": None, "labelsPerRoll": None, "rollsPerCarton": None, "piecesPerBox": None,
        "rollLengthM": x["lenM"] if continuous else None,
        "columns": x["columns"], "rows": x["rows"],
        "hGapMm": round(x["gapX"], 2) if x["columns"] > 1 and x["gapX"] > 0 else None,
        "vGapMm": None, "marginsMm": None, "cornerRadiusMm": round(x["radX"], 2) if x["radX"] is not None else None, "shape": "rectangle",
        "format": "linerless" if linerless else "continuous" if continuous else "roll",
        "rfid": None, "sample": None,
        "category": "wristband" if kind == "wristband" else "tag" if kind == "tag" else
                    "zebra-linerless" if linerless else "zebra-receipt" if kind == "receipt" else "zebra-label",
        "stockFamily": fam, "productCategory": None, "productSubType": None, "productModel": None,
        "status": None, "eolDate": None, "regions": [], "sourceUrl": None,
        "sources": [EXPORT_ID],
        "verified": bool(x["nameOk"] and not x["sizeConflict"]),
        "verification": "export-name+units" if (x["nameOk"] and not x["sizeConflict"]) else "unverified",
        "notes": "; ".join(notes),
        "_reasons": ([] if (x["nameOk"] and not x["sizeConflict"]) else ["the export's own name and size columns disagree, or it lists the part with two different sizes"]),
    }


def prepare_seed(seed):
    """Name the source of every seed entry and say how its verified flag came about.

    A seed entry is kept as it is; the ZebraDesigner export then confirms or contradicts it
    in apply_export. Returns the list of entries.
    """
    out = []
    for s in seed:
        if "sources" not in s:
            u = s.get("sourceUrl") or ""
            s["sources"] = ["zipship-catalogue-pdf"] if "zipship" in u else []
        s["verification"] = ("catalogue-pdf-only" if s.get("verified") and "zipship-catalogue-pdf" in s["sources"]
                             else "seed-other" if s.get("verified") else "unverified")
        out.append(s)
    return out


def finish_flags(cat):
    """Drop the private working markers an entry may carry.

    Returns (catalogue, review); `review` is empty here because every verified flag is
    decided in apply_export. The signature is kept for the callers.
    """
    for e in cat:
        for k in ("_selfConsistent", "_swapped", "_acrossAmbiguous", "_bothUnits", "crossChecked"):
            e.pop(k, None)
    return cat, []


def validate(cat):
    """Automated checks; returns a list of problem strings."""
    problems = []
    seen = collections.Counter(e["id"] for e in cat)
    for k, v in seen.items():
        if v > 1:
            problems.append("duplicate id %s x%d" % (k, v))
    for e in cat:
        pn = e.get("partNumber")
        if pn is None:
            continue  # generic sizes (4x6 in ...) have no part number
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/\-]*", pn):
            problems.append("odd part number format: " + pn)
        for dim, dimin in (("widthMm", "widthIn"), ("heightMm", "heightIn")):
            v, vi = e.get(dim), e.get(dimin)
            if v is None:
                continue
            if not (5 <= v <= 600):
                problems.append("%s %s=%s outside 5-600 mm" % (pn, dim, v))
            if vi is not None and abs(v - vi * 25.4) > 0.6:
                problems.append("%s %s/%s disagree" % (pn, dim, dimin))
        # Roll-length sanity: labels per roll x label height should be a believable roll
        # (a few metres up to a few hundred metres).
        if e.get("labelsPerRoll") and e.get("heightMm") and e.get("format") == "roll" and not e.get("sample"):
            roll_m = e["labelsPerRoll"] * e["heightMm"] / 1000.0
            if not (3 <= roll_m <= 700):
                problems.append("%s implied roll length %.0f m looks wrong (%s labels x %s mm)" %
                                (pn, roll_m, e["labelsPerRoll"], e["heightMm"]))
        if e.get("verified") and not (e.get("sourceUrl") or e.get("sources")):
            problems.append(pn + " verified without a source")
    return problems


VERIFICATION_NOTE = (
    "verified=true means the size was parsed consistently AND cross-checked by a second, independent "
    "statement. The 'verification' field says how: '2+ sources agree on size' = the ZipShip catalogue PDF "
    "and the ZebraDesigner stock export agree; 'export-name+units' = export only, where the stock name "
    "text, the micrometre columns and the display-unit columns all agree; 'catalogue-pdf-only' = stated "
    "by the ZipShip PDF extraction alone, so not verified. Entries with verified=false need a person to "
    "check them (see stock-catalog-unparsed.csv). Each entry lists its sources so one source can be dropped later.")

OWNER_NOTE = ("Owner determined 2026-10-10 that part numbers and label dimensions are public product data; "
              "Rotula is not affiliated with the brands listed; brand and product names belong to their owners.")

SOURCE_NAMES = {
    PDF_ID: "Zebra public ZipShip catalogue PDF",
    EXPORT_ID: "ZebraDesigner stock library export (part numbers, label sizes, gaps, corner radii; owner-supplied)",
}
SOURCE_ALIASES = {"zipship": PDF_ID, "pdf": PDF_ID, PDF_ID: PDF_ID,
                  "export": EXPORT_ID, "designer": EXPORT_ID, "zebradesigner": EXPORT_ID, EXPORT_ID: EXPORT_ID}

# Optional extension fields: they are dropped when empty so entries carry no empty placeholders.
DROP_WHEN_NONE = ("gtin", "friendlyName", "productCategory", "productSubType", "productModel", "status",
                  "eolDate", "closure", "sensing", "perforation", "adhesive", "material", "coating",
                  "rollDiameterMm", "piecesPerBox", "rfid", "sample", "rollsPerCarton", "rollLengthM")


def write_outputs(cat, unparsed, review, conflicts, outdir, counts, active):
    os.makedirs(outdir, exist_ok=True)
    meta = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedOn": dt.datetime.now().strftime("%Y-%m-%d"),
        "counts": counts,
        "sources": [{"id": i, "name": SOURCE_NAMES[i]} for i in ALL_SOURCES if i in active],
        "notes": OWNER_NOTE + " Gap between labels along the roll is not stored by any source; leave it to "
                 "printer calibration unless an entry states a sensing mode. " + VERIFICATION_NOTE,
    }
    with open(os.path.join(outdir, "stock-catalog.json"), "w", encoding="utf-8") as f:
        json.dump({"metadata": meta, "entries": cat}, f, indent=1, ensure_ascii=False)
    with open(os.path.join(outdir, "stock-catalog-unparsed.csv"), "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["partNumber", "name", "reason", "status"])
        for u in unparsed:
            w.writerow([u["partNumber"], u["name"], u["reason"], "source row not read; no new entry built from it"])
        for u in review:
            w.writerow([u["partNumber"], u["name"], u["reason"], "in the catalogue, verified=false"])
    with open(os.path.join(outdir, "stock-catalog-conflicts.csv"), "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["partNumber", "what", "value in the other source", "value in the ZebraDesigner export"])
        for c in conflicts:
            w.writerow([c["partNumber"], c["what"], c["other"], c["export"]])


def build(seed_path, export_path, outdir, active):
    """Build the catalogue from the active sources. Returns a dict of results for the caller/report."""
    unparsed = []
    # ---- seed (ZipShip PDF extraction and generic/Avery sizes that cite a public page)
    seed, seed_dropped = [], collections.Counter()
    if seed_path and os.path.exists(seed_path):
        with open(seed_path, encoding="utf-8") as f:
            sd = json.load(f)
        raw_seed = sd["entries"] if isinstance(sd, dict) else sd
        for s in raw_seed:
            u = s.get("sourceUrl") or ""
            if not u.lower().startswith("https://"):
                seed_dropped["no public source URL"] += 1      # only entries with a public source are kept
                continue
            if "sources" not in s:
                s["sources"] = [PDF_ID] if "zipship" in u else []
            if PDF_ID not in active and PDF_ID in s["sources"]:
                seed_dropped["PDF source excluded"] += 1
                continue
            seed.append(s)
    cat = prepare_seed(seed)
    cat, _ = finish_flags(cat)
    conflicts = []
    # ---- ZebraDesigner export
    ov, exp_stats, fam_counts, exp_only_fam = collections.Counter(), collections.Counter(), collections.Counter(), collections.Counter()
    disagreements, resolutions, family_sheet = [], [], []
    if EXPORT_ID in active:
        exp_rows, family_sheet = read_designer_export(export_path)
        say("Read %d rows from the ZebraDesigner export." % len(exp_rows))
        exp, exp_stats, fam_counts = build_export_entries(exp_rows)
        n_before = len(cat)
        ov, disagreements = apply_export(cat, exp, resolutions)
        for e in cat[n_before:]:
            exp_only_fam[e.get("stockFamily") or ""] += 1
        for d in disagreements:
            conflicts.append({"partNumber": d["partNumber"], "what": d["what"], "other": d["other"], "export": d["export"]})
    # ---- Zebra parts that only one PDF extraction states are not called verified
    for e in cat:
        if e.get("brand") == "Zebra" and e.get("sources") == [PDF_ID] and e.get("verified"):
            e["verified"] = False
            e["verification"] = "catalogue-pdf-only"
            e["_reasons"] = ["stated only by the ZipShip PDF extraction, which has shown row-shift errors; no second source"]
    # ---- tidy: private markers, empty extension fields
    review = []
    for e in cat:
        reasons = e.pop("_reasons", None)
        if not e.get("verified"):
            review.append({"partNumber": e.get("partNumber") or e["id"], "name": e["name"],
                           "reason": "; ".join(reasons) if reasons else "not verified"})
        for k in DROP_WHEN_NONE:
            if k in e and e[k] is None:
                del e[k]
    problems = validate(cat)
    counts = {
        "exportRows": exp_stats.get("rows", 0), "exportDistinctParts": exp_stats.get("distinctPartNumbers", 0),
        "seedEntriesKept": len(seed), "seedEntriesDropped": sum(seed_dropped.values()),
        "catalogEntries": len(cat), "verified": sum(1 for e in cat if e.get("verified")),
        "inCatalogueNeedsReview": len(review), "partsInBothSources": ov.get("bothInExport", 0),
        "exportOnlyParts": ov.get("exportOnly", 0), "disagreements": len(disagreements),
    }
    write_outputs(cat, unparsed, review, conflicts, outdir, counts, active)
    ents = [e for e in cat if EXPORT_ID in (e.get("sources") or [])]
    summary = {
        "active": sorted(active), "counts": counts, "overlap": dict(ov), "exportStats": dict(exp_stats),
        "familyCountsParts": dict(fam_counts.most_common()), "exportOnlyByFamily": dict(exp_only_fam.most_common()),
        "familySheet": family_sheet, "seedDropped": dict(seed_dropped),
        "resolutions": resolutions, "disagreements": disagreements,
        "byVerification": dict(collections.Counter(e.get("verification") for e in cat)),
        "byCategory": dict(collections.Counter(e["category"] for e in cat)),
        "bySources": dict(collections.Counter("+".join(e.get("sources") or ["(none)"]) for e in cat)),
        "withCornerRadius": sum(1 for e in ents if e.get("cornerRadiusMm") is not None),
        "withHGap": sum(1 for e in ents if e.get("hGapMm") is not None),
        "acrossGt1": sum(1 for e in ents if (e.get("columns") or 1) > 1),
        "checkFindings": len(problems),
    }
    with open(os.path.join(outdir, "stock-catalog-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=1)
    with open(os.path.join(outdir, "stock-catalog-checks.txt"), "w", encoding="utf-8") as f:
        f.write("Automatic check findings (%d)\n" % len(problems))
        for p in problems:
            f.write(p + "\n")
    return {"counts": counts, "problems": problems, "summary": summary}


def parse_args(argv):
    """Optional switches for people who build the catalogue from a script; double-clicking needs none."""
    exc, pause = [], None
    it = iter(argv)
    for a in it:
        if a == "--exclude-source":
            v = next(it, "")
            if v not in SOURCE_ALIASES:
                raise RuntimeError("Unknown source '%s'. Use one of: %s." % (v, ", ".join(sorted(SOURCE_ALIASES))))
            exc.append(SOURCE_ALIASES[v])
        elif a == "--no-pause":
            pause = False
        else:
            raise RuntimeError("Unknown switch '%s'. Supported: --exclude-source, --no-pause." % a)
    return exc, pause


def main():
    pause = True
    try:
        st = load_settings()
        exc, cli_pause = parse_args(sys.argv[1:])
        pause = bool(st.get("pauseAtEnd", True)) if cli_pause is None else cli_pause
        # Both sources are on by default; the settings file or a switch can leave one out.
        active = {PDF_ID, EXPORT_ID}
        active -= {SOURCE_ALIASES[x] for x in st.get("excludeSources", []) if x in SOURCE_ALIASES}
        active -= set(exc)
        if not active:
            raise RuntimeError("Every source was excluded; nothing to build.")
        export = st.get("designerExportPath") or ""
        if EXPORT_ID in active and not (export and os.path.exists(export)):
            raise RuntimeError("The ZebraDesigner export was not found. Write the full path of the .xlsx file "
                               "into 'designerExportPath' in extract-settings.json (next to this script).")
        seed = st.get("seedPath") or ""
        out = st.get("outputFolder") or "output"
        out = out if os.path.isabs(out) else os.path.join(HERE, out)
        say("Sources    : " + ", ".join(sorted(active)))
        say("Seed       : " + (seed if seed and os.path.exists(seed) else "(none: building from the export alone)"))
        res = build(seed, export, out, active)
        say("\nDone. Results are in: " + out)
        for k, v in res["counts"].items():
            say("  %-28s %s" % (k, v))
        if res["problems"]:
            say("\nThe automatic checks found %d problem(s); the first ones:" % len(res["problems"]))
            for p in res["problems"][:10]:
                say("  - " + p)
        say("\nNext step: open stock-catalog-unparsed.csv and stock-catalog-conflicts.csv and review them by hand.")
    except ImportError:
        say("The 'openpyxl' package is missing. Install it once with:  pip install openpyxl   and run this again.")
    except Exception as ex:  # plain-language message, never a bare traceback
        say("\nSomething went wrong: %s\nNothing was changed. Fix the problem above and run again." % ex)
    if pause:
        try:
            input("\nPress Enter to close this window...")
        except EOFError:
            pass


if __name__ == "__main__":
    main()
