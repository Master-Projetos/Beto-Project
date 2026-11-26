import sys, zipfile
from pathlib import Path

import ezdxf
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools" / "dwg2kmz"))
import dwg2kmz  # noqa: E402


@pytest.fixture
def dxf(tmp_path):
    doc = ezdxf.new("R2010")
    doc.layers.add("REDE", color=1)
    msp = doc.modelspace()
    msp.add_line((500000, 7400000), (500100, 7400000), dxfattribs={"layer": "REDE"})
    msp.add_lwpolyline([(500000, 7400000), (500050, 7400050), (500100, 7400000)],
                       close=True, dxfattribs={"layer": "REDE"})
    msp.add_circle((500200, 7400200), 20)
    msp.add_arc((500300, 7400300), 30, 0, 90)
    msp.add_point((500400, 7400400))
    msp.add_text("Poste 1", dxfattribs={"insert": (500010, 7400010), "height": 2})
    blk = doc.blocks.new("POSTE")
    blk.add_circle((0, 0), 2)
    msp.add_blockref("POSTE", (500500, 7400500))
    path = tmp_path / "t.dxf"
    doc.saveas(path)
    return path


def test_kmz_has_expected_content(dxf, tmp_path):
    out = tmp_path / "t.kmz"
    dest, n, _ = dwg2kmz.convert(dxf, out, utm="23S")
    assert dest == out and n >= 7
    with zipfile.ZipFile(out) as z:
        kml = z.read("doc.kml").decode()
    assert "<name>REDE</name>" in kml and "Poste 1" in kml
    # UTM 23S, E=500000 -> meridiano central -45
    first = kml.split("<coordinates>")[1].split(",")[0]
    assert abs(float(first) + 45.0) < 1e-6
    assert "ff0000ff" in kml  # layer vermelho (ACI 1) em aabbggrr


def test_kml_output_and_crs_epsg(dxf, tmp_path):
    out = tmp_path / "t.kml"
    dwg2kmz.convert(dxf, out, crs="EPSG:31983")
    assert out.read_text().startswith("<?xml")


def test_requires_crs_for_projected_coords(dxf, tmp_path):
    with pytest.raises(dwg2kmz.ConversionError, match="--crs"):
        dwg2kmz.convert(dxf, tmp_path / "x.kmz")


def test_lonlat_drawing_needs_no_crs(tmp_path):
    doc = ezdxf.new()
    doc.modelspace().add_line((-46.6, -23.5), (-46.5, -23.4))
    p = tmp_path / "g.dxf"
    doc.saveas(p)
    out = tmp_path / "g.kml"
    dwg2kmz.convert(p, out)
    assert "-46.60000000,-23.50000000" in out.read_text()


def test_dwg_without_converter(tmp_path, monkeypatch):
    f = tmp_path / "a.dwg"
    f.write_bytes(b"AC1032")
    monkeypatch.setenv("PATH", str(tmp_path))
    monkeypatch.delenv("DWG_CONVERTER", raising=False)
    with pytest.raises(dwg2kmz.ConversionError, match="conversor"):
        dwg2kmz.convert(f, utm="23S")
