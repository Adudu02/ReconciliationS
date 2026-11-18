# Guía de uso para clientes y administradores

[English version](user-guide.en.md) · [Instalación del servidor (inglés)](server-installation.md)

## 1. Qué hace el producto

Importa XML de CFDI 4.0 y movimientos bancarios desde un CSV compatible.\
Organiza la información en un espacio de trabajo por empresa.\
Propone vínculos entre facturas y movimientos con criterios explicables.\
Permite revisar las sugerencias y consultar saldos, complementos y alertas.\
Conserva un registro de auditoría de las acciones correspondientes.

**“Demostración: los resultados no tienen validez fiscal ni legal.”**
Utilice únicamente datos sintéticos o anonimizados. El servidor de demostración
consulta un SAT simulado por defecto; una alerta no sustituye una verificación fiscal.

## 2. Roles y permisos

Los permisos corresponden a la membresía en la empresa seleccionada. El operador
de plataforma crea empresas; **no obtiene acceso automático a sus datos**.
Si también tiene una membresía, se aplican los permisos de ese rol.

| Acción | Operador sin membresía | Administrador | Contador | Lector |
|---|---|---|---|---|
| Consultar resumen, facturas, movimientos y revisión | No | Sí | Sí | Sí |
| Consultar un reporte de importación, originales o un trabajo por ID (API) | No | Sí | Sí | Sí |
| Cargar importaciones | No | Sí | Sí | No |
| Ejecutar conciliación | No | Sí | Sí | No |
| Confirmar, rechazar o cambiar vínculos sugeridos | No | Sí | Sí | No |
| Clasificar movimientos | No | Sí | Sí | No |
| Solicitar consulta SAT (API) | No | Sí | Sí, sin `anonymizedUuid` | No |
| Listar trabajos fallidos / reintentarlos (API) | No | Sí | No | No |
| Consultar configuración (API) y miembros (Equipo/API) | No | Sí | Sí | Sí |
| Cambiar configuración (API) o agregar miembros (Equipo/API) | No | Sí | No | No |
| Archivar empresa (API) | No | Sí | No | No |
| Crear empresa (API) | Sí | Solo si también es operador | Solo si también es operador | Solo si también es operador |
| Consultar auditoría y verificar su cadena (API) | No | Sí | Sí | Sí |
| Cambiar contraseña propia (Mi cuenta) | Sí | Sí | Sí | Sí |

“Administrador”, “Contador” y “Lector” son los nombres que muestra la interfaz.
Las empresas archivadas muestran “Archivada · solo lectura”: se pueden consultar,
pero no admiten nuevas importaciones, conciliaciones, decisiones, clasificaciones,
cambios de configuración/miembros ni reintentos. No se ofrece una acción de
restauración de empresa en la interfaz.

Fuentes de permisos: [CompanyRoleHandler.cs](../../src/Reconciliation.Core/Tenancy/CompanyRoleHandler.cs),
[SessionState.cs](../../src/Reconciliation.Web/SessionState.cs), y los archivos
`*Endpoints.cs` de Tenancy, Ingestion, Matching, Jobs, Sat y Audit en
[`src/Reconciliation.Core`](../../src/Reconciliation.Core).

## 3. Iniciar sesión, idioma y empresa

1. Abra en su navegador la URL que le proporcionó el responsable del servidor.
   En el segundo equipo se utiliza la dirección del servidor, no `localhost`.
2. En “Iniciar sesión”, capture “Correo electrónico” y “Contraseña”; seleccione
   “Iniciar sesión”. Utilice una cuenta previamente habilitada.
3. El idioma inicial es español de México. Los botones “ES” y “EN” permiten
   cambiar a “Español” o “English”. El idioma no se guarda: una recarga vuelve
   al idioma inicial de la URL; `?lang=en` inicia en inglés y el valor predeterminado es español.
4. En “Elija una empresa”, seleccione una de sus membresías. El encabezado
   muestra el nombre de la empresa y su rol. Use “Cambiar de empresa” para elegir otra.
5. Use “Cerrar sesión” al terminar. Si no hay membresías, solicite acceso a un
   administrador. Que un operador vea el selector vacío no indica un error.

## 4. Pantallas

### 4.1 Resumen

“Resumen” abre “Resumen de la empresa”. Presenta cantidades y montos de
“Facturas” y “Movimientos” por “Estado”, además de “Alertas de facturas”:
“Complemento de pago vencido”, “Cancelación en proceso”, “Alerta EFOS” y “Factura global”.

