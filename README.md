# Preguiça App (Material List)

Reads the material tables embedded in a Word document and fills the
`templates/lista_materiais.xlsx` template. Excel is not required. It also fills the TCR/TCE Word
documents from the Route Map report, creates KMZ maps from lists of coordinates and turns AutoCAD
drawings (DWG) into KMZ.

Before generating, the app lists every material table in the document (name in the first
column, quantity in the third) with a preview of its items, and the user checks the ones to
use. Tables whose title (first cell) is registered start checked. The app ships with MATERIAL,
MATERIAIS, INSTALAÇÃO CLIENTE, FERRAGENS, LANÇAMENTO DE CABOS, CAIXAS / SPLITTERS and DATA
CENTER E POP - INDOOR (EQUIPAMENTOS PASSIVOS); the list can be edited in the "Cabeçalhos" page,
or extended right from the table list.

## PDF report (v2)

Besides the Word document, the app accepts the project report exported by Route Map as a PDF
(`Relatorio_Rede_..._AAAA-MM-DD.pdf`; pick it or drag it onto the window). It reads the
"LISTA DE MATERIAIS" section: every category (Ferragens, Cabos / Lançamento, Fusão, Data Center —
Passivo / Ativo) becomes a table, with name, unit, quantity and unit price. Categories registered
in the "Cabeçalhos" page (choose "Relatório do Route Map" there; it has its own list, with these five by
default) start checked; any other category found in the PDF is listed as "(nova)" and can be
checked on the spot. The city and the date come from the report header; the PDF has no project code, so that
cell is left to fill by hand. Each category's quantities × prices are checked against the
subtotal printed in the PDF, and any mismatch is shown as a warning in the result.

### Word (TCR) and spreadsheet from the PDF

After picking a PDF, the "Gerar" page asks what to generate. Any combination can be checked (the
choice is remembered): the PDF is read and the tables are picked once, then each file is generated
in turn (the Word documents first, each with its report data and where to save it, then the
spreadsheet). A file whose dialog is cancelled is skipped and the others still come out; the result
lists them all and "Abrir todos" opens them. From a TCE/TCR saved as PDF only the spreadsheet can
be made, so the Word choices are dropped with a note.

- **TCR (Word)**: fills the TCR template with the report: designer (projetista), date, study
  summary (ports, cost per port, city, launch, CAPEX, safety coefficient, deadline, labor), area
  study (neighborhood, HP, HC, penetration rate), objective, scope, investment text, area, number of
  poles and the cost chart. The tables are the template's own Excel objects: the app only
  writes quantities and prices into them (and the cost summary); nothing is rebuilt as a Word table.
  When Word is installed, each filled object is activated once so its picture shows the new values;
  otherwise they appear after opening the object. Items missing from a template table are listed as
  warnings. The labor objects stay as in the template, and the code on the cover and the map are not
  in the PDF, so they are left to fill by hand.
- **TCE (Word)**: same, using `templates/tce.docx` (summary, scope, investment text, poles, chart and
  the material objects, including customer installation).
- **Planilha de materiais (Excel)**: as described above.

The TCR/TCE templates are built into the executable when `templates/tcr.docx` / `templates/tce.docx` exist at build time (same
as `templates/lista_materiais.xlsx`); a file of the same name next to the executable takes precedence.
If there is none, the app asks for the blank document the first time and remembers it. The "Modelos"
page shows which template each one uses and lets you change it.

### A TCE/TCR saved as PDF

A PDF that is not the Route Map report is read as a TCE/TCR saved from Word (`TCE-LC-DVL-4310-00.pdf`):
its material tables (title and column names on the first row, then name, unit, quantity, unit price
and total, closed by a TOTAL line) are found by where the words sit on the page, so names that wrap
inside a cell are put back together. Only the spreadsheet can be generated from it; when TCR/TCE is
selected the app says so and offers the spreadsheet instead. The tables use the header list of the
Word documents, and the project code, date and city come from the cover and the "Cidade:" row.
The PDF must have selectable text; for a scanned one, use the `.docx`.

## Map points (KMZ)

The "Mapa (KMZ)" page turns coordinates into a `.kmz` with one placemark per point, for Google Earth
and Google Maps (My Maps). The points come from:

- **typed or pasted text**, one point per line: `Poste 12, -23.5505, -46.6333`. The name is optional
  and may come last; `;`, tabs or spaces also separate, decimal commas (`-23,5505, -46,6333`) and
  degrees/minutes/seconds (`23°33'01.8"S`) are understood;
- **a Python dict or list**, pasted as is: `{"Poste 12": (-23.5505, -46.6333)}`,
  `{"A": {"lat": -23.5, "lon": -46.6}}`, `[{"nome": "A", "lat": -23.5, "lon": -46.6}]`,
  `[("A", -23.5, -46.6)]`, or column-wise `{"nome": [...], "lat": [...], "lon": [...]}`. It is parsed
  as a literal, never executed;
