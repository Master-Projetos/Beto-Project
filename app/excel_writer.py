import os

import xlwings as xw

from app.config import (
    FIRST_ITEM_ROW,
    SECTION_TITLE_CELL,
    SHEET_NAME,
    TEMPLATE,
    grand_total,
    lookup_name,
    normalize,
)


def contiguous_blocks(descending_rows):
    blocks = []
    for row in descending_rows:
        if blocks and blocks[-1][1] == row + 1:
            blocks[-1][1] = row
        else:
            blocks.append([row, row])
    return blocks


def fill(materials, output, template=TEMPLATE, app=None):
    own_app = app is None
    if own_app:
        app = xw.App(visible=False, add_book=False)
    app.screen_updating = False
    try:
        book = app.books.open(os.path.abspath(template))
        app.calculation = 'manual'
        sheet = book.sheets[SHEET_NAME]
        last_row = sheet.used_range.last_cell.row

        descriptions = sheet.range(f'B1:B{last_row}').value
        row_by_description = {
            normalize(d): i + 1
            for i, d in enumerate(descriptions)
            if isinstance(d, str) and i + 1 >= FIRST_ITEM_ROW
        }

        for i, (formula,) in enumerate(sheet.range(f'H1:H{last_row}').formula):
            if '$I$9' in formula:
                sheet.range(f'H{i + 1}').formula = formula.replace('$I$9', f'D{i + 1}')

        for row in row_by_description.values():
            if not sheet.range(f'H{row}').formula:
                sheet.range(f'H{row}').formula = f'=G{row}*D{row}'

        last_total = max(
            i + 1
            for i, value in enumerate(sheet.range(f'G1:G{last_row}').value)
            if isinstance(value, str) and normalize(value) == 'TOTAL'
        )

        not_found = {}
        filled_rows = set()
        for material_name, material in materials.items():
            row_number = row_by_description.get(lookup_name(material_name))
            if row_number is None:
                not_found[material_name] = material
                continue
            filled_rows.add(row_number)
            sheet.range(f'D{row_number}').value = material.quantity

        if not_found:
            first_new = last_total
            last_new = first_new + len(not_found) - 1
            sheet.range(f'{first_new}:{last_new}').insert(
                shift='down', copy_origin='format_from_left_or_above'
            )
            sheet.range(f'A{first_new}').value = [
                [
                    None,
                    name,
                    material.unit,
                    material.quantity,
                    None,
                    None,
                    material.unit_price,
                    f'=G{row}*D{row}',
                ]
                for row, (name, material) in enumerate(
                    not_found.items(), start=first_new
                )
            ]
            filled_rows.update(range(first_new, last_new + 1))
            last_total += len(not_found)

        unused_rows = sorted(set(range(FIRST_ITEM_ROW, last_total)) - filled_rows)
        for block_end, block_start in contiguous_blocks(reversed(unused_rows)):
            sheet.range(f'{block_start}:{block_end}').delete(shift='up')
        sheet.range(SECTION_TITLE_CELL).value = None

        for cell_ref, formula in grand_total(FIRST_ITEM_ROW + len(filled_rows)).items():
            sheet.range(cell_ref).formula = f'={formula}'

        app.calculation = 'automatic'
        book.save(os.path.abspath(output))
        book.close()
    finally:
        if own_app:
            app.quit()
    return list(not_found)