Los montos de facturas usan totales brutos de CFDI de ingreso importados; los de
movimientos suman abonos y cargos. Los movimientos clasificados y las facturas
sin saldo por pagar permanecen en los totales. No interprete esos totales como
el saldo pendiente de cobro; consulte el detalle de cada factura.

### 4.2 Importar archivos

Disponible para contadores y administradores de una empresa activa.

1. Abra “Importar archivos” y seleccione “Tipo de importación”.
2. Para CFDI, elija “Archivos XML de CFDI” y seleccione XML de CFDI 4.0.
   Para banco, elija “Estado de cuenta bancario en CSV”, seleccione un solo CSV
   del “Formato de banco compatible” y capture “Referencia de cuenta”. No se
   admite cualquier formato bancario; use el formato de referencia del servidor.
3. En “Archivos”, seleccione los archivos y pulse “Cargar archivos”.
   El límite es **20 archivos, 10 MiB por archivo y 20 MiB en total**; la API puede
   tener límites menores. El modo bancario acepta un solo archivo.
4. Espere el resultado del trabajo y revise “Reporte por archivo”. Las columnas
   muestran “Archivo”, “Estado”, “Detalles” y “Movimientos”; se informan los
   movimientos importados y duplicados. Los motivos enviados por el servidor
   pueden aparecer en inglés.

| Estado del archivo | Significado |
|---|---|
| “Procesando” | El archivo todavía está en proceso. |
| “Importado” | Se procesó la importación del archivo; revise también los conteos. |
| “Ya importado” | El archivo ya se había importado; no se duplica la importación. |
| “Rechazado” | El archivo no se aceptó; revise “Detalles” antes de corregirlo y volver a cargarlo. |

Use “Abrir el resumen para ejecutar la conciliación” cuando termine.
La importación por sí sola no equivale a una decisión de conciliación.
Un lector no ve esta opción en el menú; una visita directa muestra el aviso de solo lectura.

### 4.3 Facturas

“Facturas” presenta las facturas de ingreso importadas. Filtre por “Estado”:
“Todos los estados”, “Pendiente”, “Sugerido” o “Conciliado”. En “Estado del complemento”,
elija “Todos los complementos”, “Faltante (incluye vencido)” o “Vencido” y pulse
“Aplicar filtros”. Use “Cargar más facturas” cuando esté disponible.

La lista incluye UUID, emisión, dirección, estado, total, pendiente y vencimiento
del complemento. Abra el UUID para consultar el detalle. El filtro de pendientes
excluye saldos pendientes en cero. Los vencimientos de complementos se calculan
para pagos PPD confirmados; “Faltante” incluye los que ya vencieron.

### 4.4 Resumen de la factura

El detalle “Resumen de la factura” presenta UUID, dirección y método de pago.
En “Montos” se muestran “Total de la factura”, “Notas de crédito aplicadas”,
“Neto por cobrar”, “Cobrado confirmado”, “Porcentaje cobrado”, “Pendiente” y
“No cubierto por vínculos”.

**“Los vínculos sugeridos no cuentan para los montos cobrados confirmados.”**
“Pendiente” descuenta lo confirmado; “No cubierto por vínculos” descuenta también
las sugerencias activas. Por eso ambos importes pueden ser distintos.

“Estado y motivos” presenta el estado SAT, la cancelación, la alerta EFOS y los
motivos marcados como “requiere revisión” o “informativo”. “Movimientos vinculados”
presenta monto aplicado, estado, confianza y “Complemento de pago”, con vencimiento,
CFDI de pago y forma/fecha de pago cuando existen. Una advertencia recibida del
servidor puede mantenerse en inglés. No hay un botón de consulta SAT en esta pantalla.

### 4.5 Movimientos

“Movimientos” muestra “Abono”, “Cargo”, “Estado” y “Clasificación”. Filtre con
“Todos los estados (incluye clasificados)”, “Pendiente (sin clasificar)”, “Sugerido”
o “Conciliado”; pulse “Aplicar filtro”. Use “Cargar más movimientos” si aparece.

Para clasificar, seleccione “Clasificar”, elija “Préstamo”, “Aportación de socios”,
“Pago de impuestos”, “Facturación tardía” u “Otro”; agregue “Nota (opcional)” y pulse
“Guardar clasificación”. “Quitar clasificación” elimina la clasificación al guardar;
“Cancelar clasificación” descarta la edición. Clasificar es una acción distinta
de confirmar un vínculo y no lo confirma. Los clasificados se excluyen del filtro
“Pendiente (sin clasificar)”, pero siguen en los totales.

### 4.6 Bandeja de revisión