- **an Excel sheet** (`.xlsx`) with coordinates, addresses, or both. The columns are predicted from
  their titles *and* their content, and shown for checking: name, latitude, longitude, the street
  (or the whole address in one cell), number, street type, district, city, state and CEP. A title
  alone is not trusted: a "TIPO" column holding TORRE/POP is not the street type, "Nº CONTRATO" is
  not the house number, a three-letter "SIGLA" is not the state, and the nickname ("APELIDO") is
  preferred to a designation code for the marker name.

  An address in one cell is split the way people type it: "RUA X, Nº 138", "R X N48", "AV X 790",
  "…, apto 3", "… - Centro", "ESTRADA X, S/N", "ROD BR381 KM 100,5", a CEP anywhere, or a street
  without its type. "CIDADE / UF" is split too. When the sheet has no city (or a row leaves it
  empty), the city and UF typed on the page are used.

  Rows with a valid latitude and longitude keep them; the others are looked up by address with
  "Localizar endereços", in OpenStreetMap's free search (Nominatim): one request a second, one at a
  time, a generic client name, and only street, number, district, city and state are sent. Each
  address is asked once (same address typed differently included) and the places found are kept in
  `%APPDATA%\ListaMateriais\enderecos.json`, so a second run is instant. A run can be stopped at any
  time; above 500 addresses the app asks first. Each point says in the KMZ how it was found: on the
  number, only on the street (most Brazilian streets have no house numbers on the map), or only in
  the district. When only the city is found the row is left out and listed, instead of piling up on
  the city center; addresses not found are listed too.

The points found (and the entries that could not be read, with the reason) are listed before the
file is generated. The marker is a shape (pin, circle, square, triangle, diamond, star) in the
color and size you choose, or a picture from the computer; it is stored inside the KMZ, so it shows
on any machine.

## Simplifying a map (KMZ)

The "Simplificar KMZ" page takes a busy technical `.kmz`/`.kml` (thousands of points, cables drawn
in short pieces, dozens of colors, descriptions and data tables) and writes a map anyone can read:

