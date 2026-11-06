#!/usr/bin/env python3
"""Draw the AS-BUILT database diagrams (migrations M0001 to M0012) as SVG and PNG.

Writes into docs/diagrams/database/:
  database-erd-implemented[-es]      tables, columns and foreign keys, grouped by area
  database-states-implemented[-es]   import_file status machine and session / refresh-token lifecycle
  database-security-implemented[-es] forced row-level security, privileges of the application role, triggers

Usage:
    python3 tools/db_diagrams.py            # English
    python3 tools/db_diagrams.py --lang es  # Spanish (titles, legends and notes; identifiers stay as in the code)

The schema below mirrors src/Reconciliation.Migrator/Migrations. When a migration changes a table, update TABLES
(and GRANTS / TRIGGERS) here and run the script again, then commit the new files.
"""
import argparse
import pathlib
import shutil
import subprocess
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / 'docs/diagrams/database'

ES = {
    'As-built data model (migrations M0001 to M0012)': 'Modelo de datos construido (migraciones M0001 a M0012)',
    'PK primary key · FK foreign key · UK unique · RLS = forced row-level security by company':
        'PK clave primaria · FK clave foránea · UK único · RLS = seguridad a nivel de fila forzada por empresa',
    'Tenancy and access': 'Multiempresa y acceso',
    'Sessions (per user, outside company RLS)': 'Sesiones (por usuario, fuera del RLS de empresa)',
    'Ingestion (every table carries company_id and forced RLS)': 'Ingesta (toda tabla lleva company_id y RLS forzado)',
    'Status and lifecycle rules of the data': 'Reglas de estado y ciclo de vida de los datos',
    'import_file.status': 'import_file.status',
    'Session and refresh token': 'Sesión y token de refresco',
    'upload received': 'archivo recibido',
    'importing': 'importing',
    'transient: lives only inside the transaction, never committed': 'transitorio: vive solo dentro de la transacción, nunca se confirma',
    'valid CFDI or CSV': 'CFDI o CSV válido',
    'original + sha256 stored, retained_until set': 'se guarda original + sha256; se fija retained_until',
    'invalid, hostile or not the company\'s': 'inválido, hostil o ajeno a la empresa',
    'hash kept, bytes not kept': 'se conserva el hash, no los bytes',
    'UUID already imported': 'UUID ya importado',
    'original_file_id points to the first file': 'original_file_id apunta al primer archivo',
    'Once accepted: original, sha256 and retention cannot change or shrink (trigger preserve_import_original).':
        'Ya aceptado: original, sha256 y retención no cambian ni se acortan (trigger preserve_import_original).',
    'No DELETE privilege and no DELETE route exist.': 'No existe privilegio DELETE ni ruta DELETE.',
    'login': 'login',
    'revoked_at is NULL': 'revoked_at es NULL',
    'Active': 'Activa',
    'Revoked': 'Revocada',
    'Expired': 'Caducada',
    'logout, password_changed, reuse_detected': 'logout, password_changed, reuse_detected',
    'idle 8 h or absolute 30 days (computed, not stored)': 'inactividad 8 h o tope de 30 días (se calcula, no se guarda)',
    'Current token': 'Token vigente',
    'Rotated token': 'Token rotado',
    'rotated_at is NULL': 'rotated_at es NULL',
    'rotated_at set, replaced_by = new token': 'rotated_at definido, replaced_by = token nuevo',
    'POST /auth/refresh: rotate': 'POST /auth/refresh: rota',
    'presented again within the grace window (10 s): 409, nothing changes':
        'presentado de nuevo dentro de la ventana de gracia (10 s): 409, nada cambia',
    'presented again after the grace window: session revoked': 'presentado de nuevo después de la ventana: se revoca la sesión',
    'Table': 'Tabla', 'Area': 'Área', 'Forced RLS': 'RLS forzado', 'Privileges of role cfdi_app': 'Privilegios del rol cfdi_app',
    'Immutability / triggers': 'Inmutabilidad / triggers',
    'Row security, privileges and immutability': 'Seguridad de filas, privilegios e inmutabilidad',
    'S = SELECT · I = INSERT · U(columns) = UPDATE only on the named columns · no table grants DELETE':
        'S = SELECT · I = INSERT · U(columnas) = UPDATE solo en las columnas indicadas · ninguna tabla concede DELETE',
    'sat': 'SAT', 'cancelled permanent; company expiry index': 'cancelled permanente; índice de caducidad por empresa',
    'tenancy': 'multiempresa', 'sessions': 'sesiones', 'ingestion': 'ingesta',
    'yes': 'sí', 'no (per user)': 'no (por usuario)', 'no': 'no',
    'rfc cannot change (trigger company_rfc_immutable)': 'rfc no cambia (trigger company_rfc_immutable)',
    'original, sha256 and retention frozen (trigger)': 'original, sha256 y retención congelados (trigger)',
    'append-only': 'solo agregar',
    'raw RLS note': 'RLS',
}

