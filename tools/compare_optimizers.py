"""Compares PdfNetStack with other open source PDF optimizers on the public corpus.

Development tool only. Nothing in src/ depends on it.
Requires: pip install pypdfium2 numpy, the .NET 10 SDK, and whichever of qpdf, Ghostscript (gs)
and MuPDF (mutool) are on the PATH. A tool that is not installed is skipped and the report says so.
veraPDF (https://verapdf.org) is optional: with it, PDF/A conformance is checked by validation.

Usage:
  python tools/fetch_corpus.py --cache artifacts/corpus/files
  dotnet publish src/PdfNetStack.Cli -c Release -f net10.0 -r <rid> -p:NativeAot=true -o artifacts/aot
  python tools/compare_optimizers.py [--cache artifacts/corpus/files] [--out artifacts/compare]
                                     [--tool artifacts/aot/PdfNetStack.Tool] [--verapdf path]
                                     [--only id,id] [--tools name,name] [--dpi 100] [--pages 6]
                                     [--jobs 4] [--timeout 900]

PdfNetStack runs with the library defaults, as an application that calls PdfOptimizer.Optimize
without options gets them, plus one thread (--threads 1), since qpdf, MuPDF and Ghostscript use
one. --tool takes the Native AOT command line tool (the default when artifacts/aot has one) or
PdfNetStack.Tool.dll, run with dotnet. The JIT tool spends 0.4 to 0.5 s starting for every file,
which the time column then counts; the report says which one ran.

Bytes alone do not rank optimizers: a tool that downsamples every image or drops the tags, forms
and signatures always wins on size. So every output is scored three ways against its original:
  - size, as the tool wrote it (a tool that makes a file larger is charged for it);
  - rendering: PDFium renders the first pages and the last page of both, and the lowest SSIM on
    luma is kept, with page count, page geometry and PDFium's text compared too;
  - structure: tagging, form fields, outlines, attachments, signatures and annotations, counted by
    PDFium before and after. Anything the original had and the output lost is listed. A signed
    original whose output is not its own bytes with more appended has lost its signatures too:
    the signed byte ranges no longer hold what was signed, so they fail to verify. The same goes
    for usage rights (/Perms /UR3), the signature with which Adobe Reader enables features such
    as saving filled forms. An original that declares PDF/A and conforms to it must still
    conform: veraPDF validates both when it is installed. Without it, PDF/A-1 originals get a
    structural check of what a rewrite can break: PDF/A-1 is based on PDF 1.4, so the output
    must have no object streams or cross-reference streams, no encryption, and an unfiltered XMP
    packet that still declares PDF/A-1.
An output "keeps quality" when it opens, has the same pages, the same text, nothing lost and a
lowest SSIM of at least --min-ssim (0.95, the gate the Balanced preset uses). The head-to-head
table then compares two tools only on the files where both keep quality, so a tool cannot win the
total by wrecking one large file.

Files with incremental updates: PdfNetStack keeps them as they are unless AllowIncrementalUpdates
is set, because flattening discards the earlier revisions. qpdf, MuPDF and Ghostscript merge the
revisions without being asked. The main tables count these files at their original size for
PdfNetStack; a separate table shows them again with --allow-incremental.

SSIM is strict with scan grain and dithering: a scan reduced to 150 dpi can score 0.8 on a page
that looks the same, because the grain itself changed. Read "Outputs below the bar" with the pages
open before calling one a defect.

Each output is deleted once measured, so the run needs little disk.
"""

import argparse
import datetime
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zlib
from multiprocessing import Pool

import fetch_corpus

ROOT = fetch_corpus.ROOT
AOT_TOOL = os.path.join(ROOT, "artifacts", "aot", "PdfNetStack.Tool" + (".exe" if os.name == "nt" else ""))
JIT_TOOL = os.path.join(ROOT, "src", "PdfNetStack.Cli", "bin", "Release", "net10.0", "PdfNetStack.Tool.dll")
DEFAULT_OUT = os.path.join(ROOT, "artifacts", "compare")