- **Só os cabos** (the default): every line of the map becomes a cable, all in one layer of one color
  (neon green #39FF14 unless another is picked; amarelo, ciano, rosa and laranja neon and branco are there too) with a dark edge drawn under each line so it stands out on any ground, and thick ("Grossos" 6 px, "Bem grossos" 10 px, "Grossíssimos" 14 px);
  points (CTO, CEO, splice boxes, poles) and areas are left out. Lines that are not cables can be unchecked;
- **Tudo**: every group kept, as below.

In "Tudo":

- the drawings are grouped by folder and color; each group becomes one legend entry, named after
  its folder (tidied up: `CABOS_AS` → "Cabos as") or after its color ("Linhas vermelhas"). The names
  can be changed in the list (F2); groups given the same name become one; unchecked groups are left out;
- each entry gets one strong color (red stays red when it is free), lines are drawn thick, points
  as big round markers without labels, and a legend is drawn in the corner of Google Earth;
- line pieces whose ends meet are joined (branches stay apart) and straightened, nearby points become
  one marker ("Postes (4)"), and very short lines are dropped. "Quanto simplificar" sets how far
  this goes (Pouco: 2 m, nothing joined; Médio: 8 m, points within 40 m; Bastante: 25 m, 150 m);
- descriptions, data tables, photos, overlays and links to other files are not carried over.

## Checking a map (KMZ)

"Conferir KMZ" reviews a network map before it goes out and lists what looks wrong, red for errors
and yellow for warnings:

- **Desenhado duas vezes** (error): the same cable on top of itself, or the same box twice in one spot.
  Two differently named cables on one trace are only a warning (**Mesmo traçado**); a CEO and a CTO on
  the same pole are fine.
- **Sem nome**: left with Google Earth's default name ("Caminho sem título", "Untitled Path"...).
- **Nome repetido**: two separate drawings with one name (pieces of one cable drawn end to end may share it).
- **Ponta solta**: a cable end with no box, point or other cable within the join distance (10 m by default).
- **Ponto sem cabo**: a box no cable passes near.
- **Cabo muito curto**: under 3 m, a leftover of drawing.
- **Coordenada errada** (error) and **Fora do Brasil**: zeroed coordinates, or latitude and longitude swapped.
- **Vão longo**, off by default: a stretch between two vertices longer than the limit (80 m). Vertices
  are not always poles, so this one is noisy on route traces.

Folders that are on the map only for reference (backbone, existing network elsewhere) can be unchecked:
the project's cables may still connect to them, but their own issues are left out. A double click copies
the coordinates of an issue, "Copiar lista" copies them all for Excel, and "Salvar KMZ com os problemas
marcados" writes one marker per issue, to open in Google Earth over the original map. Port counts,
cable types and declared lengths are not checked: the maps do not carry them in a form that can be read.

## DWG drawings to KMZ

The "DWG para KMZ" page places an AutoCAD drawing on the map for Google Earth. Pick or drag a `.dwg` (or a
text `.dxf`). The file is read by the app itself, with no AutoCAD, no converter and no internet.

- **Versions:** DWG from AutoCAD 2000 to 2018 and later (R2000, R2004, R2010, R2013, R2018 formats). AutoCAD 2007 to
  2009 files (R2007) are not read yet: the app says so and asks to save the drawing as 2013 (or 2000) or as DXF.
- **What is drawn:** lines, polylines (with arcs), arcs, circles, ellipses, splines, points, texts and block
  references, from the model space. Every layer becomes a folder of the KMZ and keeps its colors. Hatches,
  dimensions, 3D solids and images are left out, and the page says how many.
- **Where it goes on the map:** the drawing has no position of its own, so the page asks for its coordinate system:
  UTM (zone and hemisphere, remembered; zone 23 South by default) or "already in degrees". The unit (meters,
  centimeters, millimeters) can be changed. SIRGAS 2000 and WGS 84 are treated as the same. Before generating, the
  page shows where the drawing lands (latitude/longitude of its center and its size in km), in red when it cannot be
  right, so a wrong zone is seen right away.
- **Layers:** listed with the number of elements; layers that are off or frozen in the drawing start unchecked.
- **Blocks:** each block reference becomes a point named after the block; "Desenhar também o conteúdo dos blocos"
  draws the block's own lines too.
- A damaged file never crashes the app: the objects that cannot be read are skipped and counted.

## Presentations (PowerPoint)

The "Apresentação" page turns a spreadsheet (`.xlsx` or `.csv`) into a `.pptx` with no other input:

- **Reading:**
  - the header row is found even below a title;
  - sheets with the same columns (one per city, one per seller) are joined, the sheet becoming a column;
  - total rows are left out;
  - values are read as people write them (`R$ 1.234,56`, `12%`, Excel date numbers, `31/12/2026`, `mar/26`).
- **Columns:** the app works out the role of each column.
  - **Main number:** chosen from the money, amount or count columns.
  - **Part:** a part of that number gives a rate, e.g. "Portas ocupadas" of "Quantidade portas" gives the occupancy.
  - **Groupings:** city, seller, product, type, plus the date and the status.
  - **Never summed or grouped:** codes, documents, phones, coordinates and contract numbers.
  - The page says in one sentence how the table was read.
- **Slides** (from 4 to 8), each closing on a one-line conclusion:
  - **Cover** and **Visão geral**: big numbers and highlights.
  - **Evolução mensal**: the last full month against the one before. A month still running is marked and left out of the comparisons.
  - **Ranking**: for the two best groupings, with the leader's share, how many times the average it is, and the share of the top 3. When there is a status column, the pending part shows in orange.
  - **Taxa** (occupancy): extremes taken only among groups that are not tiny. Groups above 85% are flagged as close to full.
  - **Situação**: done, in progress, and problems.
  - **Principais conclusões**.
- **Description (optional):** a sentence such as "Vendas de Lavras no 1º trimestre de 2026" or "último trimestre".
  - It filters by period ("último…" counts back from the latest date in the data) and by values found in the sheet (a city, a seller).
  - It also becomes the title.
  - When it filters everything out, the app says so instead of making an empty deck.
- **Drawing:**
  - The charts are drawn with shapes, in the look of the example deck, so they can be retouched by hand.
  - Text is measured and shrunk or cut to fit its box.
  - Only what the data shows is stated; causes are never made up.
- **Look:**
  - The client's look comes from `templates/apresentacao.pptx`: its master, theme, layout and pictures are kept, and its slides, thumbnail and properties are dropped.
  - Like the other templates, it is built in, or kept from an earlier copy, or read from an `apresentacao.pptx` next to the executable.
  - Without it, a plain look is used.
  - `*.pptx` files are not versioned, because the example carries client branding and numbers.
- **Privacy:** nothing leaves the computer.

## Spreadsheets (paint and filter)

The "Planilha" page paints cells and filters rows of an `.xlsx`, on every sheet or only on the ones
checked, and saves the result as a new file (the original is never changed).

- **Painting:** one or more rules, each with its columns (or the whole row), a fill color (or
  removing the fill) and optional conditions. The header can be painted too. When two rules hit the
  same cell, the later one wins.
- **Filtering:** conditions joined by E / OU (the same operators as the map page; numbers and
  `dd/mm/yyyy` dates compare as such). Rows that do not meet them are hidden, never deleted, and
  Excel's filter buttons are added to the header when the sheet has no filter or table yet. The
  buttons do not hold the conditions: using them in Excel (or clearing the filter) shows the hidden
  rows again.
- **Several values at once:** "Marcar vários..." beside a condition's value lists the values of the
  column, with a search box; checking more than one turns the condition into "É um destes". Several
  painting rules can be selected and removed together.
- **Columns** are matched by header name (ignoring case and accents), so they may sit in different
  places on each sheet. The header row is found on its own or set by hand.
- **Warnings instead of failures:** a missing column, an empty or unreadable sheet, or a filter that
  matches nothing is listed as a warning and the rest is still done. A condition on a column a sheet
  does not have is not applied there. When nothing would change, no file is written.

## Updates

On start, the published executable asks GitHub for the latest release of this repository and, when
it is newer, offers it in a strip at the top of the window. "Atualizar agora" downloads it next to
the executable, checks its SHA-256, renames the running file to `.old`, puts the new one in its
place and restarts (the `.old` file is deleted on the next start). The "Sobre" page checks on demand
and turns the automatic check off. Copies run with `dotnet run` or from Visual Studio never replace
themselves.

To publish a version, push a tag: `git tag v2.2.0 && git push origin v2.2.0`. The `Release`
workflow runs the tests, builds the executable with `-p:EmbedTemplates=false` (the repository is
public and the client's templates are not in it) and attaches `ListaMateriais.exe` and its
`.sha256` to the release. The templates of the copy handed out before keep working: on every start
the app copies the templates built into it to `%APPDATA%\ListaMateriais\`, and the versions without
them use those copies. A machine that never had a copy with templates asks for them in "Modelos".

## Code layout

```
src/ListaMateriais/
  Program.cs, MainForm.cs   entry point; window with the navigation pane, file drop and shortcuts
  Pages/                    one file per page (Gerar, Mapa, DWG, Simplificar, Conferir, Apresentação, Planilha, Modelos, Cabeçalhos, Configurações, Sobre)
  Planilhas/                paint cells and filter rows of an .xlsx
  Ui.cs, Dialogs.cs         shared look, layout helpers and the task dialogs
  *Form.cs                  dialogs of the generation flow (table selection, report data, placeholders)
  DocxReader.cs             material tables of a Word document
  PdfReader.cs              Route Map report; TcePdfParser.cs reads a TCE/TCR saved as PDF
  XlsxWriter.cs             fills the spreadsheet template
  TcrWriter.cs, Sheet.cs, WordRefresh.cs   fill the TCR/TCE Word template
  Kmz/                      point parsing (text, Python literal, xlsx), column prediction (SheetColumns), addresses
                            (AddressText) and their lookup (Geocoder), marker drawing and the KMZ writer;
                            KmlReader, MapSimplifier and SimpleKmzWriter for the simplified map;
                            MapChecker and IssueKmzWriter for the map check
  Cad/                      DWG reader (DwgFile: pages and compression; DwgReader: objects; BitReader), DXF reader,
                            curves to lines (CadGeometry), UTM to latitude/longitude, and the KMZ writer for drawings
  Updater.cs, UpdateBanner.cs   updates from the GitHub releases
  Config.cs                 settings (%APPDATA%\ListaMateriais\settings.json) and templates
tests/ListaMateriais.Tests/ parsers and the KMZ writer; Samples/ has made-up TCE PDFs. The DWG tests read real
                            files of every version when DWG_SAMPLES points to a folder with example_2000.dwg ... example_2018.dwg
                            (the LibreDWG test files; not kept here), and pass without checking anything otherwise
```

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), only to build from source

## Usage

```sh
dotnet run --project src/ListaMateriais                         # run
dotnet build -c Release                                         # build
dotnet test                                                     # run the tests
dotnet publish src/ListaMateriais -p:PublishProfile=win-x64     # creates dist/ListaMateriais.exe
```

The published executable is a single, self-contained file, so the target machine does not
need .NET installed. `templates/lista_materiais.xlsx` is built into it as the default template
(publishing fails if that file is missing), so the executable can be sent on its own. On first
use it is unpacked to `%APPDATA%\ListaMateriais\modelo-padrao.xlsx`, where it can be edited.
A `templates/` folder next to the executable, or a template picked in the "Modelos" page, takes
precedence; "Usar modelo padrão" switches back.

The "Configurações" page picks the spreadsheet type: **simplified** (only the materials in the
document, one list with its total) or **complete** (the whole template list, keeping its sections
and a total per section; materials missing from the document get quantity 0). Both center every
cell of the list except the item descriptions, which stay left-aligned.

When a material appears more than once in the checked tables (also under an alias or in
different case), the quantities are added up; the table list has an option to keep only the
first one instead.
