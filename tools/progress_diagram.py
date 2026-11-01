#!/usr/bin/env python3
"""Draw the build status map from the task list.

Reads openspec/changes/mvp-reconciliation-core/tasks.md (the `- [x]` / `- [ ]` checkboxes) and writes
docs/diagrams/progress/status-map.svg (and a PNG when ImageMagick is installed). A component's status is derived
from the tasks that deliver it, so ticking a task in tasks.md and re-running this script is all it takes.

    python3 tools/progress_diagram.py            # English: docs/diagrams/progress/status-map.{svg,png}
    python3 tools/progress_diagram.py --lang es  # Spanish: docs/diagrams/progress/status-map-es.{svg,png}
"""
import argparse
import datetime
import pathlib
import re
import shutil
import subprocess
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
TASKS = ROOT / 'openspec/changes/mvp-reconciliation-core/tasks.md'
OUT = ROOT / 'docs/diagrams/progress'

GROUP_NAMES = {1: 'Foundations', 2: 'Tenancy', 3: 'Ingestion', 4: 'Jobs', 5: 'Audit', 6: 'Matching', 7: 'SAT status',
               8: 'Review + API', 9: 'Test data', 10: 'Observability', 11: 'Delivery'}

# English text -> Spanish text. Anything missing falls back to the English text.
ES = {
    'Foundations': 'Cimientos', 'Tenancy': 'Multiempresa', 'Ingestion': 'Ingesta', 'Jobs': 'Trabajos', 'Audit': 'Auditoría',
    'Matching': 'Conciliación', 'SAT status': 'Estatus SAT', 'Review + API': 'Revisión + API', 'Test data': 'Datos de prueba',
    'Observability': 'Observabilidad', 'Delivery': 'Entrega',
    'Programs that run: hosts and tools': 'Programas que se ejecutan: hosts y herramientas',
    'Modules: code areas inside Reconciliation.Core': 'Módulos: áreas de código dentro de Reconciliation.Core',
    'Database: PostgreSQL': 'Base de datos: PostgreSQL',
    'Tools and delivery': 'Herramientas y entrega',
    'API host': 'Host de la API', 'Worker host': 'Host del worker', 'Migrator': 'Migrador', 'SAT stub': 'Stub del SAT',
    'SAT client': 'Cliente SAT', 'Review': 'Revisión',
    'Identity tables': 'Tablas de identidad', 'Tenant isolation (RLS)': 'Aislamiento (RLS)', 'Ingestion tables': 'Tablas de ingesta',
    'Job queue table': 'Tabla de cola', 'Audit log table': 'Tabla de bitácora', 'Links and matching': 'Vínculos y conciliación',
    'SAT status cache': 'Caché de estatus SAT', 'Docker Compose': 'Docker Compose', 'CI pipeline': 'Pipeline de CI',
    'ADRs': 'ADR', 'Test data tools': 'Datos de prueba', 'Failure tests': 'Pruebas de fallos', 'Runbook': 'Guía de operación',
    'Demo and docs': 'Demo y docs',
    'HOST': 'HOST', 'MODULE': 'MÓDULO', 'DATABASE': 'BASE DE DATOS', 'TOOL': 'HERRAMIENTA', 'PIPELINE': 'PIPELINE',
    'DOCS': 'DOCS', 'TESTS': 'PRUEBAS',
    'NEXT': 'SIGUIENTE', 'of': 'de', 'task': 'tarea', 'tasks': 'tareas',
    'done': 'completo', 'in progress': 'en curso', 'not started': 'sin empezar',
    'Build status map': 'Mapa de estado de la construcción',
    'Roadmap: 11 task groups': 'Hoja de ruta: 11 grupos de tareas',
    'touched by the next group of tasks': 'tocado por el siguiente grupo de tareas',
    'tasks done': 'tareas completas', 'updated': 'actualizado', 'source': 'fuente',
}


def make_tr(lang):
    return (lambda s: ES.get(s, s)) if lang == 'es' else (lambda s: s)


def tasks_by_id():
    """Task id -> done flag, in file order."""
    found = {}
    for line in TASKS.read_text(encoding='utf-8').splitlines():
        m = re.match(r'^- \[( |x)\] (\S+) ', line)
        if m:
            found[m.group(2)] = m.group(1) == 'x'
    return found


def prefix(group, skip=()):
    return lambda ids: [t for t in ids if t.startswith(f'{group}.') and t not in skip]


def pick(*wanted):
    return lambda ids: [t for t in ids if t in wanted]