# name, family, lossy, command. {in} and {out} are replaced by paths; the first word is looked up
# on the PATH, except "pdfnetstack", which runs the command line tool built from this repository
# with PDFNETSTACK_ARGS added. PdfNetStack runs with the library defaults: no option here changes
# what it writes.
TOOLS = [
    ("pdfnetstack", "PdfNetStack", False, ["pdfnetstack", "optimize", "{in}", "{out}"]),
    ("pdfnetstack-balanced", "PdfNetStack", True, ["pdfnetstack", "optimize", "{in}", "{out}", "--preset", "balanced"]),
    ("pdfnetstack-smallest", "PdfNetStack", True, ["pdfnetstack", "optimize", "{in}", "{out}", "--preset", "smallest"]),
    ("qpdf", "qpdf", False, ["qpdf", "--object-streams=generate", "--compress-streams=y", "--recompress-flate",
                             "--compression-level=9", "--warning-exit-0", "{in}", "{out}"]),
    ("mutool", "MuPDF", False, ["mutool", "clean", "-gggg", "-z", "{in}", "{out}"]),
    ("gs-printer", "Ghostscript", True, ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite",
                                         "-dPDFSETTINGS=/printer", "-sOutputFile={out}", "{in}"]),
    ("gs-ebook", "Ghostscript", True, ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite",
                                       "-dPDFSETTINGS=/ebook", "-sOutputFile={out}", "{in}"]),
]

# One thread, like the other tools, and the result as JSON, for the reason a file was kept.
PDFNETSTACK_ARGS = ["--threads", "1", "--json"]

# Runs only on the files the default run kept because of incremental updates, for their own table.
INCREMENTAL = ("pdfnetstack-incremental", ["pdfnetstack", "optimize", "{in}", "{out}", "--allow-incremental"])

# The pairs worth reading side by side: the same promise (lossless, or lossy at a similar setting).
PAIRS = [
    ("pdfnetstack", "qpdf"),
    ("pdfnetstack", "mutool"),
    ("pdfnetstack-balanced", "gs-printer"),
    ("pdfnetstack-smallest", "gs-ebook"),
]

FEATURES = ["tagged", "form", "outline", "attachments", "signatures", "annotations"]

# The usage rights signature names its transform method; /UR is the form before PDF 1.6.
USAGE_RIGHTS = re.compile(rb"/TransformMethod\s*/UR3?(?![A-Za-z0-9])|/UR3(?![A-Za-z0-9])")
PDFAID_PART = re.compile(rb"pdfaid:part(?:\s*=\s*[\"']|>)\s*(\d)")
PDFAID_CONFORMANCE = re.compile(rb"pdfaid:conformance(?:\s*=\s*[\"']|>)\s*([A-Za-z])")
XML_STREAM = re.compile(rb"/Subtype\s*/XML(?![A-Za-z0-9])")
NEW_IN_PDF15 = re.compile(rb"/Type\s*/(?:ObjStm|XRef)(?![A-Za-z0-9])")
ENCRYPT = re.compile(rb"/Encrypt(?![A-Za-z0-9])")


def pdfnetstack_launcher(tool):
    """How to start the PdfNetStack command line tool, or None when it is not built."""
    if not os.path.isfile(tool):
        return None
    if tool.endswith(".dll"):
        return ["dotnet", tool] if shutil.which("dotnet") else None
    return [tool]


def resolve(command, tool):
    """The command line to run, or None when the program is not installed."""
    if command[0] == "pdfnetstack":
        launcher = pdfnetstack_launcher(tool)
        return launcher + command[1:] + PDFNETSTACK_ARGS if launcher else None
    found = shutil.which(command[0])
    return [found] + command[1:] if found else None


def first_line(args):
    try:
        done = subprocess.run(args, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired):
        return None
    text = (done.stdout or done.stderr).strip().splitlines()
    return text[0] if text else None


def version_of(name):
    probes = {"qpdf": ["qpdf", "--version"], "mutool": ["mutool", "-v"], "gs": ["gs", "--version"]}
    if name == "pdfnetstack":
        commit = first_line(["git", "-C", ROOT, "rev-parse", "--short", "HEAD"])
        return f"this repository ({commit})" if commit else "this repository"
    probe = probes.get(name)
    if not probe or not shutil.which(probe[0]):
        return None
    return first_line(probe) or "unknown"


def luma(image):
    """Rec. 601 luma of a PDFium bitmap (BGR or BGRA byte order)."""
    import numpy as np

    x = image[..., :3].astype(np.float64)
    return 0.114 * x[..., 0] + 0.587 * x[..., 1] + 0.299 * x[..., 2]


