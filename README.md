# Material List

Reads the material tables embedded in a Word document and fills the
`templates/lista_materiais.xlsx` template. Excel is not required.

Before generating, the app lists every material table in the document (name in the first
column, quantity in the third) with a preview of its items, and the user checks the ones to
use. Tables whose title (first cell) is registered start checked. The app ships with MATERIAL,
MATERIAIS, INSTALAÇÃO CLIENTE, FERRAGENS, LANÇAMENTO DE CABOS, CAIXAS / SPLITTERS and DATA
CENTER E POP - INDOOR (EQUIPAMENTOS PASSIVOS); the list can be edited in the "Cabeçalhos" tab,
or extended right from the table list.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), only to build from source

## Usage

```sh
dotnet run --project src/ListaMateriais                         # run
dotnet build -c Release                                         # build
dotnet publish src/ListaMateriais -p:PublishProfile=win-x64     # creates dist/ListaMateriais.exe
```

The published executable is a single, self-contained file, so the target machine does not
need .NET installed. `templates/lista_materiais.xlsx` is built into it as the default template
(publishing fails if that file is missing), so the executable can be sent on its own. On first
use it is unpacked to `%APPDATA%\ListaMateriais\modelo-padrao.xlsx`, where it can be edited.
A `templates/` folder next to the executable, or a template picked in the "Modelo" tab, takes
precedence; "Usar modelo padrão" switches back.

The "Configurações" tab picks the spreadsheet type: **simplified** (only the materials in the
document, one list with its total) or **complete** (the whole template list, keeping its sections
and a total per section; materials missing from the document get quantity 0). Both center every
cell of the list except the item descriptions, which stay left-aligned.

When a material appears more than once in the checked tables (also under an alias or in
different case), the quantities are added up; the table list has an option to keep only the
first one instead.