ES.update({
    'S I; append-only': 'S I; solo anexar',
    'active pair unique; derived item states': 'par activo único; estados derivados',
    'one decision; same transaction as audit': 'una decisión; transacción con auditoría',
    'cancelled evidence: review; no capacity': 'evidencia cancelada: revisión; sin capacidad',
    'jobs': 'trabajos', 'Jobs, review decisions and SAT cache (forced RLS; restricted queue claims)':
        'Trabajos, decisiones y caché SAT (RLS forzado; claim restringido)',
    'Durable jobs': 'Trabajos duraderos',
    'queued -> processing': 'queued -> processing',
    'pending_original; processing is transaction-local': 'pending_original; processing vive dentro de la transacción',
    'claim + attempt + lease': 'claim + intento + lease', 'complete': 'completar',
    'permanent or exhausted': 'permanente o agotado', 'retry/backoff or expired lease': 'reintento/backoff o lease vencido',
    'administrator retry (audited)': 'reintento de administrador (auditado)',
    'claim/renew/finish through restricted definer': 'claim/renew/finish por dueño restringido',
    'attempt writes through restricted definer only': 'intentos escritos solo por dueño restringido',
    'cfdi_queue_owner: NOLOGIN BYPASSRLS, grants only on job/job_attempt.':
        'cfdi_queue_owner: NOLOGIN BYPASSRLS, permisos solo en job/job_attempt.',
    'cfdi_app: NOBYPASSRLS; functions pinned and EXECUTE not granted to PUBLIC.':
        'cfdi_app: NOBYPASSRLS; funciones fijadas, sin EXECUTE para PUBLIC.',
})

ES.update({
    'S; append_audit_entry EXECUTE': 'S; EXECUTE de append_audit_entry',
    'S; head writes through append only': 'S; escritura de cabecera solo por append',
    'company head lock; no app table writes': 'bloqueo por empresa; app no escribe la tabla',
    'UPDATE / DELETE / TRUNCATE refused by triggers': 'triggers rechazan UPDATE / DELETE / TRUNCATE',
})

ES.update({
    'matching': 'conciliación',
    'Links and derived states': 'Vínculos y estados derivados',
    'link FKs use company_id + id; payment_ref targets cfdi_payment': 'FK de link usa company_id + id; payment_ref apunta a cfdi_payment',
    'movement_state / cfdi_state derive item states': 'movement_state / cfdi_state derivan estados',
    'heuristics; confidence + criteria': 'heurísticas; confianza + criterios',
    'exact payment-complement evidence': 'evidencia exacta de complemento',
    'superseded / excess provisional capacity': 'sustituido / excedente provisional',
    'group 8 user decision; kept as tombstone': 'decisión del grupo 8; se conserva rechazo',
    'later complement: upgrade': 'complemento posterior: mejora',
    'automatic suggestion in place': 'sugerencia automática en su lugar',
    'system closes provisional suggestion': 'el sistema cierra sugerencia provisional',
    'movement_state / cfdi_state / cfdi_payable / cfdi_review_reasons: derived, company-scoped reads.':
        'movement_state / cfdi_state / cfdi_payable / cfdi_review_reasons: lecturas derivadas por empresa.',
})

AREAS = {
    'sat': ('#cffafe', '#0891b2'),
    'matching': ('#fef9c3', '#a16207'),
    'jobs': ('#fff7ed', '#c2410c'),
    'tenancy': ('#dbeafe', '#1d4ed8'),
    'sessions': ('#ede9fe', '#6d28d9'),
    'ingestion': ('#dcfce7', '#15803d'),
}