def ssim(a, b, window=7):
    """Mean SSIM of two luma planes, with a uniform window as scikit-image uses by default."""
    import numpy as np

    # A page smaller than the window (the corpus has 4 pt pages) is scored as one window.
    window = min(window, a.shape[0], a.shape[1])

    def box(z):
        c = np.pad(np.cumsum(np.cumsum(z, 0), 1), ((1, 0), (1, 0)))
        return (c[window:, window:] - c[:-window, window:] - c[window:, :-window] + c[:-window, :-window]) / (window * window)

    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    ma, mb = box(a), box(b)
    va = box(a * a) - ma * ma
    vb = box(b * b) - mb * mb
    cov = box(a * b) - ma * mb
    s = ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2))
    return float(s.mean())


def page_selection(count, limit):
    if count <= limit:
        return list(range(count))
    return list(range(limit - 1)) + [count - 1]


def features(doc):
    """What a reader would miss if an optimizer dropped it, as PDFium counts it."""
    import pypdfium2.raw as c

    annotations = 0
    for i in range(len(doc)):
        page = doc[i]
        annotations += c.FPDFPage_GetAnnotCount(page.raw)
        page.close()
    return {
        "tagged": int(bool(c.FPDFCatalog_IsTagged(doc.raw))),
        "form": int(doc.get_formtype() != c.FORMTYPE_NONE),
        "outline": sum(1 for _ in doc.get_toc(max_depth=64)),
        "attachments": c.FPDFDoc_GetAttachmentCount(doc.raw),
        "signatures": c.FPDF_GetSignatureCount(doc.raw),
        "annotations": annotations,
    }


def reference(path, dpi, limit):
    """Renders and counts what is compared in every output of one original."""
    import pypdfium2 as pdfium

    doc = pdfium.PdfDocument(path)
    try:
        if doc.get_formtype() != pdfium.raw.FORMTYPE_NONE:
            doc.init_forms()
        pages = {}
        for i in page_selection(len(doc), limit):
            page = doc[i]
            pages[i] = (page.get_size(), page.get_rotation(), page.get_textpage().get_text_range(),
                        luma(page.render(scale=dpi / 72.0, may_draw_forms=True).to_numpy()))
            page.close()
        return {"count": len(doc), "pages": pages, "features": features(doc)}
    finally:
        doc.close()


def appends_to(original, path):
    """Whether the file at path is the original's bytes, unchanged, with anything after them."""
    with open(original, "rb") as a, open(path, "rb") as b:
        while True:
            x = a.read(1 << 20)
            if not x:
                return True
            if b.read(len(x)) != x:
                return False


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def pdfa_claim(data, raw_only=False):
    """The PDF/A flavour the file's XMP declares, such as "1b", "2u" or "4", or None.

    The packet is searched as the file stores it and, unless raw_only, in every Flate encoded XML
    stream too (PDF/A-1 forbids a filter on it; later parts allow one).
    """
    texts = [data]
    if not raw_only and b"pdfaid" not in data:
        for m in XML_STREAM.finditer(data):
            start = data.find(b"stream", m.end(), m.end() + 4096)
            if start < 0:
                continue
            start += 6
            start += 2 if data[start:start + 2] == b"\r\n" else 1
            end = data.find(b"endstream", start)
            if end < 0:
                continue
            try:
                texts.append(zlib.decompressobj().decompress(data[start:end], 1 << 22))
            except zlib.error:
                pass
    for text in texts:
        part = PDFAID_PART.search(text)
        if part:
            conformance = PDFAID_CONFORMANCE.search(text)
            return part.group(1).decode() + (conformance.group(1).decode().lower() if conformance else "")
    return None


def pdfa1_structure(data):
    """What breaks PDF/A-1 in the bytes, of what a rewrite can break; empty when nothing does."""
    problems = []
    claim = pdfa_claim(data, raw_only=True)
    if not claim or not claim.startswith("1"):
        problems.append("no unfiltered PDF/A-1 identification")
    if NEW_IN_PDF15.search(data):
        problems.append("object or cross-reference streams")
    if ENCRYPT.search(data):
        problems.append("encryption")
    return problems


