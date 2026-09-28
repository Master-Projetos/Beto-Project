import html
import re
import zipfile
from bisect import bisect_left

from app.config import (
    FIRST_ITEM_ROW,
    SECTION_TITLE_CELL,
    SHEET_NAME,
    TEMPLATE,
    grand_total,
    lookup_name,
    normalize,
)


def sheet_path(xlsx_zip, sheet_name):
    workbook_xml = xlsx_zip.read('xl/workbook.xml').decode('utf-8')
    rels_xml = xlsx_zip.read('xl/_rels/workbook.xml.rels').decode('utf-8')
    rel_id = re.search(
        rf'<sheet [^>]*name="{re.escape(sheet_name)}"[^>]*r:id="([^"]+)"', workbook_xml
    ).group(1)
    target = re.search(
        rf'<Relationship [^>]*Id="{rel_id}"[^>]*Target="([^"]+)"', rels_xml
    )
    if target is None:
        target = re.search(
            rf'<Relationship [^>]*Target="([^"]+)"[^>]*Id="{rel_id}"', rels_xml
        )
    return 'xl/' + target.group(1).lstrip('/').removeprefix('xl/')


def shared_strings(xlsx_zip):
    xml = xlsx_zip.read('xl/sharedStrings.xml').decode('utf-8')
    items = re.findall(r'<si>(.*?)</si>', xml, re.S)
    return [
        html.unescape(''.join(re.findall(r'<t[^>]*>(.*?)</t>', item, re.S)))
        for item in items
    ]


def texts_by_row(sheet_xml, strings, column):
    rows = {}
    for row_number, attrs, value in re.findall(
        rf'<c r="{column}(\d+)"([^>]*)><v>([^<]*)</v>', sheet_xml
    ):
        text = strings[int(value)] if 't="s"' in attrs else value
        rows[int(row_number)] = normalize(text)
    return rows


def find_cell(sheet_xml, cell_ref):
    return re.search(rf'<c r="{cell_ref}"([^>]*?)(/>|>.*?</c>)', sheet_xml, re.S)


def column_index(column):
    index = 0
    for letter in column:
        index = index * 26 + ord(letter) - 64
    return index


def insert_cell(sheet_xml, cell_ref, cell_xml):
    column, row = re.match(r'([A-Z]+)(\d+)', cell_ref).groups()
    row_match = re.search(rf'<row r="{row}"[^>]*?(/>|>.*?</row>)', sheet_xml, re.S)
    row_xml = row_match.group(0)
    if row_xml.endswith('/>'):
        new_row = row_xml[:-2] + f'>{cell_xml}</row>'
    else:
        position = row_xml.rindex('</row>')
        for cell in re.finditer(r'<c r="([A-Z]+)\d+"', row_xml):
            if column_index(cell.group(1)) > column_index(column):
                position = cell.start()
                break
        new_row = row_xml[:position] + cell_xml + row_xml[position:]
    return sheet_xml.replace(row_xml, new_row, 1)


def replace_cell(sheet_xml, cell_ref, content):
    cell = find_cell(sheet_xml, cell_ref)
    if cell is None:
        return insert_cell(sheet_xml, cell_ref, f'<c r="{cell_ref}"{content}</c>')
    attrs = re.sub(r'\s+t="[^"]*"', '', cell.group(1))
    return sheet_xml.replace(cell.group(0), f'<c r="{cell_ref}"{attrs}{content}</c>', 1)


def set_number(sheet_xml, cell_ref, number):
    return replace_cell(sheet_xml, cell_ref, f'><v>{number}</v>')


def set_formula(sheet_xml, cell_ref, formula):
    return replace_cell(sheet_xml, cell_ref, f'><f>{formula}</f>')


def fix_total_formulas(sheet_xml):
    def replace(match):
        return match.group(0).replace('$I$9', f'D{match.group(1)}')

    return re.sub(
        r'<c r="H(\d+)"[^>]*><f[^>]*>[^<]*\$I\$9[^<]*</f>', replace, sheet_xml
    )