# M0007 introduced jobs; M0008 adds audit chains and function-only appends.
# name: (area, rls, [(column, type, flags)])
# M0011 adds sat_status_cache, job.seq identity/FIFO and EFOS review reasons.
# M0010 adds cancellation_status, link.review_reason and cfdi_payable()/cfdi_review_reasons().
# M0009 adds link and derived movement_state()/cfdi_state(); item states are never stored.
TABLES = {
    'sat_status_cache': ('sat', True, [
        ('company_id', 'uuid', 'PK FK'), ('uuid', 'uuid', 'PK FK'), ('state', '4 states', 'CHECK'),
        ('cancellability', 'text nullable', ''), ('cancellation_status', 'text nullable', ''), ('efos_raw', 'text nullable', ''),
        ('checked_at', 'timestamptz', ''), ('expires_at', 'timestamptz nullable', ''), ('last_error', 'text nullable', ''),
        ('attempts', 'int', '>= 0'), ('source', 'backfill/stub/real/replay', ''), ('code_raw', 'text nullable', ''),
        ('state_raw', 'text nullable', ''), ('cancellability_raw', 'text nullable', ''), ('cancellation_status_raw', 'text nullable', ''),
        ('last_job_id', 'uuid nullable', ''), ('last_error_kind', 'text nullable', '')]),
    'link_decision': ('matching', True, [
        ('company_id', 'uuid', 'PK FK'), ('id', 'uuid', 'PK'), ('link_id', 'uuid', 'FK'),
        ('type', 'text (3 types)', 'CHECK'), ('decided_by', 'uuid', 'FK'),
        ('decided_at', 'timestamptz', ''), ('detail', 'jsonb object', '')]),
    'link': ('matching', True, [
        ('company_id', 'uuid', 'PK FK'), ('id', 'uuid', 'PK'), ('movement_id', 'uuid', 'FK'), ('cfdi_id', 'uuid', 'FK'),
        ('payment_cfdi_id', 'uuid nullable', 'FK'), ('payment_ref', 'uuid nullable', 'FK'), ('amount_applied', 'numeric(18,2)', '> 0'),
        ('state', '4 states', 'CHECK'), ('origin', 'automatic / user', ''), ('criteria', 'jsonb array', ''),
        ('confidence', 'high/medium/low', ''), ('explanation', 'text', ''), ('created_at', 'timestamptz', ''),
        ('decided_by', 'uuid nullable', 'FK'), ('decided_at', 'timestamptz', ''), ('reopened_at', 'timestamptz', ''), ('review_reason', 'text nullable', '')]),
    'job': ('jobs', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('seq', 'bigint identity', 'FIFO'), ('type', 'text (4 types)', ''),
        ('state', 'text (4 states)', ''), ('payload', 'jsonb', ''), ('input_ref', 'uuid', 'upload ref'),
        ('attempts', 'int', ''), ('max_attempts', 'int', ''), ('run_after', 'timestamptz', ''), ('locked_by', 'text', ''),
        ('locked_until', 'timestamptz', ''), ('progress_done', 'int', ''), ('progress_total', 'int', ''), ('last_error', 'text', ''),
        ('created_by', 'uuid', 'FK'), ('created_at', 'timestamptz', ''), ('finished_at', 'timestamptz', '')]),
    'job_attempt': ('jobs', True, [
        ('company_id', 'uuid', 'PK FK'), ('job_id', 'uuid', 'PK FK'), ('attempt_no', 'int', 'PK'),
        ('started_at', 'timestamptz', ''), ('finished_at', 'timestamptz', ''), ('outcome', 'text', ''),
        ('error_kind', 'transient/permanent', ''), ('error', 'text', '')]),
    'company': ('tenancy', True, [
        ('id', 'uuid', 'PK'), ('rfc', 'text', 'CHECK'), ('name', 'text', ''), ('date_tolerance_days', 'int (0-366)', ''),
        ('amount_tolerance', 'numeric <=1.00', ''), ('complement_next_biz_day', 'boolean', ''),
        ('created_at', 'timestamptz', ''), ('archived_at', 'timestamptz', '')]),
    'membership': ('tenancy', True, [
        ('company_id', 'uuid', 'PK FK'), ('user_id', 'uuid', 'PK FK'), ('role', 'membership_role', '')]),
    'app_user': ('tenancy', False, [
        ('id', 'uuid', 'PK'), ('email', 'text', 'UK'), ('password_hash', 'text', ''), ('created_at', 'timestamptz', '')]),
    'audit_entry': ('tenancy', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('company_seq', 'bigint', 'UK'),
        ('actor_type', 'user / system', 'CHECK'), ('actor_id', 'uuid nullable', 'FK'), ('action', 'text', ''),
        ('occurred_at', 'timestamptz UTC', ''), ('items', 'jsonb references[]', ''), ('detail', 'jsonb', ''),
        ('canonical', 'text UTF-8', ''), ('prev_hash, hash', 'bytea SHA-256', ''),
        ('old_values, new_values', 'jsonb', 'generated'), ('created_at', 'timestamptz', 'generated')]),
    'audit_chain_head': ('tenancy', True, [
        ('company_id', 'uuid', 'PK FK'), ('last_seq', 'bigint', ''), ('last_hash', 'bytea SHA-256', '')]),
    'auth_session': ('sessions', False, [
        ('id', 'uuid', 'PK'), ('user_id', 'uuid', 'FK'), ('created_at', 'timestamptz', ''), ('last_used_at', 'timestamptz', ''),
        ('absolute_expires_at', 'timestamptz', ''), ('revoked_at', 'timestamptz', ''), ('revoked_reason', 'text', '')]),
    'refresh_token': ('sessions', False, [
        ('id', 'uuid', 'PK'), ('session_id', 'uuid', 'FK'), ('token_hash', 'bytea (SHA-256)', 'UK'),
        ('issued_at', 'timestamptz', ''), ('expires_at', 'timestamptz', ''), ('rotated_at', 'timestamptz', ''),
        ('replaced_by', 'uuid', 'FK self')]),
    'auth_event': ('sessions', False, [
        ('id', 'uuid', 'PK'), ('user_id', 'uuid', 'FK'), ('session_id', 'uuid', 'FK'), ('event', 'text (5 values)', ''),
        ('remote_ip', 'inet', ''), ('created_at', 'timestamptz', '')]),
    'bank_account': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('bank', 'text', 'UK'), ('account_reference', 'text', 'UK')]),
    'bank_movement': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('bank_account_id', 'uuid', 'FK'), ('import_file_id', 'uuid', 'FK'),
        ('movement_date', 'date', 'UK'), ('description', 'text', 'UK'), ('credit', 'numeric(18,2)', 'UK'),
        ('debit', 'numeric(18,2)', 'UK'), ('balance', 'numeric(18,2)', 'UK'), ('reference', 'text', ''),
        ('classification', 'text enum', ''), ('classification_note', 'text <=2000', '')]),
    'import_file': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('upload_id', 'uuid', ''), ('filename', 'text', ''),
        ('kind', 'text', ''), ('file_index', 'int', ''), ('status', 'text', ''), ('reason', 'text', ''), ('pending_original', 'bytea', 'transient'), ('original', 'bytea', ''),
        ('sha256', 'bytea', ''), ('original_file_id', 'uuid', 'FK self'), ('retained_until', 'timestamptz', ''),
        ('imported_movements', 'int', ''), ('duplicate_movements', 'int', ''), ('created_at', 'timestamptz', '')]),
    'cfdi': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('import_file_id', 'uuid', 'FK'), ('uuid', 'uuid', 'UK'),
        ('type', 'I / P / E', ''), ('direction', 'issued / received', ''), ('issuer_rfc', 'text', ''),
        ('receiver_rfc', 'text', ''), ('issued_at', 'timestamp', ''), ('stamped_at', 'timestamp', ''),
        ('currency', 'text', ''), ('subtotal, discount', 'numeric(18,2)', ''), ('4 tax columns', 'numeric(18,2)', ''),
        ('serie, folio', 'text nullable', ''), ('total', 'numeric(18,2)', 'CHECK'), ('is_global', 'boolean', ''), ('review_reason', 'text', ''),
        ('sat_status', 'text', ''), ('cancellation_status', 'text nullable', '')]),
    'cfdi_relation': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('cfdi_id', 'uuid', 'FK'), ('related_uuid', 'uuid', ''),
        ('relation_code', 'text', '')]),
    'cfdi_payment': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('cfdi_id', 'uuid', 'FK'), ('payment_index', 'int', 'UK'),
        ('paid_at', 'timestamp', ''), ('payment_form', 'text', ''), ('currency', 'text', ''),
        ('exchange_rate', 'numeric(20,10)', ''), ('amount', 'numeric(18,2)', ''), ('operation_number', 'text', '')]),
    'cfdi_payment_doc': ('ingestion', True, [
        ('id', 'uuid', 'PK'), ('company_id', 'uuid', 'FK'), ('payment_id', 'uuid', 'FK'), ('invoice_uuid', 'uuid', ''),
        ('installment', 'int', ''), ('previous_balance', 'numeric(18,2)', ''), ('paid', 'numeric(18,2)', ''),
        ('remaining_balance', 'numeric(18,2)', ''), ('inconsistent_balance', 'boolean', '')]),
}