def verapdf_compliance(verapdf, flavour, paths, timeout):
    """Whether veraPDF finds each file compliant with the flavour, by file name; None on failure."""
    with tempfile.TemporaryDirectory(prefix="verapdf-") as tmp:
        report = os.path.join(tmp, "report.xml")
        try:
            with open(report, "wb") as out:
                subprocess.run([verapdf, "--flavour", flavour, "--format", "xml", *paths],
                               stdout=out, stderr=subprocess.DEVNULL, timeout=timeout)
            root = ET.parse(report).getroot()
        except (OSError, subprocess.TimeoutExpired, ET.ParseError):
            return None
    compliant = {os.path.basename(p): False for p in paths}
    for job in (e for e in root.iter() if e.tag.endswith("job")):
        name = next((e.text for e in job.iter() if e.tag.endswith("name") and e.text), None)
        reports = [e for e in job.iter() if e.tag.endswith("validationReport")]
        if name and reports:
            compliant[os.path.basename(name)] = all(r.get("isCompliant") == "true" for r in reports)
    return compliant


def score(ref, path, dpi, original):
    """Compares one output with its original's reference."""
    import pypdfium2 as pdfium

    r = {"problems": [], "min_ssim": None, "lost": []}
    try:
        doc = pdfium.PdfDocument(path)
    except Exception as ex:  # noqa: BLE001 - an output PDFium refuses is a result, not a crash
        r["problems"].append(f"PDFium cannot open it: {ex}")
        return r
    try:
        if doc.get_formtype() != pdfium.raw.FORMTYPE_NONE:
            doc.init_forms()
        if len(doc) != ref["count"]:
            r["problems"].append(f"page count {ref['count']} -> {len(doc)}")
            return r
        values = []
        text_pages = []
        for i, (size, rotation, text, image) in ref["pages"].items():
            page = doc[i]
            if page.get_size() != size or page.get_rotation() != rotation:
                r["problems"].append(f"page {i + 1}: geometry differs")
                page.close()
                continue
            if page.get_textpage().get_text_range() != text:
                text_pages.append(i + 1)
            other = luma(page.render(scale=dpi / 72.0, may_draw_forms=True).to_numpy())
            page.close()
            values.append(ssim(image, other) if image.shape == other.shape else 0.0)
        if text_pages:
            r["problems"].append(f"text differs on page(s) {text_pages[:5]}")
        r["min_ssim"] = min(values) if values else None
        now = features(doc)
        r["lost"] = [f"{k} {ref['features'][k]} -> {now[k]}" for k in FEATURES if now[k] < ref["features"][k]]
        signed = ref["features"]["signatures"]
        unchanged = None
        if signed and now["signatures"] >= signed:
            unchanged = appends_to(original, path)
            if not unchanged:
                r["lost"].append(f"signatures {signed} no longer verify (signed bytes rewritten)")
        if ref["usage_rights"] and not (unchanged if unchanged is not None else appends_to(original, path)):
            r["lost"].append("usage rights (/UR3) voided or removed")
        return r
    finally:
        doc.close()


def run_tool(name, command, source, out, ref, dpi, timeout, min_ssim):
    """Runs one tool on one file and scores what it wrote; the output stays for the caller."""
    args = [a.replace("{in}", source).replace("{out}", out) for a in command]
    t = {"seconds": None, "bytes": None}
    started = time.perf_counter()
    try:
        done = subprocess.run(args, capture_output=True, timeout=timeout)
        t["seconds"] = round(time.perf_counter() - started, 3)
        if name.startswith("pdfnetstack"):
            try:
                outcome = json.loads(done.stdout.decode("utf-8", "replace"))
                t["disposition"], t["reason"] = outcome.get("Disposition"), outcome.get("Reason")
            except ValueError:
                pass
        if done.returncode != 0 or not os.path.isfile(out) or os.path.getsize(out) == 0:
            tail = done.stderr.decode("utf-8", "replace").strip().splitlines()[-2:]
            t["error"] = f"exit {done.returncode}: " + " | ".join(tail)
    except subprocess.TimeoutExpired:
        t["error"] = f"timed out after {timeout} s"
    if "error" not in t:
        t["bytes"] = os.path.getsize(out)
        t.update(score(ref, out, dpi, source))
    keeps(t, min_ssim)
    return t


def keeps(t, min_ssim):
    t["keeps_quality"] = ("error" not in t and not t["problems"] and not t["lost"]
                          and t["min_ssim"] is not None and t["min_ssim"] >= min_ssim)


