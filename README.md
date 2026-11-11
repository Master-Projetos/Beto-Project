# Preguiça App (Material List)

Reads the material tables embedded in a Word document and fills the
`templates/lista_materiais.xlsx` template. Excel is not required. It also fills the TCR/TCE Word
documents from the Route Map report and creates KMZ maps from lists of coordinates.

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

### Word (TCR) or spreadsheet from the PDF

After picking a PDF, the "Gerar" page asks what to generate (the choice is remembered):

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
- **an Excel sheet** (`.xlsx`): pick the sheet and the columns of the name, latitude and longitude
  (preselected when the header row names them).

The points found (and the entries that could not be read, with the reason) are listed before the
file is generated. The marker is a shape (pin, circle, square, triangle, diamond, star) in the
color and size you choose, or a picture from the computer; it is stored inside the KMZ, so it shows
on any machine.

## Code layout

```
src/ListaMateriais/
  Program.cs, MainForm.cs   entry point; window with the navigation pane, file drop and shortcuts
  Pages/                    one file per page (Gerar, Mapa, Modelos, Cabeçalhos, Configurações, Sobre)
  Ui.cs, Dialogs.cs         shared look, layout helpers and the task dialogs
  *Form.cs                  dialogs of the generation flow (table selection, report data, placeholders)
  DocxReader.cs             material tables of a Word document
  PdfReader.cs              Route Map report; TcePdfParser.cs reads a TCE/TCR saved as PDF
  XlsxWriter.cs             fills the spreadsheet template
  TcrWriter.cs, Sheet.cs, WordRefresh.cs   fill the TCR/TCE Word template
  Kmz/                      point parsing (text, Python literal, xlsx), marker drawing and the KMZ writer
  Config.cs                 settings (%APPDATA%\ListaMateriais\settings.json) and templates
tests/ListaMateriais.Tests/ parsers and the KMZ writer; Samples/ has made-up TCE PDFs
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