# privileges of role cfdi_app, and immutability rules (from the migrations)
GRANTS = {
    'sat_status_cache': 'S I U; no DELETE',
    'link': 'S I U; no DELETE',
    'link_decision': 'S I; append-only',
    'audit_chain_head': 'S; head writes through append only',
    'job': 'S I U(state, max_attempts, run_after, ...)', 'job_attempt': 'S',
    'company': 'S I U', 'membership': 'S I U', 'app_user': 'S I U(password_hash)', 'audit_entry': 'S; append_audit_entry EXECUTE',
    'auth_session': 'S I U(last_used_at, revoked_at, revoked_reason)', 'refresh_token': 'S I U(rotated_at, replaced_by)',
    'auth_event': 'S I', 'bank_account': 'S I', 'bank_movement': 'S I U(classification, classification_note)',
    'import_file': 'S I U(status, reason, original, sha256, ...)', 'cfdi': 'S I U(review_reason, sat_status, cancellation_status)',
    'cfdi_relation': 'S I', 'cfdi_payment': 'S I', 'cfdi_payment_doc': 'S I U(inconsistent_balance)',
}
NOTES = {
    'sat_status_cache': 'cancelled permanent; company expiry index',
    'link': 'active pair unique; derived item states',
    'link_decision': 'one decision; same transaction as audit',
    'audit_chain_head': 'company head lock; no app table writes',
    'job': 'claim/renew/finish through restricted definer',
    'job_attempt': 'attempt writes through restricted definer only',
    'company': 'rfc cannot change (trigger company_rfc_immutable)',
    'import_file': 'original, sha256 and retention frozen (trigger)',
    'auth_event': 'append-only', 'audit_entry': 'UPDATE / DELETE / TRUNCATE refused by triggers',
}

W = 280
HEAD = 26
ROW = 17
PAD = 8


def esc(s):
    return escape(str(s), {'"': '&quot;'})


class Ctx:
    def __init__(self, lang):
        self.lang = lang

    def tr(self, text):
        return ES.get(text, text) if self.lang == 'es' else text


def box_height(name):
    return HEAD + len(TABLES[name][2]) * ROW + PAD


class Layout:
    def __init__(self, positions):
        self.pos = positions

    def rect(self, name):
        x, y = self.pos[name]
        return x, y, W, box_height(name)

    def col_y(self, name, column):
        x, y, _, _ = self.rect(name)
        for i, (c, _, _) in enumerate(TABLES[name][2]):
            if c == column:
                return y + HEAD + i * ROW + ROW / 2
        raise KeyError((name, column))

    def anchor(self, name, side, column=None, dx=0):
        x, y, w, h = self.rect(name)
        if side == 'left':
            return x, self.col_y(name, column)
        if side == 'right':
            return x + w, self.col_y(name, column)
        if side == 'top':
            return x + w / 2 + dx, y
        return x + w / 2 + dx, y + h


