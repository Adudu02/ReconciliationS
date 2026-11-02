#!/usr/bin/env node
/*
 * Convert a build log (Markdown subset) to .docx, then to PDF with LibreOffice.
 *
 * Supported Markdown: # to #### headings, paragraphs, bullet and numbered lists (one nested level), pipe tables,
 * fenced code blocks, ![image](relative.png), **bold**, *italic*, `code`, [text](url).
 *
 * Usage (from the repository root):
 *   npm install --prefix /tmp/mdtools docx            # once; keeps node_modules out of the repository
 *   NODE_PATH=/tmp/mdtools/node_modules node tools/md_to_docx.js docs/build-log/BUILD-LOG.md docs/build-log/exports/BUILD-LOG.docx
 *   soffice --headless --convert-to pdf --outdir docs/build-log/exports docs/build-log/exports/BUILD-LOG.docx
 *
 * Page: US Letter, 1 inch margins. Images are PNG files resolved relative to the Markdown file.
 */
const fs = require('fs');
const path = require('path');
const D = require('docx');
const { Document, Packer, Paragraph, TextRun, ImageRun, Table, TableRow, TableCell, WidthType, ShadingType,
  HeadingLevel, LevelFormat, BorderStyle, AlignmentType } = D;

const [src, out] = process.argv.slice(2);
if (!src || !out) {
  console.error('usage: md_to_docx.js <input.md> <output.docx>');
  process.exit(2);
}
const baseDir = path.dirname(src);
const lines = fs.readFileSync(src, 'utf8').split('\n');
const FONT = 'Calibri';
const MONO = 'Courier New';

function inline(text, base = {}) {
  const runs = [];
  const re = /(\*\*(.+?)\*\*|\*(.+?)\*|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\))/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) runs.push(new TextRun({ ...base, text: text.slice(last, m.index) }));
    if (m[2] !== undefined) runs.push(...inline(m[2], { ...base, bold: true }));
    else if (m[3] !== undefined) runs.push(...inline(m[3], { ...base, italics: true }));
    else if (m[4] !== undefined) runs.push(new TextRun({ ...base, text: m[4], font: MONO, size: (base.size || 21) - 2, color: '9B2C2C' }));
    else runs.push(new TextRun({ ...base, text: m[5], color: '1F5FA8', underline: {} }));
    last = re.lastIndex;
  }
  if (last < text.length) runs.push(new TextRun({ ...base, text: text.slice(last) }));
  return runs;
}

function pngSize(file) {
  const b = fs.readFileSync(file);
  return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) };
}

function buildTable(rows) {
  const cells = rows.map(r => r.replace(/^\||\|$/g, '').split('|').map(c => c.trim()));
  const header = cells[0];
  const body = cells.slice(2);
  const n = header.length;
  const total = 9360;
  const weights = header.map((_, i) => {
    const lens = [header[i], ...body.map(r => r[i] || '')].map(s => s.length);
    const avg = lens.reduce((a, b) => a + b, 0) / lens.length;
    return Math.min(Math.max(avg, 16), 55);
  });
  const sw = weights.reduce((a, b) => a + b, 0);
  const widths = weights.map(w => Math.floor(total * w / sw));
  widths[n - 1] += total - widths.reduce((a, b) => a + b, 0);
  const border = { style: BorderStyle.SINGLE, size: 4, color: 'BFBFBF' };
  const borders = { top: border, bottom: border, left: border, right: border };
  const mk = (txt, i, isHead) => new TableCell({
    width: { size: widths[i], type: WidthType.DXA }, borders,
    margins: { top: 60, bottom: 60, left: 90, right: 90 },
    shading: isHead ? { type: ShadingType.CLEAR, fill: '1F3864', color: 'auto' } : undefined,
    children: [new Paragraph({ children: inline(txt, { size: 18, font: FONT, ...(isHead ? { bold: true, color: 'FFFFFF' } : {}) }) })],
  });
  return new Table({
    width: { size: total, type: WidthType.DXA }, columnWidths: widths,
    rows: [
      new TableRow({ tableHeader: true, children: header.map((h, i) => mk(h, i, true)) }),
      ...body.map(r => new TableRow({ cantSplit: true, children: header.map((_, i) => mk(r[i] || '', i, false)) })),
    ],
  });
}

