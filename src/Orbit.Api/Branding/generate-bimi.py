"""Build the BIMI icon using only Python's standard library."""

import math
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def curve_extrema(points):
    p0, p1, p2, p3 = points
    a = -p0 + 3 * p1 - 3 * p2 + p3
    b = 2 * (p0 - 2 * p1 + p2)
    c = p1 - p0
    roots = []
    if abs(a) < 1e-12:
        if abs(b) > 1e-12:
            roots = [-c / b]
    elif b * b - 4 * a * c >= 0:
        delta = math.sqrt(b * b - 4 * a * c)
        roots = [(-b + delta) / (2 * a), (-b - delta) / (2 * a)]
    return [p0, p3] + [
        (1 - t) ** 3 * p0 + 3 * (1 - t) ** 2 * t * p1
        + 3 * (1 - t) * t ** 2 * p2 + t ** 3 * p3
        for t in roots if 0 < t < 1
    ]


def ink_bounds(paths):
    axes = [[], []]
    for path in paths:
        data = path.attrib["d"]
        if re.sub(r"[MLCZ\d.\s,+-]", "", data):
            raise ValueError("The canonical mark must use absolute M, L, C and Z commands.")
        tokens = iter(re.findall(r"[MLCZ]|[-+]?\d*\.?\d+", data))
        current = start = (0, 0)
        for command in tokens:
            if command == "Z":
                current = start
                continue
            count = {"M": 2, "L": 2, "C": 6}[command]
            values = [float(next(tokens)) for _ in range(count)]
            end = tuple(values[-2:])
            for axis in range(2):
                axes[axis].extend(
                    curve_extrema([current[axis], *values[axis::2]])
                    if command == "C" else [end[axis]]
                )
            current = end
            if command == "M":
                start = end
    return [(min(axis), max(axis)) for axis in axes]


directory = Path(__file__).resolve().parent
namespace = "http://www.w3.org/2000/svg"
ET.register_namespace("", namespace)
source = ET.parse(directory / "Sources/orbit-mark.svg").getroot()
paths = source.findall(f"{{{namespace}}}path")
if len(paths) != 2 or source.attrib["viewBox"] != "0 0 1024 1024":
    raise ValueError("Expected the canonical Orbit body and moon on a 1024 canvas.")
(left, right), (top, bottom) = ink_bounds(paths)
scale = 1024 * 0.6 / (right - left)
dx = 512 - scale * (left + right) / 2
dy = 512 - scale * (top + bottom) / 2
transform = f"translate({dx:.12f} {dy:.12f}) scale({scale:.12f})"
root = ET.Element(f"{{{namespace}}}svg", {
    "version": "1.2", "baseProfile": "tiny-ps",
    "width": "1024", "height": "1024", "viewBox": "0 0 1024 1024",
})
ET.SubElement(root, "title").text = "Orbit"
ET.SubElement(root, "desc").text = "Orbit's ringed planet and orange moon on a solid dark background."
ET.SubElement(root, "rect", {"width": "1024", "height": "1024", "fill": "#09090B"})
for path, color in zip(paths, ["#F4F4F6", "#C4530F"], strict=True):
    attributes = {"fill": color, "transform": transform, "d": path.attrib["d"]}
    if "fill-rule" in path.attrib:
        attributes["fill-rule"] = path.attrib["fill-rule"]
    ET.SubElement(root, "path", attributes)
ET.indent(root)
content = ET.tostring(root, encoding="utf-8", xml_declaration=True) + b"\n"
destination = directory / "Assets/orbit-bimi.svg"
if sys.argv[1:] == ["--check"]:
    if destination.read_bytes() != content:
        sys.exit("The committed BIMI icon differs from its generated source.")
elif sys.argv[1:]:
    sys.exit("Usage: python3 generate-bimi.py [--check]")
else:
    destination.write_bytes(content)
print(f"BIMI icon: {len(content)} bytes")