def draw_table(name, lay, ctx):
    area, rls, cols = TABLES[name]
    fill, stroke = AREAS[area]
    x, y, w, h = lay.rect(name)
    out = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="6" fill="#fff" stroke="{stroke}" stroke-width="1.6"/>',
           f'<path d="M{x} {y + HEAD} V{y + 6} a6 6 0 0 1 6 -6 H{x + w - 6} a6 6 0 0 1 6 6 V{y + HEAD} Z" fill="{fill}" stroke="{stroke}" stroke-width="1.6"/>',
           f'<text x="{x + 10}" y="{y + 18}" font-size="14" font-weight="bold" fill="#111">{esc(name)}</text>']
    if rls:
        out.append(f'<rect x="{x + w - 44}" y="{y + 6}" width="36" height="15" rx="7" fill="#fff4e0" stroke="#c77c00"/>'
                   f'<text x="{x + w - 26}" y="{y + 17}" font-size="10" font-weight="bold" fill="#8a5300" text-anchor="middle">RLS</text>')
    for i, (c, t, f) in enumerate(cols):
        ry = y + HEAD + i * ROW
        if i % 2:
            out.append(f'<rect x="{x + 1}" y="{ry}" width="{w - 2}" height="{ROW}" fill="#f8fafc"/>')
        weight = ' font-weight="bold"' if 'PK' in f else ''
        out.append(f'<text x="{x + 10}" y="{ry + 12.5}" font-size="11.5"{weight} fill="#111">{esc(c)}</text>')
        out.append(f'<text x="{x + 158}" y="{ry + 12.5}" font-size="10.5" fill="#555">{esc(t)}</text>')
        if f:
            out.append(f'<text x="{x + w - 8}" y="{ry + 12.5}" font-size="10" font-weight="bold" fill="{stroke}" text-anchor="end">{esc(f)}</text>')
    return out


def link(lay, a, col_a, side_a, b, col_b, side_b, dxa=0, dxb=0, label_a='1', label_b='N'):
    pa = lay.anchor(a, side_a, col_a, dxa)
    pb = lay.anchor(b, side_b, col_b, dxb)
    horizontal = lambda s: s in ('left', 'right')
    if horizontal(side_a) and horizontal(side_b):
        mx = (pa[0] + pb[0]) / 2
        pts = [pa, (mx, pa[1]), (mx, pb[1]), pb]
    elif not horizontal(side_a) and not horizontal(side_b):
        my = (pa[1] + pb[1]) / 2
        pts = [pa, (pa[0], my), (pb[0], my), pb]
    elif horizontal(side_a):
        pts = [pa, (pb[0], pa[1]), pb]
    else:
        pts = [pa, (pa[0], pb[1]), pb]
    d = 'M' + ' L'.join(f'{x:.1f} {y:.1f}' for x, y in pts)
    out = [f'<path d="{d}" fill="none" stroke="#475569" stroke-width="1.5"/>']

    def tag(p, q, text):
        # place the cardinality label near p, pointing toward q
        ox = 8 if q[0] >= p[0] else -8
        oy = -5 if q[1] >= p[1] else 12
        if abs(q[0] - p[0]) < abs(q[1] - p[1]):
            ox = 6
            oy = 14 if q[1] >= p[1] else -6
        anchor = 'start' if ox > 0 else 'end'
        return f'<text x="{p[0] + ox:.1f}" y="{p[1] + oy:.1f}" font-size="12" font-weight="bold" fill="#111" text-anchor="{anchor}">{text}</text>'
    out.append(tag(pts[0], pts[1], label_a))
    out.append(tag(pts[-1], pts[-2], label_b))
    return out


def svg_open(w, h, title, subtitle, ctx):
    return [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" font-family="Liberation Sans, Arial, sans-serif">',
            f'<rect width="{w}" height="{h}" fill="#fff"/>',
            f'<text x="{w / 2}" y="32" text-anchor="middle" font-size="20" font-weight="bold" fill="#111">{esc(ctx.tr(title))}</text>',
            f'<text x="{w / 2}" y="54" text-anchor="middle" font-size="12.5" fill="#555">{esc(ctx.tr(subtitle))}</text>']


def zone(x, y, w, h, area, label, ctx):
    fill, stroke = AREAS[area]
    return [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10" fill="{fill}" fill-opacity="0.35" stroke="{stroke}" stroke-dasharray="6 4"/>',
            f'<text x="{x + 12}" y="{y + 20}" font-size="13.5" font-weight="bold" fill="{stroke}">{esc(ctx.tr(label))}</text>']