def check_pdfa(result, source, outputs, data, verapdf, timeout, min_ssim):
    """Adds a lost PDF/A conformance to each output that broke it, for an original that has it."""
    flavour = result["pdfa"]
    if verapdf:
        compliant = verapdf_compliance(verapdf, flavour, [source] + [path for _, path in outputs], timeout)
        if compliant is None:
            result["pdfa_check"] = "veraPDF failed"
            return
        if not compliant[os.path.basename(source)]:
            result["pdfa_check"] = "the original does not conform"
            return
        result["pdfa_check"] = "veraPDF"
        broken = {name for name, path in outputs if not compliant[os.path.basename(path)]}
        detail = {name: "veraPDF" for name in broken}
    else:
        if not flavour.startswith("1"):
            result["pdfa_check"] = "not checked (needs veraPDF)"
            return
        if pdfa1_structure(data):
            result["pdfa_check"] = "the original does not pass the structural check"
            return
        result["pdfa_check"] = "structural"
        detail = {}
        for name, path in outputs:
            problems = pdfa1_structure(read_bytes(path))
            if problems:
                detail[name] = ", ".join(problems)
    for name, why in detail.items():
        t = result["tools"][name]
        t["lost"].append(f"PDF/A-{flavour} conformance ({why})")
        keeps(t, min_ssim)


def run_file(job):
    entry, source, tools, incremental, dpi, limit, timeout, min_ssim, verapdf = job
    data = read_bytes(source)
    result = {"id": entry["id"], "bytes": len(data), "tools": {},
              "pdfa": pdfa_claim(data), "usage_rights": bool(USAGE_RIGHTS.search(data))}
    try:
        ref = reference(source, dpi, limit)
    except Exception as ex:  # noqa: BLE001 - an original PDFium cannot read is skipped and listed
        result["error"] = f"PDFium cannot read the original: {ex}"
        return result
    ref["usage_rights"] = result["usage_rights"]
    result["features"] = ref["features"]
    work = tempfile.mkdtemp(prefix="compare-")
    kept = []
    try:
        runs = list(tools)
        while runs:
            name, command = runs.pop(0)
            out = os.path.join(work, name + ".pdf")
            t = run_tool(name, command, source, out, ref, dpi, timeout, min_ssim)
            result["tools"][name] = t
            if name == "pdfnetstack" and incremental and "incremental updates" in (t.get("reason") or ""):
                runs.insert(0, incremental)
            if result["pdfa"] and "error" not in t:
                kept.append((name, out))
            elif os.path.exists(out):
                os.remove(out)
        if kept:
            check_pdfa(result, source, kept, data, verapdf, timeout, min_ssim)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    return result


def pct(saved, total):
    return f"{100.0 * saved / total:.1f}%" if total else "n/a"


def mib(n):
    return f"{n / (1024 * 1024):.1f} MiB" if n >= 1024 * 1024 else f"{n / 1024:.0f} KiB"