“Bandeja de revisión” ordena las sugerencias por confianza y después muestra
facturas señaladas. Revise fecha, monto, factura, criterios, puntos y motivos.
“Resumen de la factura” abre el detalle. Use “Actualizar bandeja” o
“Cargar más registros” para consultar los registros disponibles.

- “Confirmar” acepta el vínculo sugerido como conciliado.
- “Rechazar” rechaza la sugerencia; no registra un cobro confirmado.
- “Cambiar” abre “Factura destino”, “Monto aplicado” y “Nota de la decisión (opcional)”.
  Seleccione una factura y un monto positivo con hasta dos decimales; pulse
  “Guardar cambio” o “Cancelar cambio”. Use “Cargar más facturas” si necesita otra página.

Las acciones se muestran en sugerencias con vínculo, no en toda factura señalada.
Si otra persona ya decidió la sugerencia, actualice la bandeja; puede recibir 409.
No hay una acción de reapertura ni de creación manual independiente de vínculos en la interfaz.

### 4.7 Ejecución de conciliación

En “Resumen”, utilice “Ejecutar conciliación”, dentro de “Ejecución de conciliación”.
Se usan las importaciones y la configuración vigente de la empresa. “Trabajo”
muestra identificador, intento y “Progreso”; espere el estado final antes de revisar.

| Estado del trabajo | Significado |
|---|---|
| “En cola” | El trabajo espera su ejecución. |
| “En ejecución” | El servidor está procesando el trabajo. |
| “Completado” | Terminó correctamente; consulte los resultados. |
| “Fallido” | No terminó correctamente; revise el mensaje y el reporte por archivo cuando exista. |

La interfaz consulta el progreso periódicamente. Un trabajo completado puede dejar
sugerencias para revisión humana; no significa que todas las facturas estén conciliadas.

### 4.8 Equipo

“Equipo” muestra el correo electrónico y el rol de los miembros de la empresa
seleccionada. Los contadores y lectores pueden consultar la lista; en una empresa
archivada todos ven el equipo de solo lectura.

Un administrador de una empresa activa puede usar “Agregar miembro”: capture
“Correo electrónico”, elija “Rol” y, para una cuenta nueva, proporcione
“Contraseña inicial (solo para cuentas nuevas)” de al menos 12 caracteres.
“Mostrar contraseña” / “Ocultar contraseña” controla su visibilidad. Comparta la
contraseña inicial de forma segura; no la envíe por una URL ni por un canal público.
Si la cuenta ya existe, conserva su contraseña y el campo puede dejarse vacío.
Después de enviar, el campo se vacía incluso si la operación falla.

La pantalla no permite eliminar miembros ni editar roles. Si el correo ya aparece
en la lista, se informa que el usuario ya pertenece a la empresa y no se cambia
su rol. Un usuario agregado podrá iniciar sesión y elegir la empresa.
No se envían invitaciones ni se exige cambiar la contraseña en el primer acceso.

### 4.9 Mi cuenta

“Mi cuenta” está en el encabezado y está disponible incluso sin una empresa
seleccionada. En “Cambiar contraseña”, capture “Contraseña actual”,
“Contraseña nueva” y “Confirmar contraseña nueva”. La contraseña nueva debe tener
al menos 12 caracteres y coincidir con la confirmación.

Al enviar se vacían los campos. Si se cambia correctamente, aparece
“La contraseña se cambió. Inicie sesión de nuevo.” y se vuelve al inicio de sesión.
Inicie sesión con la nueva contraseña. Se revocan las sesiones de renovación;
los tokens de acceso ya emitidos conservan su vigencia hasta expirar.
Si no se puede verificar la contraseña actual, revísela o inicie sesión de nuevo.
No se ofrece recuperación ni restablecimiento de contraseña en esta pantalla.

## 5. Estados y confianza

| Etiqueta | Interpretación |
|---|---|
| “Pendiente” | Estado derivado sin cobertura confirmada completa ni sugerencia computable. Puede existir un pago parcial confirmado; consulte los montos. Facturas canceladas o sin neto por cobrar también pueden conservar este estado y no aparecen necesariamente en el filtro de pendientes. |
| “Sugerido” | Hay un vínculo propuesto para revisión; no se cuenta como cobrado confirmado. Un movimiento también puede mostrar este estado por un vínculo retenido debido a cancelación. |
| “Conciliado” | Los vínculos conciliados cubren el neto por cobrar de la factura o el importe del movimiento. No certifica validez fiscal ni garantiza que se haya recibido el complemento. |
| “Recibido” | Se dispone del complemento de pago correspondiente al vínculo. |
| “Faltante” | Falta el complemento; consulte si existe una fecha de vencimiento. |
| “Vencido” | Falta el complemento y ya pasó su vencimiento calculado; no significa que la factura esté vencida. |
| “Alta”, “Media”, “Baja” | Niveles de confianza del vínculo, no porcentajes de cobro ni garantías. Revise los criterios y motivos incluso cuando la confianza sea alta. |

