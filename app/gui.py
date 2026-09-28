import logging
import os
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from app.config import load_template_path, save_template_path
from app.docx_reader import read_materials
from app.xlsx_writer import fill

WIDTH = 420
HEIGHT = 540
PADDING = 16


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title('Lista de Materiais')
        self.geometry(f'{WIDTH}x{HEIGHT}')
        self.minsize(WIDTH, HEIGHT)

        self.docx_path = None
        self.template_path = None
        self.docx_name = tk.StringVar(value='Nenhum documento selecionado')
        self.template_name = tk.StringVar()
        self.template_full = tk.StringVar()
        self.set_template(load_template_path(), save=False)

        style = ttk.Style(self)
        style.configure('Title.TLabel', font=('Segoe UI', 14, 'bold'))
        style.configure('Muted.TLabel', foreground='#666666')
        style.configure('Big.TButton', font=('Segoe UI', 11, 'bold'), padding=10)

        notebook = ttk.Notebook(self)
        notebook.pack(fill='both', expand=True, padx=8, pady=8)
        notebook.add(self.build_generate_tab(notebook), text='Gerar planilha')
        notebook.add(self.build_template_tab(notebook), text='Modelo')

    def build_generate_tab(self, parent):
        tab = ttk.Frame(parent, padding=PADDING)

        ttk.Label(tab, text='Gerar lista de materiais', style='Title.TLabel').pack(
            anchor='w'
        )
        ttk.Label(
            tab,
            text='Selecione o documento Word com a tabela de materiais.',
            style='Muted.TLabel',
            wraplength=WIDTH - 60,
        ).pack(anchor='w', pady=(2, 16))

        ttk.Button(tab, text='Selecionar documento...', command=self.choose_docx).pack(
            fill='x'
        )
        ttk.Label(
            tab,
            textvariable=self.docx_name,
            style='Muted.TLabel',
            wraplength=WIDTH - 60,
        ).pack(anchor='w', pady=(6, 16))

        self.generate_button = ttk.Button(
            tab,
            text='Gerar planilha',
            style='Big.TButton',
            command=self.generate,
            state='disabled',
        )
        self.generate_button.pack(fill='x')

        ttk.Label(tab, text='Resultado').pack(anchor='w', pady=(20, 4))
        result_frame = ttk.Frame(tab)
        result_frame.pack(fill='both', expand=True)
        self.result = tk.Text(
            result_frame,
            height=10,
            wrap='word',
            state='disabled',
            relief='solid',
            borderwidth=1,
            font=('Segoe UI', 9),
            padx=8,
            pady=6,
        )
        scrollbar = ttk.Scrollbar(result_frame, command=self.result.yview)
        self.result.configure(yscrollcommand=scrollbar.set)
        self.result.pack(side='left', fill='both', expand=True)
        scrollbar.pack(side='right', fill='y')
        return tab

    def build_template_tab(self, parent):
        tab = ttk.Frame(parent, padding=PADDING)

        ttk.Label(tab, text='Modelo da planilha', style='Title.TLabel').pack(anchor='w')
        ttk.Label(
            tab,
            text='Planilha usada como base. Novas linhas e códigos podem ser '
            'adicionados direto no modelo, mantendo o layout.',
            style='Muted.TLabel',
            wraplength=WIDTH - 60,
        ).pack(anchor='w', pady=(2, 16))

        ttk.Label(tab, text='Modelo atual').pack(anchor='w')
        ttk.Label(
            tab, textvariable=self.template_name, font=('Segoe UI', 10, 'bold')
        ).pack(anchor='w', pady=(2, 0))
        ttk.Label(
            tab,
            textvariable=self.template_full,
            style='Muted.TLabel',
            wraplength=WIDTH - 60,
        ).pack(anchor='w', pady=(0, 16))

        ttk.Button(tab, text='Trocar modelo...', command=self.choose_template).pack(
            fill='x', pady=(0, 8)
        )
        ttk.Button(tab, text='Abrir modelo no Excel', command=self.open_template).pack(
            fill='x'
        )
        return tab

    def set_template(self, path, save=True):
        if path is None:
            self.template_path = None
            self.template_name.set('Nenhum modelo selecionado')
            self.template_full.set('Clique em "Trocar modelo..." para escolher.')
            return
        self.template_path = Path(path)
        self.template_name.set(self.template_path.name)
        self.template_full.set(str(self.template_path))
        if save:
            save_template_path(self.template_path)

    def choose_template(self):
        path = filedialog.askopenfilename(
            title='Selecione o modelo',
            initialdir=self.template_path.parent if self.template_path else None,
            filetypes=[('Planilha Excel', '*.xlsx')],
        )
        if path:
            self.set_template(path)
        return bool(path)

    def open_template(self):
        if self.template_path:
            os.startfile(self.template_path)

    def choose_docx(self):
        path = filedialog.askopenfilename(
            title='Selecione o documento', filetypes=[('Documento Word', '*.docx')]
        )
        if path:
            self.docx_path = Path(path)
            self.docx_name.set(self.docx_path.name)
            self.generate_button.configure(state='normal')
            self.show_result('')

    def show_result(self, text):
        self.result.configure(state='normal')
        self.result.delete('1.0', 'end')
        self.result.insert('1.0', text)
        self.result.configure(state='disabled')

    def generate(self):
        if self.template_path is None or not self.template_path.exists():
            messagebox.showinfo(
                'Modelo', 'Nenhum modelo encontrado. Selecione a planilha modelo.'
            )
            if not self.choose_template():
                return

        output = filedialog.asksaveasfilename(
            title='Salvar planilha',
            initialdir=self.docx_path.parent,
            initialfile=f'{self.docx_path.stem}_materiais.xlsx',
            defaultextension='.xlsx',
            filetypes=[('Planilha Excel', '*.xlsx')],
        )
        if not output:
            return

        try:
            materials = read_materials(self.docx_path)
            if not materials:
                messagebox.showwarning(
                    'Nada encontrado',
                    'Nenhuma tabela de MATERIAL foi encontrada no documento.',
                )
                return
            added = fill(materials, output, self.template_path)
        except PermissionError:
            messagebox.showerror(
                'Erro',
                'Não foi possível salvar. '
                'Feche a planilha se ela estiver aberta no Excel.',
            )
            return
        except Exception as error:
            logging.exception('Falha ao gerar planilha')
            messagebox.showerror('Erro', f'Falha ao gerar planilha:\n{error}')
            return

        message = (
            f'{len(materials)} materiais processados.\n'
            f'{len(materials) - len(added)} encontrados no modelo.\n\n'
            f'Salvo em:\n{output}'
        )
        if added:
            message += (
                '\n\nAdicionados como novas linhas (sem código Protheus):\n- '
                + '\n- '.join(added)
            )
        self.show_result(message)
        if messagebox.askyesno('Concluído', 'Planilha gerada. Abrir agora?'):
            os.startfile(output)


def run():
    logging.basicConfig(level=logging.INFO, format='%(levelname)s: %(message)s')
    App().mainloop()