def remap_rows(sheet_xml, new_row):
    def remap_refs(text):
        return re.sub(
            r'(\$?[A-Z]{1,3}\$?)(\d+)',
            lambda m: f'{m.group(1)}{new_row(int(m.group(2)))}',
            text,
        )

    sheet_xml = re.sub(
        r'<row r="(\d+)"', lambda m: f'<row r="{new_row(int(m.group(1)))}"', sheet_xml
    )
    sheet_xml = re.sub(
        r'( (?:r|ref|sqref|activeCell|topLeftCell)=")([^"]+)"',
        lambda m: f'{m.group(1)}{remap_refs(m.group(2))}"',
        sheet_xml,
    )
    return re.sub(
        r'(<f[^>]*>)([^<]*)(</f>)',
        lambda m: m.group(1) + remap_refs(m.group(2)) + m.group(3),
        sheet_xml,
    )


def shift_rows(sheet_xml, first_row, count):
    return remap_rows(sheet_xml, lambda row: row + count if row >= first_row else row)


def unshare_total_formulas(sheet_xml):
    sheet_xml = re.sub(r'<f t="shared"[^>]*>([^<]+)</f>', r'<f>\1</f>', sheet_xml)
    return re.sub(
        r'<c r="H(\d+)"([^>]*)><f t="shared"[^>]*/>',
        lambda m: (
            f'<c r="H{m.group(1)}"{m.group(2)}><f>G{m.group(1)}*D{m.group(1)}</f>'
        ),
        sheet_xml,
    )


def delete_rows(sheet_xml, rows):
    deleted = sorted(rows)

    def keep_merge(match):
        first, last = (int(n) for n in re.findall(r'\d+', match.group(1)))
        return (
            '' if all(row in rows for row in range(first, last + 1)) else match.group(0)
        )

    sheet_xml = re.sub(
        r'<row r="(\d+)"[^>]*?(?:/>|>.*?</row>)',
        lambda m: '' if int(m.group(1)) in rows else m.group(0),
        sheet_xml,
        flags=re.S,
    )
    sheet_xml = re.sub(r'<mergeCell ref="([^"]+)"/>', keep_merge, sheet_xml)
    sheet_xml = re.sub(
        r'<mergeCells count="\d+"',
        f'<mergeCells count="{sheet_xml.count("<mergeCell ref=")}"',
        sheet_xml,
    )
    return remap_rows(sheet_xml, lambda row: row - bisect_left(deleted, row))


def new_row_xml(template_row_xml, template_row, row_number, name, material):
    row_xml = template_row_xml.replace(
        f'<row r="{template_row}"', f'<row r="{row_number}"', 1
    )
    styles = dict(re.findall(rf'<c r="([A-Z]+){template_row}"( s="\d+")?', row_xml))
    values = {
        'B': f' t="inlineStr"><is><t>{html.escape(name)}</t></is>',
        'C': f' t="inlineStr"><is><t>{html.escape(material.unit)}</t></is>',
        'D': f'><v>{material.quantity}</v>',
        'G': f'><v>{material.unit_price}</v>',
        'H': f'><f>G{row_number}*D{row_number}</f>',
    }
    cells = ''.join(
        f'<c r="{column}{row_number}"{style}{values[column]}</c>'
        if column in values
        else f'<c r="{column}{row_number}"{style}/>'
        for column, style in styles.items()
    )
    return re.sub(r'>.*</row>', f'>{cells}</row>', row_xml, count=1, flags=re.S)


def insert_rows(sheet_xml, after_row, new_materials):
    template_row_xml = re.search(
        rf'<row r="{after_row}".*?</row>', sheet_xml, re.S
    ).group(0)
    sheet_xml = shift_rows(sheet_xml, after_row + 1, len(new_materials))
    new_rows = ''.join(
        new_row_xml(template_row_xml, after_row, after_row + 1 + i, name, material)
        for i, (name, material) in enumerate(new_materials.items())
    )
    anchor = re.search(rf'<row r="{after_row}".*?</row>', sheet_xml, re.S)
    return sheet_xml[: anchor.end()] + new_rows + sheet_xml[anchor.end() :]