def erd(ctx):
    c = [30, 370, 710, 1050, 1390]
    pos = {
        'link_decision': (c[2], 1510), 'sat_status_cache': (c[3], 1510), 'link': (c[0], 940), 'job': (c[0], 1510), 'job_attempt': (c[1], 1510),
        'company': (c[0], 120), 'membership': (c[1], 120), 'app_user': (c[2], 120),
        'audit_entry': (c[1], 330), 'audit_chain_head': (c[2], 480),
        'auth_session': (c[3], 120), 'refresh_token': (c[4], 120), 'auth_event': (c[3], 360),
        'bank_account': (c[0], 740), 'bank_movement': (c[1], 740), 'import_file': (c[2], 740), 'cfdi': (c[3], 740),
        'cfdi_relation': (c[4], 740), 'cfdi_payment': (c[3], 1170), 'cfdi_payment_doc': (c[4], 1170),
    }
    lay = Layout(pos)
    width, height = 1700, 1910
    out = svg_open(width, height, 'As-built data model (migrations M0001 to M0012)',
                   'PK primary key · FK foreign key · UK unique · RLS = forced row-level security by company', ctx)
    out += zone(18, 78, 1002, 570, 'tenancy', 'Tenancy and access', ctx)
    out += zone(1028, 78, 662, 480, 'sessions', 'Sessions (per user, outside company RLS)', ctx)
    out += zone(18, 1470, 1672, 420, 'jobs', 'Jobs, review decisions and SAT cache (forced RLS; restricted queue claims)', ctx)
    out += zone(18, 690, 1672, 750, 'ingestion', 'Ingestion (every table carries company_id and forced RLS)', ctx)
    out += link(lay, 'company', 'id', 'right', 'membership', 'company_id', 'left')
    out += link(lay, 'app_user', 'id', 'left', 'membership', 'user_id', 'right')
    out += link(lay, 'company', 'id', 'bottom', 'audit_entry', 'company_id', 'left')
    out += link(lay, 'app_user', 'id', 'bottom', 'audit_entry', 'actor_id', 'right', dxa=-40)
    out += link(lay, 'app_user', 'id', 'right', 'auth_session', 'user_id', 'left')
    out += link(lay, 'auth_session', 'id', 'right', 'refresh_token', 'session_id', 'left')
    out += link(lay, 'auth_session', 'id', 'bottom', 'auth_event', 'session_id', 'top')
    out += link(lay, 'app_user', 'id', 'bottom', 'auth_event', 'user_id', 'left', dxa=40)
    out += link(lay, 'bank_account', 'id', 'right', 'bank_movement', 'bank_account_id', 'left')
    out += link(lay, 'bank_movement', 'import_file_id', 'right', 'import_file', 'id', 'left', label_a='N', label_b='1')
    out += link(lay, 'import_file', 'id', 'right', 'cfdi', 'import_file_id', 'left')
    out += link(lay, 'cfdi', 'id', 'right', 'cfdi_relation', 'cfdi_id', 'left')
    out += link(lay, 'cfdi', 'id', 'bottom', 'cfdi_payment', 'cfdi_id', 'top')
    out += link(lay, 'cfdi_payment', 'id', 'right', 'cfdi_payment_doc', 'payment_id', 'left')
    out += link(lay, 'bank_movement', 'id', 'left', 'link', 'movement_id', 'right')
    # Route the invoice FK through the free gutter below ingestion, away from table text.
    px, py = lay.anchor('cfdi', 'right', 'id')
    qx, qy = lay.anchor('link', 'bottom')
    out.append(f'<path d="M{px} {py} H1350 V1320 H{qx} V{qy}" fill="none" stroke="#a16207" stroke-width="1.5"/>')
    out.append(f'<text x="360" y="1305" font-size="11" fill="#444">{esc(ctx.tr("link FKs use company_id + id; payment_ref targets cfdi_payment"))}</text>')
    lx, ly = lay.anchor('link', 'right', 'id')
    dx, dy = lay.anchor('link_decision', 'left', 'link_id')
    out.append(f'<path d="M{lx} {ly} H690 V{dy} H{dx}" fill="none" stroke="#b7791f" stroke-width="1.5"/>')
    out += link(lay, 'job', 'id', 'right', 'job_attempt', 'job_id', 'left')
    for y, text in [(1850, 'cfdi_queue_owner: NOLOGIN BYPASSRLS, grants only on job/job_attempt.'),
                    (1870, 'cfdi_app: NOBYPASSRLS; functions pinned and EXECUTE not granted to PUBLIC.')]:
        out.append(f'<text x="730" y="{y}" font-size="12" fill="#222">{esc(ctx.tr(text))}</text>')
    px, py = lay.anchor('cfdi', 'left', 'uuid')
    qx, qy = lay.anchor('sat_status_cache', 'left', 'uuid')
    out.append(f'<path d="M{px} {py} H1020 V{qy} H{qx}" fill="none" stroke="#0891b2" stroke-width="1.5"/>')
    for name in TABLES:
        out += draw_table(name, lay, ctx)
    note = ctx.tr('PK primary key · FK foreign key · UK unique · RLS = forced row-level security by company')
    out.append('</svg>')
    return width, height, out


def node(x, y, w, h, title, sub, fill, stroke, ctx, dashed=False):
    dash = ' stroke-dasharray="6 4"' if dashed else ''
    out = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10" fill="{fill}" stroke="{stroke}" stroke-width="2"{dash}/>',
           f'<text x="{x + w / 2}" y="{y + (22 if sub else h / 2 + 5)}" text-anchor="middle" font-size="14" font-weight="bold" fill="#111">{esc(ctx.tr(title))}</text>']
    if sub:
        words, lines_, cur = ctx.tr(sub).split(' '), [], ''
        for word in words:
            if len(cur) + len(word) + 1 > max(18, int(w / 6.2)):
                lines_.append(cur)
                cur = word
            else:
                cur = (cur + ' ' + word).strip()
        lines_.append(cur)
        for i, line_ in enumerate(lines_):
            out.append(f'<text x="{x + w / 2}" y="{y + 40 + i * 14}" text-anchor="middle" font-size="11" fill="#444">{esc(line_)}</text>')
    return out