## 6. Ejemplo de trabajo para un contador

1. Inicie sesión y elija una empresa activa con rol “Contador” o “Administrador”.
2. Importe los XML sintéticos y después el CSV bancario compatible. Revise cada reporte.
3. En “Resumen”, pulse “Ejecutar conciliación” y espere “Completado”.
4. Abra “Bandeja de revisión”, examine una sugerencia y su factura; pulse “Confirmar”
   solo cuando el vínculo corresponda al pago. Deje sin confirmar las demás sugerencias
   hasta revisarlas.
5. Consulte “Resumen de la factura” y compare lo confirmado con lo pendiente.

El [ejemplo documentado](../../README.md#review-api-and-summaries-8a) describe una factura
de **11,600.00**, con **5,800.00 confirmados** y **5,800.00 sugeridos**: muestra
**50% cobrado**, **5,800.00 pendientes** y cero no cubierto por vínculos.
Son cifras de ese ejemplo, no resultados garantizados para cualquier archivo.
El complemento faltante/vencido se evalúa por separado de la cobertura del cobro.

## 7. Administración mediante API

> **Operaciones que siguen sin página:** crear empresas, modificar configuración
> y archivar requieren API. “Equipo” permite consultar y agregar miembros; “Mi cuenta”
> permite cambiar la contraseña propia. No hay páginas de administración de trabajos,
> auditoría ni SAT. El cliente ve las empresas de sus membresías y las acciones de su rol.

Los siguientes ejemplos usan curl. Sustituya los marcadores; el token debe pertenecer
al rol indicado. Use la URL proporcionada por el responsable del servidor.
La creación de empresa requiere un **operador**, no solamente un administrador:

```bash
BASE=http://localhost:8080
OPERATOR_TOKEN='<accessToken del operador>'
curl -fsS -X POST "$BASE/companies" \
  -H "Authorization: Bearer $OPERATOR_TOKEN" -H 'Content-Type: application/json' \
  -d '{"rfc":"AAA010101AAA","name":"Synthetic demo company","adminEmail":"admin@example.invalid","adminPassword":"<administrator-password>"}'
```

Obtenga el token con `POST /auth/tokens` y `{email,password}`, como indica la
[instalación](server-installation.md#first-start). Un administrador nuevo requiere
contraseña de al menos 12 caracteres; si el correo ya existe, conserva su contraseña.

### 7.1 Miembros

Use “Equipo” para agregar por correo. La operación equivalente de API requiere
un token de administrador de una empresa activa:

```bash
ADMIN_TOKEN='<accessToken del administrador>'
COMPANY_ID='<UUID de la empresa>'
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/members" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d '{"email":"accountant@example.invalid","role":"accountant","initialPassword":"<initial-password>"}'
```

La cuenta nueva requiere al menos 12 caracteres de contraseña inicial; una cuenta
existente conserva su contraseña, aunque se envíe `initialPassword`. Los valores
de `role` son `administrator`, `accountant` y `viewer`. No se envían invitaciones.
Debe suministrarse exactamente uno de `email` o `userId`.

Agregar miembros por correo revela a los administradores si la cuenta existe: una cuenta nueva requiere contraseña inicial y una existente no.
Al igual que en `POST /companies`, se puede vincular una cuenta existente a la empresa sin el consentimiento de su titular; no se requiere invitación ni aceptación.

La forma anterior `{userId,role}` sigue disponible para usuarios existentes:

```bash
USER_ID='<UUID de un usuario existente>'
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/members" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d "{\"userId\":\"$USER_ID\",\"role\":\"accountant\"}"
```

En ambas formas, la API conserva la semántica anterior: repetir la misma membresía
es una operación sin cambios ni una nueva entrada de auditoría; enviar otro rol
actualiza la membresía y registra el cambio, sin permitir quitar el último
administrador. La pantalla “Equipo” solo ofrece agregar, no editar roles.
`GET /companies/{companyId}/members` devuelve `{userId,email,role}` únicamente
para miembros de esa empresa. Las cuentas nuevas registran `account.created`;
las membresías registran `member.added` o `member.changed`, sin contraseñas ni hashes.

### 7.2 Configuración

Con las variables anteriores, modifique solo los campos necesarios:

```bash
curl -fsS -X PATCH "$BASE/companies/$COMPANY_ID/settings" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID" \
  -H 'Content-Type: application/json' \
  -d '{"dateToleranceDays":3,"amountTolerance":0.50,"complementNextBizDay":true}'
```

Son valores de ejemplo: tolerancia de fechas de 0–366 días; tolerancia monetaria
absoluta de 0–1.00 con hasta dos decimales; `complementNextBizDay` controla el ajuste
al siguiente día hábil cuando el vencimiento calculado cae en fin de semana.
Consulte lo vigente con `GET /companies/{companyId}/settings`, con los mismos
encabezados. El cliente no ve un formulario de configuración; las siguientes
conciliaciones usan los ajustes vigentes.

### 7.3 Archivar

Esta llamada vuelve la empresa de solo lectura; no es una eliminación:

```bash
curl -fsS -X POST "$BASE/companies/$COMPANY_ID/archive" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H "X-Company-Id: $COMPANY_ID"
```

El cliente conserva acceso de lectura y ve “Archivada · solo lectura” al cargar
nuevamente la empresa. No hay una acción de desarchivar documentada.
Las rutas, campos y restricciones están en
[CompanyEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyEndpoints.cs) y
[CompanyAdminEndpoints.cs](../../src/Reconciliation.Core/Tenancy/CompanyAdminEndpoints.cs).

## 8. Sesión y recarga

El token de acceso permanece **en memoria**; no se guarda en `localStorage`,
`sessionStorage` ni en la URL. Con la configuración predeterminada
`Session__BrowserRefreshCookie=false`, la interfaz no conserva el token de renovación
JSON. **Al recargar debe iniciar sesión otra vez.** Cuando vence el acceso y falla
la renovación, también se solicita iniciar sesión.

Solo con el transporte de cookie de renovación habilitado y una cookie válida se
puede renovar silenciosamente después de recargar; luego se vuelve a elegir empresa.
El servidor debe permitir el origen exacto del navegador. La cookie es HttpOnly,
SameSite=Strict y de ruta `/auth`; se marca Secure en HTTPS para el transporte del navegador.
HTTPS es necesario fuera de la demostración. La configuración global heredada
`Session__RefreshCookie` tiene precedencia y no es la configuración predeterminada.

Un 401 provoca un intento coordinado de renovación y un reintento de la solicitud;
si fallan, se vuelve al inicio de sesión. Las pestañas no comparten el token en memoria.
Véanse [ADR-004](../adr/ADR-004-session-renewal.md),
[ADR-010](../adr/ADR-010-browser-ui-and-api-contract.md) y
[ApiClient.cs](../../src/Reconciliation.Web/ApiClient.cs).

## 9. Preguntas frecuentes y mensajes

| Mensaje o situación | Qué hacer |
|---|---|
| “No hay membresías de empresa disponibles. Solicite acceso a un administrador de la empresa.” | Solicite una membresía; un operador sin membresías también ve este mensaje. |
| “Espacio de trabajo de solo lectura. Un contador o un administrador puede decidir sobre las sugerencias en una empresa activa.” | Verifique el rol y si la empresa está archivada; un lector no puede decidir. |
| “Este espacio de trabajo es de solo lectura. Los contadores y administradores pueden importar a una empresa activa.” | Solicite al administrador el acceso adecuado; no es un fallo de carga. |
| 401 / “Su sesión ha terminado. Inicie sesión de nuevo.” | Inicie sesión otra vez y seleccione empresa. |
| 403 | Verifique rol, empresa y configuración del origen si utiliza cookies de renovación. |
| 409 al decidir | Otra decisión o un cambio de estado puede haber ocurrido; actualice antes de intentar de nuevo. |
| 413 / “La carga es demasiado grande. Reduzca su tamaño e intente de nuevo.” | Reduzca archivos/tamaño respetando los límites; el servidor puede tener límites menores. |
| 429 / “Demasiadas solicitudes. Espere un momento e intente de nuevo.” | Espere antes de repetir. |
| “El servicio tuvo un problema al completar la solicitud. Intente de nuevo más tarde.” | Es el encabezado genérico de error del servidor; intente más tarde y avise al responsable si persiste. |
| Detalle en inglés bajo un encabezado en español | Es texto original de la API que no se traduce. Los errores 5xx usan el encabezado genérico sin mostrar el cuerpo crudo. No implica que haya cambiado su idioma. |

Fuente de los textos: [Catalog.Es.cs](../../src/Reconciliation.Web/Localization/Catalog.Es.cs)
y [ErrorText.razor](../../src/Reconciliation.Web/Shared/ErrorText.razor).
