import json
import os
import sys
from pathlib import Path

if getattr(sys, 'frozen', False):
    ROOT = Path(sys.executable).resolve().parent
else:
    ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = ROOT / 'templates' / 'lista_materiais.xlsx'
SETTINGS_FILE = (
    Path(os.getenv('APPDATA', Path.home())) / 'ListaMateriais' / 'settings.json'
)
SHEET_NAME = 'MATERIAIS'


def load_template_path():
    try:
        saved = Path(json.loads(SETTINGS_FILE.read_text(encoding='utf-8'))['template'])
    except (OSError, ValueError, KeyError):
        saved = None
    for candidate in (saved, TEMPLATE):
        if candidate and candidate.exists():
            return candidate
    return None


def save_template_path(path):
    SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    SETTINGS_FILE.write_text(json.dumps({'template': str(path)}), encoding='utf-8')


MATERIAL_TABLE_TITLES = {'MATERIAL', 'MATERIAIS', 'INSTALAÇÃO CLIENTE'}

FIRST_ITEM_ROW = 10
GRAND_TOTAL_CELL = 'G8'
SECTION_TITLE_CELL = 'A9'

NAME_ALIASES = {
    'CONECTOR RJ45': 'CONECTORES RJ45',
    'DROP': 'DROP FLAT 1FO',
}


def normalize(description):
    return description.strip().upper()


def grand_total(total_row):
    total = (
        f'SUM(H{FIRST_ITEM_ROW}:H{total_row - 1})'
        if total_row > FIRST_ITEM_ROW
        else '0'
    )
    return {f'H{total_row}': total, GRAND_TOTAL_CELL: f'H{total_row}'}


def lookup_name(material_name):
    return normalize(NAME_ALIASES.get(material_name, material_name))