def arrow(points, label_lines, lx, ly, ctx, color='#475569', anchor='start'):
    d = 'M' + ' L'.join(f'{x} {y}' for x, y in points)
    out = [f'<path d="{d}" fill="none" stroke="{color}" stroke-width="1.8" marker-end="url(#arr)"/>']
    for i, t in enumerate(label_lines):
        out.append(f'<text x="{lx}" y="{ly + i * 14}" font-size="11" fill="#222" text-anchor="{anchor}">{esc(ctx.tr(t))}</text>')
    return out


def states(ctx):
    width, height = 1300, 1630
    out = svg_open(width, height, 'Status and lifecycle rules of the data', 'import_file.status · auth_session / refresh_token · job.state', ctx)
    out.append('<defs><marker id="arr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">'
               '<path d="M0 0 L10 5 L0 10 z" fill="#475569"/></marker></defs>')
    # panel 1
    out += zone(18, 78, 1264, 400, 'ingestion', 'import_file.status', ctx)
    out.append('<circle cx="70" cy="250" r="9" fill="#111"/>')
    out.append(f'<text x="70" y="278" text-anchor="middle" font-size="11" fill="#222">{esc(ctx.tr("upload received"))}</text>')
    out += node(170, 200, 300, 100, 'queued -> processing', 'pending_original; processing is transaction-local', '#fff7e6', '#c77c00', ctx, dashed=True)
    out += arrow([(80, 250), (170, 250)], [], 0, 0, ctx)
    out += node(900, 110, 230, 66, 'imported', '', '#dcfce7', '#15803d', ctx)
    out += node(900, 215, 230, 66, 'rejected', '', '#fee2e2', '#b91c1c', ctx)
    out += node(900, 320, 230, 66, 'already_imported', '', '#e0f2fe', '#0369a1', ctx)
    out += arrow([(470, 225), (600, 225), (600, 143), (900, 143)], ['valid CFDI or CSV', 'original + sha256 stored, retained_until set'], 610, 118, ctx)
    out += arrow([(470, 250), (900, 250)], ['invalid, hostile or not the company\'s', 'hash kept, bytes not kept'], 610, 228, ctx)
    out += arrow([(470, 275), (600, 275), (600, 353), (900, 353)], ['UUID already imported', 'original_file_id points to the first file'], 610, 328, ctx)
    out.append(f'<text x="36" y="420" font-size="12" fill="#222">{esc(ctx.tr("Once accepted: original, sha256 and retention cannot change or shrink (trigger preserve_import_original)."))}</text>')
    out.append(f'<text x="36" y="440" font-size="12" fill="#222">{esc(ctx.tr("No DELETE privilege and no DELETE route exist."))}</text>')
    # panel 2
    out += zone(18, 500, 1264, 420, 'sessions', 'Session and refresh token', ctx)
    out.append('<circle cx="60" cy="590" r="9" fill="#111"/>')
    out.append(f'<text x="60" y="618" text-anchor="middle" font-size="11" fill="#222">{esc(ctx.tr("login"))}</text>')
    out += node(130, 540, 190, 110, 'Active', 'revoked_at is NULL', '#ede9fe', '#6d28d9', ctx)
    out += arrow([(69, 590), (130, 590)], [], 0, 0, ctx)
    out += node(700, 540, 250, 60, 'Expired', '', '#f1f5f9', '#64748b', ctx)
    out += node(700, 610, 250, 60, 'Revoked', '', '#fee2e2', '#b91c1c', ctx)
    out += arrow([(320, 570), (700, 570)], ['idle 8 h or absolute 30 days (computed, not stored)'], 335, 560, ctx)
    out += arrow([(320, 640), (700, 640)], ['logout, password_changed, reuse_detected'], 335, 630, ctx)
    out += node(130, 780, 230, 70, 'Current token', 'rotated_at is NULL', '#ede9fe', '#6d28d9', ctx)
    out += node(700, 780, 250, 70, 'Rotated token', 'rotated_at set, replaced_by = new token', '#f1f5f9', '#64748b', ctx)
    out += arrow([(360, 815), (700, 815)], ['POST /auth/refresh: rotate'], 375, 805, ctx)
    out += arrow([(950, 800), (990, 800), (990, 835), (950, 835)], [], 0, 0, ctx)
    out.append(f'<text x="700" y="878" font-size="11" fill="#222">{esc(ctx.tr("presented again within the grace window (10 s): 409, nothing changes"))}</text>')
    out += arrow([(825, 780), (825, 672)], [], 0, 0, ctx, color='#b91c1c')
    out.append(f'<text x="837" y="735" font-size="11" fill="#b91c1c">{esc(ctx.tr("presented again after the grace window: session revoked"))}</text>')
    out += zone(18, 940, 1264, 310, 'jobs', 'Durable jobs', ctx)
    out += node(80, 1030, 200, 64, 'queued', '', '#fff7ed', '#c2410c', ctx)
    out += node(430, 1030, 200, 64, 'running', '', '#fff7ed', '#c2410c', ctx)
    out += node(960, 980, 200, 64, 'succeeded', '', '#dcfce7', '#15803d', ctx)
    out += node(960, 1130, 200, 64, 'failed', '', '#fee2e2', '#b91c1c', ctx)
    out += arrow([(280, 1050), (430, 1050)], ['claim + attempt + lease'], 290, 1038, ctx)
    out += arrow([(630, 1050), (790, 1050), (790, 1012), (960, 1012)], ['complete'], 800, 1002, ctx)
    out += arrow([(630, 1072), (790, 1072), (790, 1162), (960, 1162)], ['permanent or exhausted'], 800, 1150, ctx)
    out += arrow([(530, 1094), (530, 1130), (180, 1130), (180, 1094)], ['retry/backoff or expired lease'], 225, 1120, ctx)
    out += arrow([(1060, 1194), (1060, 1225), (100, 1225), (100, 1094)], ['administrator retry (audited)'], 500, 1214, ctx)
    out += zone(18, 1280, 1264, 330, 'matching', 'Links and derived states', ctx)
    out += node(100, 1360, 240, 80, 'suggested', 'heuristics; confidence + criteria', '#fef9c3', '#a16207', ctx)
    out += node(650, 1320, 250, 70, 'reconciled', 'exact payment-complement evidence', '#dcfce7', '#15803d', ctx)
    out += node(650, 1470, 250, 70, 'closed', 'superseded / excess provisional capacity', '#f1f5f9', '#64748b', ctx)
    out += node(1000, 1360, 240, 90, 'rejected', 'group 8 user decision; kept as tombstone', '#fee2e2', '#b91c1c', ctx, dashed=True)
    out += arrow([(340, 1390), (490, 1390), (490, 1355), (650, 1355)],
                 ['later complement: upgrade', 'automatic suggestion in place'], 360, 1325, ctx)
    out += arrow([(220, 1440), (220, 1505), (650, 1505)], ['system closes provisional suggestion'], 270, 1492, ctx)
    out += arrow([(775, 1390), (775, 1450), (340, 1450), (340, 1420)],
                 ['cancelled evidence: review; no capacity'], 390, 1438, ctx, color='#b91c1c')
    out.append(f'<text x="45" y="1582" font-size="12" fill="#222">{esc(ctx.tr("movement_state / cfdi_state / cfdi_payable / cfdi_review_reasons: derived, company-scoped reads."))}</text>')
    out.append('</svg>')
    return width, height, out


