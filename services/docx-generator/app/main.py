from fastapi import FastAPI
from fastapi.responses import FileResponse
from pydantic import BaseModel
from typing import List, Optional
from pathlib import Path
import uuid

from docx import Document
from docx.shared import Pt
from docx.oxml.ns import qn
from openpyxl import Workbook
from openpyxl.styles import Font, Alignment


app = FastAPI(
    title="AI-ACE Document Generator",
    version="2.0"
)


OUTPUT_DIR = Path("/output")
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


@app.get("/health")
def health():
    return {"status": "ok", "service": "docx-generator"}


class TableData(BaseModel):
    header: List[str] = []
    rows: List[List[str]] = []


class SectionData(BaseModel):
    heading: str = ""
    body: str = ""


class ReportRequest(BaseModel):
    title: str = ""
    content: str = ""
    sections: List[SectionData] = []
    tables: List[TableData] = []
    format: str = "docx"       # "docx" 或 "xlsx"
    font_name: str = "宋体"
    font_size: float = 12


def _set_run_font(run, font_name, font_size):
    run.font.name = font_name
    run.font.size = Pt(font_size)
    # 中文字体需设置 eastAsia
    rpr = run._element.get_or_add_rPr()
    rfonts = rpr.find(qn("w:rFonts"))
    if rfonts is None:
        rfonts = rpr.makeelement(qn("w:rFonts"), {})
        rpr.append(rfonts)
    rfonts.set(qn("w:eastAsia"), font_name)
    rfonts.set(qn("w:ascii"), font_name)
    rfonts.set(qn("w:hAnsi"), font_name)


def _build_docx(req: ReportRequest) -> Path:
    doc = Document()

    if req.title:
        h = doc.add_heading(req.title, level=1)
        for run in h.runs:
            _set_run_font(run, req.font_name, req.font_size + 4)

    if req.content:
        p = doc.add_paragraph(req.content)
        for run in p.runs:
            _set_run_font(run, req.font_name, req.font_size)

    for sec in req.sections:
        if sec.heading:
            h = doc.add_heading(sec.heading, level=2)
            for run in h.runs:
                _set_run_font(run, req.font_name, req.font_size + 2)
        if sec.body:
            p = doc.add_paragraph(sec.body)
            for run in p.runs:
                _set_run_font(run, req.font_name, req.font_size)

    for tbl in req.tables:
        header = tbl.header or []
        rows = tbl.rows or []
        ncols = max([len(header)] + [len(r) for r in rows] + [1])
        table = doc.add_table(rows=1, cols=ncols)
        table.style = "Table Grid"

        # 表头
        for i in range(ncols):
            cell = table.rows[0].cells[i]
            text = header[i] if i < len(header) else ""
            cell.text = text
            for p in cell.paragraphs:
                for run in p.runs:
                    run.bold = True
                    _set_run_font(run, req.font_name, req.font_size)

        # 数据行
        for row in rows:
            cells = table.add_row().cells
            for i in range(ncols):
                text = row[i] if i < len(row) else ""
                cells[i].text = text
                for p in cells[i].paragraphs:
                    for run in p.runs:
                        _set_run_font(run, req.font_name, req.font_size)

    file_id = str(uuid.uuid4())
    path = OUTPUT_DIR / f"{file_id}.docx"
    doc.save(path)
    return path


def _build_xlsx(req: ReportRequest) -> Path:
    wb = Workbook()
    ws = wb.active
    ws.title = (req.title or "报告")[:31]

    row_idx = 1
    font = Font(name=req.font_name, size=req.font_size)

    if req.title:
        c = ws.cell(row=row_idx, column=1, value=req.title)
        c.font = Font(name=req.font_name, size=req.font_size + 4, bold=True)
        row_idx += 1

    if req.content:
        ws.cell(row=row_idx, column=1, value=req.content).font = font
        row_idx += 1

    for sec in req.sections:
        if sec.heading:
            c = ws.cell(row=row_idx, column=1, value=sec.heading)
            c.font = Font(name=req.font_name, size=req.font_size + 2, bold=True)
            row_idx += 1
        if sec.body:
            ws.cell(row=row_idx, column=1, value=sec.body).font = font
            row_idx += 1

    row_idx += 1
    for tbl in req.tables:
        header = tbl.header or []
        rows = tbl.rows or []
        if header:
            for i, h in enumerate(header):
                c = ws.cell(row=row_idx, column=i + 1, value=h)
                c.font = Font(name=req.font_name, size=req.font_size, bold=True)
                c.alignment = Alignment(horizontal="center")
            row_idx += 1
        for row in rows:
            for i in range(len(row)):
                ws.cell(row=row_idx, column=i + 1, value=row[i]).font = font
            row_idx += 1
        row_idx += 1

    file_id = str(uuid.uuid4())
    path = OUTPUT_DIR / f"{file_id}.xlsx"
    wb.save(path)
    return path


@app.post("/generate")
def generate(req: ReportRequest):
    fmt = (req.format or "docx").lower()
    if fmt == "xlsx":
        path = _build_xlsx(req)
    else:
        path = _build_docx(req)
    return {
        "status": "success",
        "file": path.name,
        "format": fmt,
        "path": str(path)
    }


@app.get("/download/{filename}")
def download(filename: str):
    path = OUTPUT_DIR / filename
    if not path.exists():
        return {"status": "error", "message": "文件不存在"}
    return FileResponse(path, filename=filename)
