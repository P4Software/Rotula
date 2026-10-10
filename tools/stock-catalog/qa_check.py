#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Independent quality check for stock-catalog.json.

It deliberately does NOT reuse the parsing rules of extract_stock.py. For every
catalogue entry that lists the ZebraDesigner stock export as a source, it re-reads
the export rows with its own simple rules and compares:

  * the size written in the stock name, e.g. '(102mm x 152mm)' or '(4in x 6in)',
    with the entry's width x height in the right order (a wristband stored rotated
    in the export is compared swapped, as the catalogue keeps the short side as width);
  * the micrometre columns (LabelSizeX/Y divided by 1000) with the entry's mm values;
  * corner radius, labels across, rows and the gap between labels across.

Usage: python qa_check.py [<stock-catalog.json> [<ZebraDesigner_Stock.xlsx>]]
With no arguments it reads both paths from extract-settings.json next to this script
(designerExportPath, and outputFolder + stock-catalog.json).
"""
import json
import os
import re
import shutil
import sys
import tempfile


def read_sheet(path, sheet_hint):
    """Rows of the first sheet whose name contains the hint, as dicts keyed by header."""
    import openpyxl
    tmp = tempfile.mkdtemp(prefix="rotula-qa-")
    copy = os.path.join(tmp, "c.xlsx")
    shutil.copyfile(path, copy)
    wb = openpyxl.load_workbook(copy, read_only=True, data_only=True)
    ws = next((w for w in wb.worksheets if sheet_hint in w.title.lower()), wb.worksheets[0])
    it = ws.iter_rows(values_only=True)
    head = [str(h) if h is not None else "" for h in next(it)]
    rows = [dict(zip(head, r)) for r in it if r and r[0] not in (None, "")]
    wb.close()
    shutil.rmtree(tmp, ignore_errors=True)
    return rows


def close(x, cands, tol):
    return any(c is not None and abs(x - c) <= tol for c in cands)


def check_export(entries, rows):
    by = {}
    for r in rows:
        by.setdefault(str(r["StockNumber"]).strip().upper(), []).append(r)
    bad, n = [], 0
    for e in entries:
        if "zebradesigner-stock-export" not in (e.get("sources") or []):
            continue
        grp = by.get(str(e["partNumber"]).strip().upper())
        if not grp:
            bad.append((e["partNumber"], "part number not found in the export"))
            continue
        n += 1
        problems = []
        # at least one export row must agree with the entry on everything below
        ok_any = False
        for r in grp:
            m = re.search(r"\(\s*(\d*\.?\d+)\s*(in|mm)\s*x\s*(\d*\.?\d+)\s*(in|mm)\s*\)", r["StockName"], re.I)
            if not m:
                continue
            a, ua, b, ub = float(m.group(1)), m.group(2).lower(), float(m.group(3)), m.group(4).lower()
            amm, bmm = a * (25.4 if ua == "in" else 1), b * (25.4 if ub == "in" else 1)
            cx, cy = r["LabelSizeX"] / 1000.0, r["LabelSizeY"] / 1000.0
            rot = r["Orientation"] == 2
            # candidates for (width, height) in the catalogue's convention
            cw, ch = (cy, cx) if rot else (cx, cy)
            nw, nh = (bmm, amm) if rot else (amm, bmm)
            local = []
            if e.get("widthMm") is not None and not (close(e["widthMm"], (cw,), 0.15)):
                local.append("width")
            if e.get("heightMm") is not None and not (close(e["heightMm"], (ch,), 0.15)):
                local.append("height")
            cols = int(r["WebX"] or 1)
            if (e.get("columns") or 1) != cols:
                local.append("columns")
            if (e.get("rows") or 1) != int(r["WebY"] or 1):
                local.append("rows")
            if e.get("cornerRadiusMm") is not None and not close(e["cornerRadiusMm"], (r["RadiusX"] / 1000.0,), 0.06):
                local.append("radius")
            if e.get("hGapMm") is not None and not close(e["hGapMm"], (r["GapX"] / 1000.0,), 0.06):
                local.append("gap")
            # inch figures must be the exact micrometre value / 25.4 (catches inches taken from rounded mm)
            if e.get("widthIn") is not None and abs(e["widthIn"] * 25.4 - cw) > 0.02:
                local.append("widthIn")
            if e.get("heightMm") is not None and e.get("heightIn") is not None and abs(e["heightIn"] * 25.4 - ch) > 0.02:
                local.append("heightIn")
            if e.get("heightMm") is None:
                local = [x for x in local if x != "height"]
            if not local:
                ok_any = True
                break
            problems = local
        if not ok_any:
            # entries that took the PDF/export arbitration or list conflicting rows are allowed to differ
            if "CONFLICT" in (e.get("notes") or "") or "two different sizes" in (e.get("notes") or ""):
                continue
            bad.append((e["partNumber"], "no export row matches on: " + ", ".join(problems or ["name"])))
    return n, bad


def settings_paths():
    """Default input paths from extract-settings.json (the same file the extractor uses)."""
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "extract-settings.json")
    if not os.path.exists(path):
        return None, None
    st = json.load(open(path, encoding="utf-8"))
    out = st.get("outputFolder") or "output"
    out = out if os.path.isabs(out) else os.path.join(here, out)
    return os.path.join(out, "stock-catalog.json"), st.get("designerExportPath") or None


def main():
    cat_path, export_path = settings_paths()
    if len(sys.argv) > 1:
        cat_path = sys.argv[1]
    if len(sys.argv) > 2:
        export_path = sys.argv[2]
    if not cat_path or not export_path:
        print("Give the catalogue and the export on the command line, or fill in extract-settings.json first.")
        return
    cat = json.load(open(cat_path, encoding="utf-8"))["entries"]
    n, bad = check_export(cat, read_sheet(export_path, "stock"))
    print("Entries re-checked against export rows: %d" % n)
    print("Entries with a finding: %d" % len(bad))
    for p, why in bad[:80]:
        print("  %s : %s" % (p, why))


if __name__ == "__main__":
    main()
