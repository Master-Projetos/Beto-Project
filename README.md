# Material List

Reads the material tables (MATERIAL, MATERIAIS, INSTALAÇÃO CLIENTE) embedded in a Word
document and fills the `templates/lista_materiais.xlsx` template. Excel is not required.

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
need .NET installed. Put the `templates/` folder next to it, or pick the template in the
"Modelo" tab.