# lane -> (title, [(name, type, selector)])
LANES = [
    ('Programs that run: hosts and tools', [
        ('API host', 'host', pick('1.1', '1.2', '2.3', '8.1', '8.2', '8.3', '8.3b', '8.3c', '8.4', '8.5')),
        ('Worker host', 'host', pick('1.1', '1.2', '4.1', '4.2', '4.3', '4.4', '4.6')),
        ('Migrator', 'tool', pick('1.5')),
        ('SAT stub', 'host', pick('1.2', '7.2')),
    ]),
    ('Modules: code areas inside Reconciliation.Core', [
        ('Tenancy', 'module', prefix(2, skip=('2.5',))),
        ('Ingestion', 'module', lambda ids: [t for t in ids if t.startswith('3.') and t not in ('3.1', '3.2')]),
        ('Jobs', 'module', pick('4.1', '4.2', '4.3', '4.4', '4.6')),
        ('Audit', 'module', prefix(5)),
        ('Matching', 'module', prefix(6, skip=('6.8',))),
        ('SAT client', 'module', pick('7.1', '7.3', '7.3b', '7.3c', '7.4', '7.5', '7.6')),
        ('Review', 'module', pick('8.1', '8.2', '8.3', '8.3b', '8.3c', '8.3d')),
    ]),
    ('Database: PostgreSQL', [
        ('Identity tables', 'database', pick('1.5')),
        ('Tenant isolation (RLS)', 'database', pick('2.1', '2.2', '2.6')),
        ('Ingestion tables', 'database', pick('3.1', '3.2', '3.5', '3.7')),
        ('Job queue table', 'database', pick('4.1')),
        ('Audit log table', 'database', pick('5.1', '5.2')),
        ('Links and matching', 'database', pick('6.1')),
        ('SAT status cache', 'database', pick('7.3')),
    ]),
    ('Tools and delivery', [
        ('Docker Compose', 'pipeline', pick('1.2')),
        ('CI pipeline', 'pipeline', pick('1.3')),
        ('ADRs', 'docs', pick('1.4', '2.5', '4.5', '6.8', '11.1', '11.1b')),
        ('Test data tools', 'tool', prefix(9)),
        ('Observability', 'pipeline', pick('10.1', '10.2', '10.3')),
        ('Failure tests', 'tests', pick('10.4')),
        ('Runbook', 'docs', pick('10.5')),
        ('Demo and docs', 'docs', pick('11.2', '11.3', '11.4', '11.5')),
    ]),
]

