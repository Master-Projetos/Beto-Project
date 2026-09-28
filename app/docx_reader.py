import io
import logging
import zipfile
from typing import NamedTuple

from openpyxl import load_workbook

from app.config import MATERIAL_TABLE_TITLES, normalize


class Material(NamedTuple):
    quantity: float
    unit: str
    unit_price: float


def read_materials(docx_path):
    materials = {}
    with zipfile.ZipFile(docx_path) as docx_zip:
        embedded_xlsx_names = sorted(
            n
            for n in docx_zip.namelist()
            if n.startswith('word/embeddings/') and n.endswith('.xlsx')
        )
        for xlsx_name in embedded_xlsx_names:
            workbook = load_workbook(
                io.BytesIO(docx_zip.read(xlsx_name)), data_only=True
            )
            for sheet in workbook.worksheets:
                rows = [
                    row
                    for row in sheet.iter_rows(values_only=True)
                    if any(value is not None for value in row)
                ]
                title = rows[0][0] if rows else None
                if (
                    not isinstance(title, str)
                    or normalize(title) not in MATERIAL_TABLE_TITLES
                ):
                    continue
                for row in rows[1:]:
                    name, unit, quantity, unit_price, total = (row + (None,) * 5)[:5]
                    if name is None or not isinstance(quantity, (int, float)):
                        continue
                    unit_price = (
                        unit_price if isinstance(unit_price, (int, float)) else 0
                    )
                    materials[str(name).strip()] = Material(
                        quantity, unit or '', round(unit_price, 2)
                    )
                    logging.info(
                        '%s | unidade: %s | quantidade: %s'
                        ' | valor unitário: %s | valor total: %s',
                        name,
                        unit,
                        quantity,
                        unit_price,
                        total,
                    )
    return materials