def security(ctx):
    order = ['company', 'membership', 'app_user', 'audit_entry', 'audit_chain_head', 'auth_session', 'refresh_token', 'auth_event',
             'import_file', 'cfdi', 'cfdi_relation', 'cfdi_payment', 'cfdi_payment_doc', 'bank_account', 'bank_movement', 'link', 'link_decision', 'job', 'job_attempt', 'sat_status_cache']
    cols = [('Table', 180), ('Area', 110), ('Forced RLS', 100), ('Privileges of role cfdi_app', 430), ('Immutability / triggers', 380)]
    width = sum(w for _, w in cols) + 40
    row_h = 30
    height = 150 + row_h * (len(order) + 1)
    out = svg_open(width, height, 'Row security, privileges and immutability',
                   'S = SELECT · I = INSERT · U(columns) = UPDATE only on the named columns · no table grants DELETE', ctx)
    y = 80
    x = 20
    cx = x
    for title, w in cols:
        out.append(f'<rect x="{cx}" y="{y}" width="{w}" height="{row_h}" fill="#1f3864"/>')
        out.append(f'<text x="{cx + 10}" y="{y + 20}" font-size="12.5" font-weight="bold" fill="#fff">{esc(ctx.tr(title))}</text>')
        cx += w
    for i, name in enumerate(order):
        area, rls, _ = TABLES[name]
        fill, stroke = AREAS[area]
        ry = y + row_h * (i + 1)
        cx = x
        values = [name, ctx.tr(area), ctx.tr('yes') if rls else ctx.tr('no (per user)') if area == 'sessions' or name == 'app_user' else ctx.tr('no'),
                  ctx.tr(GRANTS[name]), ctx.tr(NOTES.get(name, '')) if name in NOTES else '']
        for (title, w), v in zip(cols, values):
            bg = fill if title == 'Area' else ('#fff4e0' if title == 'Forced RLS' and rls else '#fff')
            out.append(f'<rect x="{cx}" y="{ry}" width="{w}" height="{row_h}" fill="{bg}" stroke="#cbd5e1"/>')
            weight = ' font-weight="bold"' if title == 'Table' else ''
            out.append(f'<text x="{cx + 10}" y="{ry + 20}" font-size="12"{weight} fill="#111">{esc(v)}</text>')
            cx += w
    out.append('</svg>')
    return width, height, out


def write(name, svg_lines, lang_suffix):
    OUT.mkdir(parents=True, exist_ok=True)
    svg_path = OUT / f'{name}{lang_suffix}.svg'
    svg_path.write_text('\n'.join(svg_lines), encoding='utf-8')
    print(f'wrote {svg_path.relative_to(ROOT)}')
    magick = shutil.which('magick')
    if magick:
        png_path = OUT / f'{name}{lang_suffix}.png'
        subprocess.run([magick, '-density', '110', str(svg_path), str(png_path)], check=True)
        print(f'wrote {png_path.relative_to(ROOT)}')
    else:
        print('ImageMagick not found: only the SVG was written')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--lang', choices=['en', 'es'], default='en')
    args = parser.parse_args()
    ctx = Ctx(args.lang)
    suffix = '-es' if args.lang == 'es' else ''
    write('database-erd-implemented', erd(ctx)[2], suffix)
    write('database-states-implemented', states(ctx)[2], suffix)
    write('database-security-implemented', security(ctx)[2], suffix)


if __name__ == '__main__':
    main()