const children = [];
const spacer = () => new Paragraph({ children: [], spacing: { after: 120 } });
let i = 0;
while (i < lines.length) {
  const line = lines[i];
  if (!line.trim()) { i++; continue; }
  let m;
  if (line.startsWith('```')) {
    i++;
    const code = [];
    while (i < lines.length && !lines[i].startsWith('```')) code.push(lines[i++]);
    i++;
    code.forEach((l, idx) => children.push(new Paragraph({
      keepNext: idx < code.length - 1, keepLines: true,
      shading: { type: ShadingType.CLEAR, fill: 'F2F2F2', color: 'auto' },
      spacing: { after: idx === code.length - 1 ? 160 : 0, line: 240 },
      children: [new TextRun({ text: l.length ? l : ' ', font: MONO, size: 15 })] })));
  } else if ((m = line.match(/^(#{1,4}) (.*)/))) {
    const lvl = [HeadingLevel.HEADING_1, HeadingLevel.HEADING_2, HeadingLevel.HEADING_3, HeadingLevel.HEADING_4][m[1].length - 1];
    children.push(new Paragraph({ heading: lvl, children: inline(m[2]) }));
    i++;
  } else if ((m = line.match(/^!\[(.*)\]\((.*)\)/))) {
    const f = path.join(baseDir, m[2]);
    const { w, h } = pngSize(f);
    const width = 620; // px at 96 dpi, about 6.45 in
    children.push(new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 160 }, children: [
      new ImageRun({ type: 'png', data: fs.readFileSync(f), transformation: { width, height: Math.round(width * h / w) },
        altText: { title: m[1], description: m[1], name: m[1] } })] }));
    i++;
  } else if (line.startsWith('|')) {
    const rows = [];
    while (i < lines.length && lines[i].startsWith('|')) rows.push(lines[i++]);
    children.push(buildTable(rows), spacer());
  } else if (/^\s*- /.test(line)) {
    const items = [];
    while (i < lines.length && lines[i].trim() &&
      (/^\s*- /.test(lines[i]) || (/^\s+\S/.test(lines[i]) && items.length))) {
      const l = lines[i];
      const mm = l.match(/^(\s*)- (.*)/);
      if (mm) items.push({ level: mm[1].length >= 2 ? 1 : 0, text: mm[2] });
      else items[items.length - 1].text += ' ' + l.trim();
      i++;
    }
    for (const it of items) children.push(new Paragraph({ numbering: { reference: 'bullets', level: it.level },
      spacing: { after: 60 }, children: inline(it.text) }));
  } else if (/^\d+\. /.test(line)) {
    const items = [];
    while (i < lines.length && lines[i].trim() && (/^\d+\. /.test(lines[i]) || /^\s+\S/.test(lines[i]))) {
      if (/^\d+\. /.test(lines[i])) items.push(lines[i].replace(/^\d+\. /, ''));
      else items[items.length - 1] += ' ' + lines[i].trim();
      i++;
    }
    for (const t of items) children.push(new Paragraph({ numbering: { reference: 'numbers', level: 0 },
      spacing: { after: 60 }, children: inline(t) }));
  } else {
    // paragraph; a line starting with ** begins a new paragraph
    let buf = line.trim(); i++;
    while (i < lines.length && lines[i].trim() && !/^(#|\||!\[|\s*- |\d+\. |\*\*|```)/.test(lines[i])) buf += ' ' + lines[i++].trim();
    children.push(new Paragraph({ spacing: { after: 120 }, children: inline(buf) }));
  }
}

const hStyle = (id, size, color, before, after) => ({ id, name: id.replace('Heading', 'Heading '), basedOn: 'Normal', next: 'Normal', quickFormat: true,
  run: { font: FONT, size, bold: true, color }, paragraph: { spacing: { before, after }, outlineLevel: Number(id.slice(-1)) - 1, keepNext: true } });

const doc = new Document({
  creator: 'BNK', title: path.basename(src, '.md'),
  styles: {
    default: { document: { run: { font: FONT, size: 21 } } },
    paragraphStyles: [
      hStyle('Heading1', 40, '1F3864', 0, 200),
      hStyle('Heading2', 30, '1F3864', 320, 140),
      hStyle('Heading3', 25, '2E5597', 260, 100),
      hStyle('Heading4', 22, '2E5597', 200, 80),
    ],
  },
  numbering: { config: [
    { reference: 'bullets', levels: [
      { level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } },
      { level: 1, format: LevelFormat.BULLET, text: '–', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1080, hanging: 270 } } } } ] },
    { reference: 'numbers', levels: [
      { level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 360 } } } } ] },
  ] },
  sections: [{ properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } } }, children }],
});
Packer.toBuffer(doc).then(b => fs.writeFileSync(out, b));
