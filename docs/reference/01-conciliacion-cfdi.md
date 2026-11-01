# Proyecto 1: Plataforma de conciliación de CFDI y estados de cuenta

## 1. Descripción
Aplicación web multi-empresa que recibe facturas electrónicas (CFDI 4.0, XML) y estados de cuenta bancarios (CSV y, después, PDF), y propone qué movimiento bancario corresponde a qué factura. Muestra lo conciliado, lo pendiente y los casos dudosos para revisión humana, y conserva una bitácora de auditoría.

**Usuarios:** contadores de despachos y encargados administrativos de pymes que hoy concilian en Excel.

## 2. Propósito
- **Problema:** conciliar cientos de comprobantes contra movimientos bancarios a mano es lento y propenso a error. Se pasan por alto pagos parciales, complementos de pago, notas de crédito y facturas canceladas.
- **Por qué importa:** es un dolor reconocible para cualquier empresa mexicana, con reglas de negocio reales.
- **Qué demuestra del perfil mid-senior:** modelado de un dominio con reglas, multi-tenant con aislamiento a nivel de base de datos, idempotencia, auditoría, seguridad al procesar XML, observabilidad y despliegue reproducible en AWS.

## 3. Ejemplo (datos sintéticos)
Empresa ficticia «Comercial Ejemplo SA de CV», RFC `AAA010101AAA`.

**Entrada A: CFDI de ingreso**
| Campo | Valor |
|---|---|
| UUID | `00000000-0000-4000-8000-000000000001` |
| Emisor → Receptor | `AAA010101AAA` → `XEXX010101000` |
| Total | $11,600.00 MXN |
| Fecha | 2026-10-02 |

**Entrada B: CFDI de pago** (complemento de pagos 2.0) que relaciona el UUID anterior por $5,800.00 el 2026-10-10.

**Entrada C: estado de cuenta (CSV)**
```
fecha,concepto,abono,cargo,saldo
2026-10-10,SPEI REC 0034 PAGO FACT 0001,5800.00,,125300.00
2026-10-20,SPEI REC 0071 XEXX COMERCIAL,5800.00,,131100.00
```

**Salida esperada**
- Movimiento 1 → CFDI de pago B → factura A: **conciliado** (pago parcial, saldo pendiente $5,800.00).
- Movimiento 2 → sin CFDI de pago: **sugerido** contra factura A por monto y concepto, con estado «por revisar» hasta recibir el complemento.
- Resumen de la factura A: pagada 50 %, pendiente $5,800.00, con ambos movimientos vinculados y registrados en la auditoría.

## 4. Alcance
**MVP:** ingesta idempotente de CFDI y CSV de 2 o 3 bancos; motor de reglas (monto, fecha, RFC, referencia); pagos parciales, complementos y notas de crédito; multi-tenant con roles; bitácora de auditoría; tablero de revisión; exportación a Excel; consulta de estatus ante el SAT con caché; despliegue en AWS con Terraform.

**Fuera de alcance:** timbrado/emisión de CFDI, contabilidad electrónica, integración bancaria en vivo, pagos, aplicación móvil.

## 5. Referencias
| Referencia | Para qué sirve |
|---|---|
| [Anexo 20, guía de llenado de CFDI (SAT)](http://omawww.sat.gob.mx/tramitesyservicios/Paginas/documentos/Anexo_20_Guia_de_llenado_CFDI.pdf) | Estructura y reglas del CFDI 4.0. El buscador devolvió copias que podrían no ser la revisión vigente: **por verificar** en el portal del SAT. |
| [Factura de recepción de pagos (SAT)](http://omawww.sat.gob.mx/tramitesyservicios/Paginas/recepcion_de_pagos.htm) | Complemento de pagos 2.0: vigencia, revisiones del documento técnico y plazos de emisión. |
| [Guía de llenado del complemento de pagos (SAT)](http://omawww.sat.gob.mx/tramitesyservicios/Paginas/documentos/Guiallenadopagos311221.pdf) | Reglas para relacionar pagos con facturas. Fecha de 2021: **por verificar** contra la guía más reciente. |
| [Consulta de estatus de CFDI (SAT, PDF)](https://www.sat.gob.mx/minisitio/Factura/documentos/cancelacion/consulta_cfdi.pdf) | Servicio web para saber si un CFDI está vigente o cancelado. Versión 1.3 (2020); existe una más reciente: **por verificar**. |
| [phpcfdi/sat-estado-cfdi](https://packagist.org/packages/phpcfdi/sat-estado-cfdi) | Cliente open source de ese servicio; útil para entender parámetros y respuestas. |
| [PostgreSQL: Row Security Policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html) | Aislamiento por empresa (tenant) en la base de datos. |
| [OWASP: XXE Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/XML_External_Entity_Prevention_Cheat_Sheet.html) | Procesar XML de terceros sin exponer archivos locales ni SSRF. |
| [OpenTelemetry: documentación](https://opentelemetry.io/docs/) | Trazas, métricas y logs. |
| [Terraform: backend S3](https://developer.hashicorp.com/terraform/language/backend/s3) | Estado remoto con bloqueo (`use_lockfile`). |
| [Terraform Registry: aws_ecs_service](https://registry.terraform.io/providers/hashicorp/aws/latest/docs/resources/ecs_service) | Servicio en ECS Fargate. **Por verificar:** la búsqueda no devolvió esta página oficial. |
| [Amazon Builders' Library: reintentos con backoff y jitter](https://aws.amazon.com/builders-library/timeouts-retries-and-backoff-with-jitter/) | Reintentos al consultar el SAT sin saturarlo. |

## 6. Preguntas abiertas (planeación profunda)
- ¿Qué bancos y formatos de CSV/PDF se soportan primero? ¿Cómo se diseña un parser por banco extensible?
- Modelo de datos: ¿cómo se representa la relación N:M entre movimientos y CFDI (pagos parciales, un pago a varias facturas)?
- Reglas de conciliación: ¿puntuación por coincidencia o reglas en orden de prioridad? ¿Cómo se prueba (propiedades, datos sintéticos)?
- Multi-tenant: ¿un esquema compartido con RLS o esquemas por empresa? Registrar la decisión en un ADR.
- ¿Qué se guarda del XML original y por cuánto tiempo? Requisitos de cifrado y retención.
- Estatus ante el SAT: límites de tasa, reintentos, qué hacer si el servicio está caído.
- Stack definitivo (TypeScript/NestJS propuesto) y estrategia de pruebas E2E.
- Generación del conjunto sintético: ¿cuántos CFDI y movimientos para pruebas de carga realistas?
- Verificar con la fuente oficial la versión vigente del Anexo 20, del complemento de pagos y del servicio de consulta.

## 7. Dependencias con otros proyectos
- **Proyecto 2:** la ingesta y la conciliación masiva se ejecutan como tareas; el contrato de reintentos e idempotencia debe estar definido antes.
- **Proyecto 3:** expone al proyecto 1 la extracción de PDFs y las sugerencias de casos ambiguos; el proyecto 1 define los esquemas de entrada y salida.