def drop_cached_formula_values(sheet_xml):
    return re.sub(r'(</f>)<v>[^<]*</v>', r'\1', sheet_xml)


def force_recalculation(workbook_xml):
    if '<calcPr' in workbook_xml:
        workbook_xml = re.sub(r'\s+fullCalcOnLoad="[^"]*"', '', workbook_xml)
        return workbook_xml.replace('<calcPr', '<calcPr fullCalcOnLoad="1"', 1)
    return workbook_xml.replace(
        '</workbook>', '<calcPr fullCalcOnLoad="1"/></workbook>'
    )


def fill(materials, output, template=TEMPLATE):
    with zipfile.ZipFile(template) as template_zip:
        target_sheet = sheet_path(template_zip, SHEET_NAME)
        sheet_xml = template_zip.read(target_sheet).decode('utf-8')
        strings = shared_strings(template_zip)
        row_by_description = {
            text: row
            for row, text in texts_by_row(sheet_xml, strings, 'B').items()
            if row >= FIRST_ITEM_ROW
        }
        total_rows = sorted(
            row
            for row, text in texts_by_row(sheet_xml, strings, 'G').items()
            if text == 'TOTAL'
        )

        last_total = total_rows[-1]

        not_found = {}
        filled_rows = set()
        for material_name, material in materials.items():
            row_number = row_by_description.get(lookup_name(material_name))
            if row_number is None:
                not_found[material_name] = material
                continue
            filled_rows.add(row_number)
            sheet_xml = set_number(sheet_xml, f'D{row_number}', material.quantity)

        sheet_xml = unshare_total_formulas(fix_total_formulas(sheet_xml))
        for row in filled_rows:
            cell = find_cell(sheet_xml, f'H{row}')
            if cell is None or '<f' not in cell.group(2):
                sheet_xml = set_formula(sheet_xml, f'H{row}', f'G{row}*D{row}')

        if not_found:
            sheet_xml = insert_rows(sheet_xml, last_total - 1, not_found)
            filled_rows.update(range(last_total, last_total + len(not_found)))
            last_total += len(not_found)

        sheet_xml = delete_rows(
            sheet_xml, set(range(FIRST_ITEM_ROW, last_total)) - filled_rows
        )
        sheet_xml = replace_cell(sheet_xml, SECTION_TITLE_CELL, '>')
        for cell_ref, formula in grand_total(FIRST_ITEM_ROW + len(filled_rows)).items():
            sheet_xml = set_formula(sheet_xml, cell_ref, formula)

        write_package(
            template_zip, output, target_sheet, drop_cached_formula_values(sheet_xml)
        )
    return list(not_found)


def write_package(template_zip, output, target_sheet, sheet_xml):
    def read(name):
        return template_zip.read(name).decode('utf-8')

    replaced_parts = {
        target_sheet: sheet_xml,
        'xl/workbook.xml': force_recalculation(read('xl/workbook.xml')),
        '[Content_Types].xml': re.sub(
            r'<Override[^>]*calcChain[^>]*/>', '', read('[Content_Types].xml')
        ),
        'xl/_rels/workbook.xml.rels': re.sub(
            r'<Relationship[^>]*calcChain[^>]*/>',
            '',
            read('xl/_rels/workbook.xml.rels'),
        ),
    }

    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as output_zip:
        for item in template_zip.infolist():
            if item.filename == 'xl/calcChain.xml':
                continue
            if item.filename in replaced_parts:
                output_zip.writestr(item, replaced_parts[item.filename].encode('utf-8'))
            else:
                output_zip.writestr(item, template_zip.read(item))
