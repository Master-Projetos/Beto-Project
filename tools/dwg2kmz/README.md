# dwg2kmz — conversor DWG/DXF → KML/KMZ

```bash
pip install -r tools/dwg2kmz/requirements.txt
python tools/dwg2kmz/dwg2kmz.py projeto.dwg --crs EPSG:31983      # -> projeto.kmz
python tools/dwg2kmz/dwg2kmz.py projeto.dxf --utm 23S -o saida.kml
```

- **DWG**: precisa do [ODA File Converter](https://www.opendesign.com/guestfiles/oda_file_converter)
  (gratuito) ou do LibreDWG (`dwg2dxf`) no PATH — ou use `--converter caminho`. Sem eles, exporte
  o desenho como DXF e passe o `.dxf`.
- **Coordenadas**: informe o sistema do desenho com `--crs` (ex.: `EPSG:31983` SIRGAS 2000 / UTM 23S,
  `EPSG:31982` UTM 22S, `EPSG:31984` UTM 24S) ou `--utm 23S`. Desenhos já em lon/lat dispensam.
  Se o desenho estiver em mm/cm, use `--scale 0.001` / `0.01`.
- **Suporta**: LINE, LWPOLYLINE, POLYLINE, CIRCLE, ARC, ELLIPSE, SPLINE (curvas discretizadas,
  `--tolerance`), POINT, TEXT/MTEXT (rótulos) e blocos (INSERT, recursivo).
  Cada layer vira uma pasta; as cores do CAD são mantidas. Hachuras, cotas e 3D são ignorados (aviso).
- Testes: `python -m pytest tests/dwg2kmz`