def write_report(results, available, versions, args, out_dir, cli_kind, pdfa_kind):
    names = [n for n, *_ in TOOLS if n in available]
    lossy = {n: l for n, _, l, _ in TOOLS}
    files = [r for r in results if "error" not in r]
    lines = [
        "# Optimizer comparison",
        "",
        f"{len(files)} files from the public corpus (eng/corpus/manifest.json), "
        f"{datetime.date.today().isoformat()}, {platform.system()} {platform.machine()}.",
        f"PdfNetStack: library defaults, {cli_kind}, one thread (`--threads 1`), like the other tools.",
        f"Rendering: PDFium at {args.dpi} dpi, up to {args.pages} pages per file. "
        f"An output keeps quality when it has the same pages and text, loses nothing below, "
        f"and its lowest page SSIM is at least {args.min_ssim}. "
        f"Losing usage rights (`/UR3`) or PDF/A conformance counts as losing a feature; PDF/A: {pdfa_kind}.",
        "",
        "| Tool | Kind | Version | Total out | Saved | Keeps quality | Lost a feature | Failed | Time |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    total_in = sum(r["bytes"] for r in files)
    for n in names:
        rows = [r["tools"][n] for r in files]
        out = sum(t["bytes"] if t["bytes"] is not None else r["bytes"] for r, t in zip(files, rows))
        keeps_count = sum(1 for t in rows if t["keeps_quality"])
        lost = sum(1 for t in rows if t.get("lost"))
        failed = sum(1 for t in rows if "error" in t)
        seconds = sum(t["seconds"] or 0 for t in rows)
        lines.append(f"| {n} | {'lossy' if lossy[n] else 'lossless'} | {versions.get(n) or ''} | {mib(out)} | "
                     f"{pct(total_in - out, total_in)} | {keeps_count}/{len(files)} | {lost} | {failed} | {seconds:.0f} s |")
    lines += ["", f"Input: {mib(total_in)}. A failed output is counted at its original size. "
                  "Time is the wall clock of each run, one process per file, summed.", ""]

    lines += ["## Head to head at equal quality", "",
              "Only files where both outputs keep quality count, so neither side wins by damaging a file.", "",
              "| PdfNetStack | Other | Files | PdfNetStack total | Other total | Smaller on | Larger on |",
              "|---|---|---:|---:|---:|---:|---:|"]
    for a, b in PAIRS:
        if a not in available or b not in available:
            continue
        both = [(r["tools"][a], r["tools"][b]) for r in files
                if r["tools"][a]["keeps_quality"] and r["tools"][b]["keeps_quality"]]
        ta, tb = sum(x["bytes"] for x, _ in both), sum(y["bytes"] for _, y in both)
        smaller = sum(1 for x, y in both if x["bytes"] < y["bytes"])
        larger = sum(1 for x, y in both if x["bytes"] > y["bytes"])
        lines.append(f"| {a} | {b} | {len(both)} | {mib(ta)} | {mib(tb)} | {smaller} | {larger} |")

    incremental = [r for r in files if INCREMENTAL[0] in r["tools"]]
    if incremental:
        others = [n for n in ("qpdf", "mutool") if n in available]

        def cell(r, n):
            t = r["tools"][n]
            if "error" in t:
                return "failed"
            if t.get("disposition") and t["disposition"] != "Optimized":
                return "kept as is"
            return mib(t["bytes"]) + ("" if t["keeps_quality"] else " (below the bar)")

        lines += ["", "## Files with incremental updates", "",
                  "PdfNetStack keeps these files as they are unless `AllowIncrementalUpdates` is set "
                  "(`--allow-incremental`), because flattening discards the earlier revisions. The tables above "
                  "count them at their original size for PdfNetStack. qpdf, MuPDF and Ghostscript merge the "
                  "revisions without being asked.", "",
                  "| File | Input | PdfNetStack default | PdfNetStack `--allow-incremental` | " + " | ".join(others) + " |",
                  "|---|---:|---:|---:|" + "---:|" * len(others)]
        for r in incremental:
            lines.append(f"| {r['id']} | {mib(r['bytes'])} | {cell(r, 'pdfnetstack')} | {cell(r, INCREMENTAL[0])} | "
                         + " | ".join(cell(r, n) for n in others) + " |")
        flat = [r["tools"][INCREMENTAL[0]] for r in incremental]
        total = sum(r["bytes"] for r in incremental)
        flat_out = sum(t["bytes"] if t["bytes"] is not None else r["bytes"] for r, t in zip(incremental, flat))
        lines += ["", f"With `--allow-incremental` these {len(incremental)} files go from {mib(total)} to "
                      f"{mib(flat_out)} ({pct(total - flat_out, total)} saved); "
                      f"{sum(1 for t in flat if t['keeps_quality'])} of them keep quality."]

    lost_lines = []
    for r in files:
        for n in names:
            t = r["tools"][n]
            if t.get("lost"):
                lost_lines.append(f"- {r['id']}, {n}: " + ", ".join(t["lost"]))
    if lost_lines:
        lines += ["", "## Features lost", ""] + lost_lines

    problem_lines = []
    for r in results:
        if "error" in r:
            problem_lines.append(f"- {r['id']}: {r['error']}")
            continue
        for n in names:
            t = r["tools"][n]
            if "error" in t:
                problem_lines.append(f"- {r['id']}, {n}: {t['error']}")
            elif t["problems"]:
                problem_lines.append(f"- {r['id']}, {n}: " + "; ".join(t["problems"]))
            elif t["min_ssim"] is not None and t["min_ssim"] < args.min_ssim:
                problem_lines.append(f"- {r['id']}, {n}: lowest SSIM {t['min_ssim']:.4f}")
    if problem_lines:
        lines += ["", "## Outputs below the bar", ""] + problem_lines

    checked = [r for r in files if r.get("pdfa")]
    if checked:
        lines += ["", "## PDF/A originals", ""]
        lines += [f"- {r['id']}: PDF/A-{r['pdfa']}, {r.get('pdfa_check', 'not checked')}" for r in checked]

    missing = [n for n, *_ in TOOLS if n not in available]
    if missing:
        lines += ["", "Not installed, so not compared: " + ", ".join(missing) + "."]

    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "compare.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out_dir, "compare.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"date": datetime.date.today().isoformat(), "dpi": args.dpi, "pages": args.pages,
                   "min_ssim": args.min_ssim, "pdfnetstack": cli_kind, "pdfa_check": pdfa_kind,
                   "versions": versions, "files": results}, f, indent=1)
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cache", default=os.path.join(ROOT, "artifacts", "corpus", "files"))
    p.add_argument("--manifest", default=os.path.join(ROOT, "eng", "corpus", "manifest.json"))
    p.add_argument("--out", default=DEFAULT_OUT)
    p.add_argument("--tool", default=AOT_TOOL if os.path.isfile(AOT_TOOL) else JIT_TOOL,
                   help="the Native AOT PdfNetStack.Tool, or PdfNetStack.Tool.dll to run with dotnet")
    p.add_argument("--verapdf", default=shutil.which("verapdf"), help="the veraPDF command line launcher")
    p.add_argument("--only", help="comma-separated corpus ids")
    p.add_argument("--tools", help="comma-separated tool names, default all installed")
    p.add_argument("--dpi", type=int, default=100)
    p.add_argument("--pages", type=int, default=6)
    p.add_argument("--min-ssim", type=float, default=0.95)
    p.add_argument("--jobs", type=int, default=4)
    p.add_argument("--timeout", type=int, default=900)
    args = p.parse_args()

    manifest = fetch_corpus.load_manifest(args.manifest)
    entries = manifest["files"]
    if args.only:
        wanted = set(args.only.split(","))
        entries = [e for e in entries if e["id"] in wanted]
    wanted_tools = set(args.tools.split(",")) if args.tools else None

    tools, versions = [], {}
    for name, _, _, command in TOOLS:
        if wanted_tools and name not in wanted_tools:
            continue
        resolved = resolve(command, args.tool)
        if resolved:
            tools.append((name, resolved))
            versions[name] = version_of(command[0])
    if not tools:
        sys.exit("No optimizer found. Build src/PdfNetStack.Cli or install qpdf, gs or mutool.")
    incremental = None
    if any(n == "pdfnetstack" for n, _ in tools):
        incremental = (INCREMENTAL[0], resolve(INCREMENTAL[1], args.tool))
    cli_kind = ("the JIT command line tool, whose start up (0.4 to 0.5 s per file) the time includes"
                if args.tool.endswith(".dll") else "the Native AOT command line tool")

    verapdf = args.verapdf if args.verapdf and os.path.isfile(args.verapdf) else None
    if verapdf:
        pdfa_kind = "originals that conform to the PDF/A flavour they declare, validated with " + (
            first_line([verapdf, "--version"]) or "veraPDF")
    else:
        pdfa_kind = ("veraPDF not installed, so PDF/A-1 originals get a structural check (no object or "
                     "cross-reference streams, no encryption, an unfiltered PDF/A-1 identification) and later "
                     "parts are not checked")

    jobs, missing = [], []
    for e in entries:
        path = fetch_corpus.cached_path(args.cache, e)
        if os.path.isfile(path):
            jobs.append((e, path, tools, incremental, args.dpi, args.pages, args.timeout, args.min_ssim, verapdf))
        else:
            missing.append(e["id"])
    if missing:
        print(f"{len(missing)} corpus file(s) not in the cache, skipped: {', '.join(missing[:10])}", file=sys.stderr)
    if not jobs:
        sys.exit("No corpus files found. Run tools/fetch_corpus.py first.")

    results = []
    with Pool(max(1, args.jobs)) as pool:
        for r in pool.imap_unordered(run_file, jobs):
            results.append(r)
            print(f"[{len(results)}/{len(jobs)}] {r['id']}", file=sys.stderr, flush=True)
    results.sort(key=lambda r: r["id"])
    print(write_report(results, {n for n, _ in tools}, versions, args, args.out, cli_kind, pdfa_kind))


if __name__ == "__main__":
    main()
