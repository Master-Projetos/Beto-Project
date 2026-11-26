#!/usr/bin/env python3
"""Converte DWG (ou DXF) em KML/KMZ para abrir no Google Earth.

Uso:
    python dwg2kmz.py projeto.dwg --crs EPSG:31983            # gera projeto.kmz
    python dwg2kmz.py projeto.dxf --utm 23S -o saida.kml
    python dwg2kmz.py projeto.dwg --crs EPSG:4326             # ja em lat/lon

DWG e um formato fechado: antes da leitura ele e convertido para DXF com o
ODA File Converter (ODAFileConverter) ou o LibreDWG (dwg2dxf), o que estiver
instalado (ou o indicado em --converter / variavel DWG_CONVERTER).

O desenho precisa estar em um sistema de coordenadas conhecido; informe-o com
--crs (ex.: EPSG:31983 = SIRGAS 2000 / UTM 23S) ou --utm (ex.: 23S). Se as
coordenadas ja parecem lon/lat (-180..180, -90..90) assume-se WGS84.
"""
from __future__ import annotations

import argparse
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from collections import OrderedDict
from pathlib import Path
from xml.sax.saxutils import escape

import ezdxf
from ezdxf import colors as dxf_colors
from ezdxf import path as dxf_path
from pyproj import CRS, Transformer

MAX_BLOCK_DEPTH = 8
PATH_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE", "CIRCLE", "ARC", "ELLIPSE", "SPLINE"}


class ConversionError(Exception):
    pass


# ----------------------------------------------------------------- DWG -> DXF
def dwg_to_dxf(dwg: Path, workdir: Path, converter: str | None = None) -> Path:
    """Converte um DWG em DXF usando uma ferramenta externa."""
    out = workdir / (dwg.stem + ".dxf")
    cand = converter or os.environ.get("DWG_CONVERTER")
    candidates = [cand] if cand else []
    candidates += ["dwg2dxf", "ODAFileConverter", "ODAFileConverter.exe"]
    exe = next((p for c in candidates if c and (p := shutil.which(c))), None)
    if exe is None:
        raise ConversionError(
            "Nenhum conversor de DWG encontrado. Instale o ODA File Converter "
            "(gratuito) ou o LibreDWG (dwg2dxf), ou exporte o desenho como DXF no "
            "AutoCAD/BricsCAD/LibreCAD e passe o .dxf. Tambem da para indicar o "
            "caminho com --converter."
        )
    name = Path(exe).name.lower()
    if name.startswith("odafileconverter"):
        src = workdir / "in"
        dst = workdir / "out"
        src.mkdir()
        dst.mkdir()
        shutil.copy(dwg, src / dwg.name)
        # entrada saida versao tipo recursivo auditar [filtro]
        cmd = [exe, str(src), str(dst), "ACAD2018", "DXF", "0", "1", dwg.name]
        produced = dst / (dwg.stem + ".dxf")
    else:
        cmd = [exe, "-y", "-o", str(out), str(dwg)]
        produced = out
    res = subprocess.run(cmd, capture_output=True, text=True)
    if not produced.exists():
        raise ConversionError(
            f"Falha ao converter DWG em DXF ({Path(exe).name}):\n{res.stdout}{res.stderr}"
        )
    return produced


# ------------------------------------------------------------------------ CRS
def resolve_crs(crs: str | None, utm: str | None) -> CRS | None:
    if crs:
        try:
            return CRS.from_user_input(crs)
        except Exception as e:  # pyproj levanta CRSError
            raise ConversionError(f"CRS invalido '{crs}': {e}") from e
    if utm:
        m = re.fullmatch(r"\s*(\d{1,2})\s*([NSns])\s*", utm)
        if not m or not 1 <= int(m.group(1)) <= 60:
            raise ConversionError("--utm deve ser no formato <zona><N|S>, ex.: 23S")
        south = m.group(2).upper() == "S"
        return CRS.from_dict({"proj": "utm", "zone": int(m.group(1)),
                              "south": south, "datum": "WGS84", "units": "m"})
    return None


# -------------------------------------------------------------------- Geometria
def _iter_entities(layout, depth=0):
    """Percorre as entidades, explodindo INSERTs (blocos) recursivamente."""
    for e in layout:
        t = e.dxftype()
        if t == "INSERT":
            if depth >= MAX_BLOCK_DEPTH:
                continue
            try:
                sub = list(e.virtual_entities())
            except Exception:
                continue
            yield from _iter_entities(sub, depth + 1)
        else:
            yield e