COLOR = {'done': ('#d8f0d8', '#2e7d32'), 'doing': ('#fff1cc', '#c77c00'), 'todo': ('#eeeeee', '#8a8a8a')}
TYPE_LABEL = {'host': 'HOST', 'module': 'MODULE', 'database': 'DATABASE', 'tool': 'TOOL', 'pipeline': 'PIPELINE',
              'docs': 'DOCS', 'tests': 'TESTS'}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--lang', choices=['en', 'es'], default='en')
    lang = parser.parse_args().lang
    tr = make_tr(lang)
    suffix = '-es' if lang == 'es' else ''

    done = tasks_by_id()
    ids = list(done)
    groups = {}
    for tid, ok in done.items():
        g = int(tid.split('.')[0])
        d, n = groups.get(g, (0, 0))
        groups[g] = (d + ok, n + 1)
    total_done, total = sum(v[0] for v in groups.values()), sum(v[1] for v in groups.values())
    next_group = min((g for g, (d, n) in groups.items() if d < n), default=None)

    W = 1410
    parts = []
    y = 122
    # roadmap
    parts.append(f'<text x="30" y="{y}" font-size="15" font-weight="bold" fill="#111">{escape(tr("Roadmap: 11 task groups"))}</text>')
    y += 14
    cw, gap = 118, 6
    for i, g in enumerate(sorted(groups)):
        d, n = groups[g]
        x = 30 + i * (cw + gap)
        state = 'done' if d == n else ('doing' if d else 'todo')
        fill, stroke = COLOR[state]
        extra = ' stroke-width="3" stroke="#1565c0"' if g == next_group else f' stroke-width="1.5" stroke="{stroke}"'
        parts.append(f'<rect x="{x}" y="{y}" width="{cw}" height="74" rx="8" fill="{fill}"{extra}/>')
        parts.append(f'<text x="{x + 8}" y="{y + 20}" font-size="11.5" font-weight="bold" fill="#111">{g}. {escape(tr(GROUP_NAMES[g]))}</text>')
        parts.append(f'<rect x="{x + 8}" y="{y + 32}" width="{cw - 16}" height="8" rx="4" fill="#ffffff" stroke="#bbb"/>')
        parts.append(f'<rect x="{x + 8}" y="{y + 32}" width="{(cw - 16) * d / n:.1f}" height="8" rx="4" fill="{stroke}"/>')
        parts.append(f'<text x="{x + 8}" y="{y + 58}" font-size="12" fill="#333">{d} {tr("of")} {n}</text>')
        if g == next_group:
            parts.append(f'<text x="{x + cw - 8}" y="{y + 58}" text-anchor="end" font-size="11" font-weight="bold" fill="#1565c0">{tr("NEXT")}</text>')
    y += 74 + 34

    # lanes
    card_w, card_h, per_row = 200, 78, 6
    for title, comps in LANES:
        parts.append(f'<text x="30" y="{y}" font-size="15" font-weight="bold" fill="#111">{escape(tr(title))}</text>')
        y += 12
        for i, (name, typ, selector) in enumerate(comps):
            row, col = divmod(i, per_row)
            x, cy = 30 + col * (card_w + 12), y + row * (card_h + 10)
            mine = selector(ids)
            d = sum(done[t] for t in mine)
            state = 'todo' if d == 0 else ('done' if d == len(mine) else 'doing')
            fill, stroke = COLOR[state]
            is_next = next_group is not None and any(t.startswith(f'{next_group}.') and not done[t] for t in mine)
            outline = ' stroke="#1565c0" stroke-width="3" stroke-dasharray="7 4"' if is_next else f' stroke="{stroke}" stroke-width="1.5"'
            parts.append(f'<rect x="{x}" y="{cy}" width="{card_w}" height="{card_h}" rx="8" fill="{fill}"{outline}/>')
            parts.append(f'<rect x="{x}" y="{cy}" width="8" height="{card_h}" rx="3" fill="{stroke}"/>')
            parts.append(f'<text x="{x + 18}" y="{cy + 22}" font-size="13.5" font-weight="bold" fill="#111">{escape(tr(name))}</text>')
            parts.append(f'<text x="{x + 18}" y="{cy + 40}" font-size="10.5" font-weight="bold" fill="#555">{escape(tr(TYPE_LABEL[typ]))}</text>')
            word = tr('task') if len(mine) == 1 else tr('tasks')
            n_mine = len(mine)
            label = {'done': f'{tr("done")} ({d} {tr("of")} {n_mine} {word})',
                     'doing': f'{tr("in progress")}: {d} {tr("of")} {n_mine} {word}',
                     'todo': f'{tr("not started")} ({n_mine} {word})'}[state]
            parts.append(f'<text x="{x + 18}" y="{cy + 60}" font-size="11.5" fill="#333">{escape(label)}</text>')
            if is_next:
                parts.append(f'<text x="{x + card_w - 10}" y="{cy + 40}" text-anchor="end" font-size="11" font-weight="bold" fill="#1565c0">{tr("NEXT")}</text>')
        rows = (len(comps) + per_row - 1) // per_row
        y += rows * (card_h + 10) + 26

    H = y + 44
    pct = 100 * total_done // total
    head = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="Liberation Sans, Arial, sans-serif">',
        f'<rect width="{W}" height="{H}" fill="#fff"/>',
        f'<text x="30" y="40" font-size="22" font-weight="bold" fill="#111">{escape(tr("Build status map"))}</text>',
        f'<text x="30" y="64" font-size="13.5" fill="#444">{total_done} {tr("of")} {total} {tr("tasks done")} ({pct}%) · {tr("updated")} '
        f'{datetime.date.today().isoformat()} · {tr("source")}: openspec/changes/mvp-reconciliation-core/tasks.md</text>',
        # legend
        f'<rect x="30" y="74" width="16" height="12" rx="3" fill="#d8f0d8" stroke="#2e7d32"/><text x="52" y="85" font-size="12" fill="#333">{tr("done")}</text>',
        f'<rect x="120" y="74" width="16" height="12" rx="3" fill="#fff1cc" stroke="#c77c00"/><text x="142" y="85" font-size="12" fill="#333">{tr("in progress")}</text>',
        f'<rect x="235" y="74" width="16" height="12" rx="3" fill="#eeeeee" stroke="#8a8a8a"/><text x="257" y="85" font-size="12" fill="#333">{tr("not started")}</text>',
        f'<rect x="350" y="74" width="16" height="12" rx="3" fill="#fff" stroke="#1565c0" stroke-width="2.5" stroke-dasharray="4 2"/><text x="372" y="85" font-size="12" fill="#333">{tr("touched by the next group of tasks")}</text>',
    ]
    OUT.mkdir(parents=True, exist_ok=True)
    svg_path = OUT / f'status-map{suffix}.svg'
    svg_path.write_text('\n'.join(head + parts + ['</svg>']), encoding='utf-8')
    print(f'wrote {svg_path.relative_to(ROOT)}: {total_done}/{total} tasks, next group {next_group}')
    magick = shutil.which('magick')
    if magick:
        png_path = OUT / f'status-map{suffix}.png'
        subprocess.run([magick, '-density', '110', str(svg_path), str(png_path)], check=True)
        print(f'wrote {png_path.relative_to(ROOT)}')
    else:
        print('ImageMagick not found: only the SVG was written')


if __name__ == '__main__':
    main()