def _entity_rgb(e, doc):
    try:
        if e.dxf.hasattr("true_color"):
            return dxf_colors.int2rgb(e.dxf.true_color)
        aci = e.dxf.get("color", 256)
        if aci == 256 or aci == 0:  # BYLAYER / BYBLOCK
            layer = doc.layers.get(e.dxf.layer) if e.dxf.layer in doc.layers else None
            if layer is not None:
                if layer.dxf.hasattr("true_color"):
                    return dxf_colors.int2rgb(layer.dxf.true_color)
                aci = abs(layer.dxf.color)
            else:
                aci = 7
        return dxf_colors.aci2rgb(abs(aci))
    except Exception:
        return (255, 255, 255)


def _kml_color(rgb, alpha=255):
    r, g, b = rgb
    if (r, g, b) == (0, 0, 0):  # preto some no mapa escuro
        r = g = b = 255
    return f"{alpha:02x}{b:02x}{g:02x}{r:02x}"


class Converter:
    def __init__(self, doc, crs: CRS | None, tolerance=0.5, scale=1.0,
                 offset=(0.0, 0.0), include_text=True):
        self.doc = doc
        self.tolerance = tolerance
        self.scale = scale
        self.offset = offset
        self.include_text = include_text
        self.crs = crs
        self._tf = None
        self.skipped = 0
        self.stats = OrderedDict()

    # -- coordenadas
    def _prepare_transformer(self, sample):
        xs = [self.scale * x + self.offset[0] for x, _ in sample]
        ys = [self.scale * y + self.offset[1] for _, y in sample]
        looks_geo = xs and all(-180 <= x <= 180 for x in xs) and all(-90 <= y <= 90 for y in ys)
        if self.crs is None:
            if looks_geo:
                self._tf = lambda x, y: (x, y)
                return
            raise ConversionError(
                "As coordenadas do desenho nao parecem lon/lat. Informe o sistema de "
                "coordenadas com --crs (ex.: EPSG:31983) ou --utm (ex.: 23S)."
            )
        tr = Transformer.from_crs(self.crs, 4326, always_xy=True)
        self._tf = lambda x, y: tr.transform(x, y)

    def to_lonlat(self, x, y):
        return self._tf(self.scale * x + self.offset[0], self.scale * y + self.offset[1])

    # -- extracao
    def _lines(self, e):
        """Devolve lista de polilinhas [(x, y), ...] de uma entidade de caminho."""
        t = e.dxftype()
        if t == "LINE":
            return [[(e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)]]
        p = dxf_path.make_path(e)
        out = []
        for sub in p.sub_paths():
            pts = [(v.x, v.y) for v in sub.flattening(self.tolerance / max(self.scale, 1e-12), segments=8)]
            if len(pts) >= 2:
                out.append(pts)
        return out

    def collect(self):
        items = []  # (layer, kind, name, rgb, coords_xy)
        for e in _iter_entities(self.doc.modelspace()):
            t = e.dxftype()
            layer = e.dxf.get("layer", "0")
            try:
                if t in PATH_TYPES:
                    rgb = _entity_rgb(e, self.doc)
                    for pts in self._lines(e):
                        items.append((layer, "line", None, rgb, pts))
                elif t == "POINT":
                    items.append((layer, "point", None, _entity_rgb(e, self.doc),
                                  [(e.dxf.location.x, e.dxf.location.y)]))
                elif t in ("TEXT", "MTEXT") and self.include_text:
                    txt = e.plain_text() if t == "MTEXT" else e.dxf.text
                    txt = " ".join(txt.split())
                    if txt:
                        ins = e.dxf.insert
                        items.append((layer, "text", txt, _entity_rgb(e, self.doc),
                                      [(ins.x, ins.y)]))
                else:
                    self.skipped += 1
                    continue
            except Exception:
                self.skipped += 1
                continue
            self.stats[t] = self.stats.get(t, 0) + 1
        if not items:
            raise ConversionError("Nenhuma geometria convertivel encontrada no desenho.")
        sample = [pt for it in items[:2000] for pt in it[4][:1]]
        self._prepare_transformer(sample)
        return items

    # -- KML
    def build_kml(self, name):
        items = self.collect()
        layers = OrderedDict()
        for it in items:
            layers.setdefault(it[0], []).append(it)

        out = ['<?xml version="1.0" encoding="UTF-8"?>',
               '<kml xmlns="http://www.opengis.net/kml/2.2"><Document>',
               f"<name>{escape(name)}</name>"]
        n_pm = 0
        for layer, lst in layers.items():
            out.append(f"<Folder><name>{escape(layer)}</name>")
            for _, kind, label, rgb, pts in lst:
                ll = [self.to_lonlat(x, y) for x, y in pts]
                if any(not (math.isfinite(a) and math.isfinite(b)) for a, b in ll):
                    self.skipped += 1
                    continue
                coords = " ".join(f"{a:.8f},{b:.8f},0" for a, b in ll)
                col = _kml_color(rgb)
                if kind == "line":
                    out.append(f"<Placemark><Style><LineStyle><color>{col}</color>"
                               f"<width>1.5</width></LineStyle></Style>"
                               f"<LineString><tessellate>1</tessellate>"
                               f"<coordinates>{coords}</coordinates></LineString></Placemark>")
                elif kind == "point":
                    out.append(f"<Placemark><Style><IconStyle><color>{col}</color>"
                               f"<scale>0.6</scale></IconStyle><LabelStyle><scale>0</scale></LabelStyle></Style>"
                               f"<Point><coordinates>{coords}</coordinates></Point></Placemark>")
                else:
                    out.append(f"<Placemark><name>{escape(label)}</name><Style><IconStyle>"
                               f"<scale>0</scale></IconStyle><LabelStyle><color>{col}</color>"
                               f"<scale>0.8</scale></LabelStyle></Style>"
                               f"<Point><coordinates>{coords}</coordinates></Point></Placemark>")
                n_pm += 1
            out.append("</Folder>")
        out.append("</Document></kml>")
        return "\n".join(out), n_pm


def write_output(kml: str, dest: Path):
    if dest.suffix.lower() == ".kmz":
        with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("doc.kml", kml.encode("utf-8"))
    else:
        dest.write_text(kml, encoding="utf-8")


def convert(src: Path, dest: Path | None = None, crs: str | None = None, utm: str | None = None,
            tolerance=0.5, scale=1.0, offset=(0.0, 0.0), include_text=True,
            converter: str | None = None):
    """Converte `src` (.dwg/.dxf) em KML/KMZ. Devolve (destino, nº de placemarks, avisos)."""
    src = Path(src)
    if not src.is_file():
        raise ConversionError(f"Arquivo nao encontrado: {src}")
    dest = Path(dest) if dest else src.with_suffix(".kmz")
    if dest.suffix.lower() not in (".kml", ".kmz"):
        raise ConversionError("A saida deve terminar em .kml ou .kmz")
    ext = src.suffix.lower()
    if ext not in (".dwg", ".dxf"):
        raise ConversionError("A entrada deve ser .dwg ou .dxf")
    crs_obj = resolve_crs(crs, utm)

    with tempfile.TemporaryDirectory() as tmp:
        dxf = dwg_to_dxf(src, Path(tmp), converter) if ext == ".dwg" else src
        try:
            doc = ezdxf.readfile(str(dxf))
        except (IOError, ezdxf.DXFStructureError) as e:
            raise ConversionError(f"Nao foi possivel ler o DXF: {e}") from e
        conv = Converter(doc, crs_obj, tolerance, scale, offset, include_text)
        kml, n = conv.build_kml(src.stem)
    write_output(kml, dest)
    warnings = []
    if conv.skipped:
        warnings.append(f"{conv.skipped} entidade(s) sem suporte foram ignoradas (hachuras, cotas, 3D...).")
    return dest, n, warnings


def main(argv=None):
    ap = argparse.ArgumentParser(description="Converte DWG/DXF em KML/KMZ.")
    ap.add_argument("entrada", help="arquivo .dwg ou .dxf")
    ap.add_argument("-o", "--saida", help="arquivo .kml ou .kmz (padrao: mesmo nome, .kmz)")
    ap.add_argument("--crs", help="CRS do desenho, ex.: EPSG:31983")
    ap.add_argument("--utm", help="atalho para UTM WGS84, ex.: 23S")
    ap.add_argument("--tolerance", type=float, default=0.5,
                    help="precisao ao discretizar arcos/curvas, em metros (padrao 0.5)")
    ap.add_argument("--scale", type=float, default=1.0,
                    help="fator para converter as unidades do desenho no do CRS (ex.: 0.001 se mm -> m)")
    ap.add_argument("--offset", type=float, nargs=2, metavar=("DX", "DY"), default=(0.0, 0.0),
                    help="deslocamento somado as coordenadas depois da escala")
    ap.add_argument("--sem-texto", action="store_true", help="ignora TEXT/MTEXT")
    ap.add_argument("--converter", help="caminho do ODAFileConverter ou dwg2dxf")
    a = ap.parse_args(argv)
    try:
        dest, n, warns = convert(a.entrada, a.saida, a.crs, a.utm, a.tolerance, a.scale,
                                 tuple(a.offset), not a.sem_texto, a.converter)
    except ConversionError as e:
        print(f"Erro: {e}", file=sys.stderr)
        return 1
    for w in warns:
        print(f"Aviso: {w}", file=sys.stderr)
    print(f"{n} elementos gravados em {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
