# Explore — Stage 19: Facturación electrónica ARCA

Fecha: 2026-08-21. Fase ejecutada por sdd-explore (sonnet) bajo mandato autónomo; contenido
persistido verbatim por el orquestador (el agente de fase no tenía Write en su toolset).
Restricción vinculante del mandato: el programa llega hasta donde se pueda SIN las
credenciales/certificados del dueño (homologación/mocks) y documenta el corte con precisión —
las credenciales no se piden, se deja la etapa lista para consumirlas el día que existan.

## Current State

**El legado nunca facturó fiscalmente — confirmado por lectura directa.** `alsina/ticket.php:16-30`
es el único punto de "facturación": una función `imprimir()` que llama `window.print()` sobre HTML
armado desde `$_SESSION`. No hay ninguna llamada SOAP, ningún manejo de certificado, ningún CAE en
todo el árbol `alsina/`. Esto confirma textualmente doc-11:410-411 ("`ticket.php` es un
`window.print()`").

**La capa fiscal del schema está construida pero dormida — parcialmente completa.**
- `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante` (con `es_fiscal`, `letra`,
  `discrimina_iva`, `codigo_afip`) existen y tienen RLS desde la Etapa 1
  (`docs/10-modelo-de-datos.md:69-92`).
- La regla de letra A/B/C está implementada, pura, **exhaustivamente testeada y sin ningún
  caller** — `src/Ways.Domain/Ventas/ResolvedorDeLetraComprobante.cs:16-22` documenta
  explícitamente que es *"dormant: el POS de esta etapa solo emite TX/NCX"*.
- `ServicioDeVentas.ResolverTipoComprobanteAsync` bloquea cualquier `EsFiscal == true` con un 400
  (`src/Ways.Application/Ventas/ServicioDeVentas.cs:1162`) — el camino fiscal está cerrado a
  propósito, no por omisión.
- **Falta:** `empresas.id_condicion_fiscal` — `Empresa.cs:9-17` solo tiene `Cuit`, no
  `IdCondicionFiscal` (a diferencia de `Cliente.cs:44` y `Proveedor.cs:27`, que sí lo tienen).
  `docs/10` lo pide (línea 143) pero la entidad no lo implementó — backlog explícito en
  `docs/11-programa-post-paridad.md:475`.
- **Falta:** `puntos_venta.numero` fiscal real — `PuntoVenta.cs:9-24` solo expone `Id` (el PK
  interno, generado por identity). El `PPPP` que hoy se muestra en el comprobante **es el id
  interno**, no un número de punto de venta asignado por ARCA (`docs/11:474`,
  `docs/09-multi-tenancy.md:143-145`).
- **Falta:** campos de resultado del comprobante — CAE, vencimiento de CAE, resultado,
  observaciones. `comprobantes_venta` (`docs/10:344-359`) no tiene ninguna columna fiscal de
  respuesta.
- **Numeración:** `numeraciones_comprobante` (`docs/09:131-137`) es un contador atómico por
  `(id_punto_venta, tipo_comprobante)` — clave, no ARCA. `AsignadorDeNumeroComprobante.cs` toma el
  número **antes** de la transacción principal, en su propia transacción chica, y es reusado sin
  cambios desde la Etapa 5 por `'PRES'`, `'REM'`, `'TXR'`. Esto es exactamente el mecanismo que
  entra en tensión con la numeración fiscal (ver Decisión 2 abajo): ARCA numera por **punto de
  venta fiscal**, no por `id_punto_venta` interno.

**El TXR de la Etapa 17 quedó diferido explícitamente a la Etapa 19 — arbitraje registrado.**
`openspec/changes/archive/2026-08-21-stage-17-presupuestos-y-remitos/proposal.md`:
- Decisión 7 (línea 440): *"Consolidated invoicing ships in this stage, non-fiscal, as an ITEMLESS
  comprobante of a new tipo `TXR` that writes ZERO stock."*
- Línea 74: *"The **fiscal** type of that consolidation stays deferred to Etapa 19 (doc-11:373)."*
- Línea 480-482: *"**Cost of reversing.** Etapa 19 replacing `TXR` with a fiscal type is additive:
  existing `TXR` comprobantes stay valid history, and the consolidation path gains a type
  parameter."* — restricción de diseño explícita para la Etapa 19: no se puede *reemplazar* `TXR`,
  solo agregar un tipo fiscal paralelo; `TXR` queda como registro legítimo.
- Open question 7 (línea 1297-1301): la alternativa evaluada y rechazada fue *"shipping remitos
  that cannot be invoiced until Etapa 19"* — la existencia de `TXR` es la prueba de que el dueño
  ya aceptó el trade-off.
- `TXR` en el padrón: `letra 'X'`, `es_fiscal false`, `afecta_stock false`, `signo +1`
  (`docs/10:108-116`).

**Doc 06 confirma la decisión de producto.** `docs/06-roadmap.md:157`: *"Facturación electrónica
AFIP/ARCA"* listada como ítem pendiente del roadmap post-cutover, resuelta por `docs/11:36` ("Va
última (Etapa 19), a pesar de ser la de mayor tamaño") y `docs/11:484` (backlog → Etapa 19).

**Precedente de dependencias auditadas.** `src/Ways.Infrastructure/Ways.Infrastructure.csproj:8` —
`ClosedXML` pineado a `0.104.2`, aislado a un único archivo (`ExportadorXlsx.cs:7`, comentario
explícito *"Único archivo de src/ que referencia ClosedXML"*). Target framework confirmado
`net10.0` (`Ways.Api.csproj:4`).

**Precedente de reloj testeable.** `src/Ways.Application/Abstracciones/IRelojDelSistema.cs` —
interfaz `Ahora => DateTimeOffset` ya usada en toda la app para vencimientos testeables
(`RelojFijo` es el fixture de la Etapa 17). Es el mecanismo directo a reusar para testear la
expiración del Ticket de Acceso de WSAA.

## El protocolo ARCA (investigación externa, 2026)

**WSAA — autenticación.** El cliente firma un TRA (Ticket de Requerimiento de Acceso) con el
certificado X.509 y su clave privada, produce un CMS (PKCS#7) y lo envía a `LoginCMS`, que
devuelve un Ticket de Acceso (TA) con `Token` + `Sign`, válido **12 horas**. Hay un intervalo
preventivo mínimo entre pedidos: 10 minutos en Testing, 2 minutos en Producción, para el mismo
servicio ([WSAA — Especificación Técnica](https://www.afip.gob.ar/ws/WSAA/Especificacion_Tecnica_WSAA_1.2.2.pdf)).

**WSFEv1 — endpoints confirmados por lectura directa del manual oficial**
(`manual-desarrollador-ARCA-COMPG-v4-0.pdf`, RG 4291, revisión 15/01/2025, branding ARCA):
- Homologación: `https://wswhomo.afip.gov.ar/wsfev1/service.asmx` (WSDL: `?WSDL`)
- Producción: `https://servicios1.afip.gov.ar/wsfev1/service.asmx`

**`FECAESolicitar` — forma exacta del request** (leída del XML de ejemplo del manual):
- `Auth`: `Token` (string), `Sign` (string), `Cuit` (long — CUIT del contribuyente emisor).
- `FeCabReq`: `CantReg` (int), `PtoVta` (int — el punto de venta **fiscal**, no el
  `id_punto_venta` interno), `CbteTipo` (int).
- `FeDetReq[]` (`FECAEDetRequest`): `Concepto` (1 productos/2 servicios/3 ambos), `DocTipo`,
  `DocNro`, `CbteDesde`/`CbteHasta` (numeración fiscal, rango 1-99999999), `CbteFch` (yyyymmdd),
  `ImpTotal`, `ImpTotConc`, `ImpNeto`, `ImpOpEx`, `ImpTrib`, `ImpIVA`,
  `FchServDesde`/`FchServHasta`, `FchVtoPago`, `MonId`/`MonCotiz` (ARS = `PES`, cotización 1),
  `CondicionIVAReceptorId`, `CbtesAsoc[]` (para NC/ND), `Tributos[]`, `Iva[]` (array de
  `AlicIva{Id, BaseImp, Importe}` — mapea directo al snapshot de alícuota que
  `items_comprobante_venta` ya guarda por línea), `Opcionales[]`, `Compradores[]`,
  `PeriodoAsoc`, `Actividades[]`.
- Respuesta: aprobado (CAE + vencimiento), aprobado-con-observaciones (CAE igual, con
  `Observaciones`), o rechazado (validación excluyente, sin CAE) — **tres estados, no dos**.

**QR fiscal (RG 4291) — payload JSON confirmado**: `ver`, `fecha`, `cuit`, `ptoVta`, `tipoCmp`,
`nroCmp`, `importe`, `moneda`, `ctz`, `tipoDocRec`, `nroDocRec`, `tipoCodAut` (`"E"`=CAE,
`"A"`=CAEA), `codAut` — codificado en base64, embebido en
`https://www.afip.gob.ar/fe/qr/?p=<base64>`
([fuente](https://sites.google.com/site/facturaelectronicax/wsfev1/wsfev1/wsafipfe-codigo-qr)).

**CAEA — cambio regulatorio reciente y relevante.** Desde el 1 de junio de 2026, CAEA dejó de
ser un mecanismo de uso programado quincenal y quedó **reservado exclusivamente para
contingencia** (caída de servicio, sin internet), con un tope de 5% del tiempo mensual de
disponibilidad medido por sucursal
([infozona](https://www.infozona.com.ar/cae-arca-afip-2026-que-es-como-obtener-cambio-junio-caea/),
[wynges](https://wynges.com/blog/caea-cae-cambio-2026/)). Esto simplifica el alcance: no hace
falta un "modo CAEA operativo" de rutina, solo un camino de contingencia acotado y auditable.

**Homologación — requiere SIEMPRE una Clave Fiscal real, aunque el CUIT no se valide.** El
certificado de homologación se genera vía WSASS ("Autoservicio de Acceso a APIs de
Homologación"), al que se accede desde el "Administrador de Relaciones de Clave Fiscal" con
**Clave Fiscal Nivel 2 de una persona física real**. Para homologación se puede emitir el
certificado al propio nombre y CUIT (la validación de CUIT no aplica en Homo) — pero **el login
a WSASS en sí mismo exige una Clave Fiscal real autenticada**, no un CUIT dummy
([AfipSDK — habilitar administrador de certificados de testing](https://docs.afipsdk.com/paso-a-paso/tutoriales-pagina-de-afip/habilitar-administrador-de-certificados-de-testing),
[obtener certificado de testing](https://docs.afipsdk.com/recursos/tutoriales-pagina-de-arca/obtener-certificado-de-testing)).
No existe un "CUIT anónimo de testing público" para WSFE/WSAA — el `20111111112` de los ejemplos
es del servicio de padrón (WSPadron PUC), no de WSFE/WSAA.

**Librerías .NET.** No hay una librería .NET mantenida y auditable equivalente a `ClosedXML`
para WSAA/WSFE: `AfipWsfeClient` (NuGet 1.0.0) y `tecnocode-sa/afipwsfeclient` son de bajísima
adopción y sin mantenimiento activo — no cumplen el criterio de dependencia auditada del
proyecto. La alternativa técnicamente más fuerte y más alineada al patrón ClosedXML: (a)
consumir el WSDL real con el paquete oficial de Microsoft `System.ServiceModel.Http` (sucesor
cliente-only de WCF para .NET moderno — soporta `BasicHttpBinding`/`BasicHttpsBinding`, lo que
exponen estos `.asmx`), y (b) firmar el TRA→CMS con **BCL pura**:
`System.Security.Cryptography.Pkcs.SignedCms` (desde .NET Core 3.0, multiplataforma) — sin
dependencia externa para la parte criptográfica más sensible.

## La línea del corte (sin credenciales del dueño)

| Bloque | Construible SIN credenciales | Sólo con alta en HOMOLOGACIÓN (Clave Fiscal del dueño) | Sólo en PRODUCCIÓN |
|---|---|---|---|
| Schema fiscal | Completo: `empresas.id_condicion_fiscal`, `puntos_venta` fiscal, columnas CAE/vencimiento/resultado/observaciones en `comprobantes_venta`, tabla de certificados por empresa (cifrada) | — | — |
| Dominio del comprobante fiscal | Completo: activar `es_fiscal` en el resolver, neto/IVA por alícuota desde el snapshot existente, `ResolvedorDeLetraComprobante` sale de dormant | — | — |
| Máquina de estados CAE | Completa: pendiente → aprobado / aprobado-con-observaciones / rechazado, idempotencia de reintento, contingencia CAEA como estado explícito | Validación de que las transiciones reales coinciden 1:1 con las mockeadas | — |
| Generador TRA/CMS | Completo, con certificado X.509 **autogenerado de prueba** (self-signed, BCL pura), 100% testeable | La firma real solo la acepta WSAA si el certificado fue emitido por ARCA | Certificado de producción, mismo código |
| Cliente WSAA/WSFE | Completo contra **mocks locales** con las respuestas reales del manual (LoginTicketResponse, códigos 500/501/502/600/601/602) | Prueba end-to-end contra `wswhomo.afip.gov.ar` — requiere el alta WSASS del dueño | Prueba contra `servicios1.afip.gov.ar` — CUIT de la empresa + certificado de producción |
| QR fiscal | Completo: payload JSON + base64 + verificación de forma, con `codAut` sintético | Verificación de que ARCA resuelve el QR real (requiere un CAE real de Homo) | — |
| Numeración fiscal | Diseño y máquina de estados del punto de venta fiscal completos | `FEParamGetPtosVenta` requiere alta en Homo | Alta del punto de venta fiscal real |
| Almacenamiento del certificado | Completo: modelo de cifrado por empresa, rotación, expiración | — | Carga del certificado/clave real |
| Contingencia CAEA | Completa como máquina de estados y cola offline | Prueba de un ciclo real contra Homo | — |
| Impresión / UI | Completa: comprobante con QR, pantallas de configuración de certificado (con placeholder) | — | — |
| Homologación en sí | — | **Todo el bloque depende de que el dueño loguee con Clave Fiscal en WSASS** — sin eso, cero pruebas contra servidores reales de ARCA, ni en Homo ni en Producción | — |

**La línea exacta**: todo lo que no requiere que un cliente real hable con
`wswhomo.afip.gov.ar` o `servicios1.afip.gov.ar` es construible y testeable con precisión hoy.
El primer punto de bloqueo real es el certificado — y ese certificado, aunque sea "solo de
homologación", exige que el dueño inicie sesión en el portal de ARCA con su Clave Fiscal.

## Decisiones para el proposal

1. **Modelo de certificado por empresa.** (a) tabla `certificados_fiscales` con clave privada
   cifrada a nivel de aplicación (clave maestra de app, no de tenant — un dump de DB no filtra
   certificados en claro); (b) secret manager externo referenciado por id. **Recomendación:**
   (a) para el MVP — agregar un vault externo solo para esto es desproporcionado.
2. **Punto de venta fiscal vs `id_punto_venta` interno.** (a) `puntos_venta.numero_fiscal
   integer NULL` con DOS numeraciones paralelas — la interna (histórica, TX/NCX/TXR) y la
   fiscal (solo `es_fiscal = true`); (b) reemplazar la interna. **Recomendación:** (a) —
   aditivo, no rompe historial, y un PV puede operar fiscal y no-fiscal en simultáneo durante
   la transición.
3. **El tipo fiscal de la consolidación.** La restricción de la Etapa 17 (proposal:480-482)
   descarta migrar `TXR` en el lugar. **Recomendación:** tipo fiscal nuevo con el flag de
   consolidación; `ServicioDeFacturacionDeRemitos` recibe el tipo como parámetro en vez de
   hardcodear `'TXR'`.
4. **Contingencia.** Cola de "pendientes de CAE" con reintento exponencial + circuit breaker;
   CAEA solo como último recurso explícito (el cambio regulatorio de junio 2026 lo exige).
5. **Homologación por empresa.** WSASS exige Clave Fiscal por CUIT y cada empresa tiene su
   CUIT (doc-11:402-404) — la homologación es intrínsecamente por empresa; no es decisión
   abierta, solo documentarlo.
6. **Idempotencia ante CAE ya emitido.** Un CAE es un hecho jurídico irreversible: antes de
   cada `FECAESolicitar`, consultar `FECompConsultar` con el rango propuesto; si ya existe,
   adoptar el CAE existente. Parte del contrato del cliente, no opcional.

## Approaches

1. **Todo-en-una-etapa monolítica.** Pros: una narrativa. Cons: viola el guard de 400 líneas
   muchas veces; mezcla trabajo 100% desbloqueado con trabajo bloqueado por el dueño. Effort:
   Alto (doc-11:419: *"la mayor del programa"*).
2. **Sub-etapas alineadas al corte**: (19a) schema + dominio + máquina de estados + mocks —
   completamente desbloqueada HOY; (19b) cliente real + certificado + homologación — bloqueada
   hasta el alta WSASS, con razón de bloqueo nombrada y verificable; (19c) impresión/QR/UI +
   contingencia CAEA + libro IVA. Pros: 19a arranca ya; cada sub-etapa con su propio slicing
   ~400-500 líneas/PR (patrón 16/17). Cons: overhead de coordinación — mitigado porque el
   manual oficial ya da el contrato exacto (sin incertidumbre de forma para los mocks).

**Recomendación:** Approach 2 — el único que honra literalmente el mandato.

## Risks

- **SOAP en .NET 10.** `System.ServiceModel.Http` (Microsoft, mantenido) vs armar el sobre SOAP
  a mano con `HttpClient` + `System.Xml.Linq` (son ~2 operaciones + `FEParamGet*`). Decisión
  del proposal.
- **El reloj.** El TA de WSAA expira a las 12h — el caché usa `IRelojDelSistema`, testeable con
  `RelojFijo` (el precedente de reloj del programa, con sus 3 lecciones registradas).
- **Irreversibilidad fiscal.** Timeouts post-CAE-exitoso sin `FECompConsultar` previo producen
  comprobantes fiscales huérfanos o duplicados.
- **Tamaño.** La mayor del programa; sin sub-etapas excede todo guard de revisión razonable.
- **Bloqueo externo.** El alta WSASS depende de una acción del dueño fuera del repositorio; el
  proposal de 19b lo declara bloqueante explícito, jamás lo estima ni lo pide.

## Ready for Proposal

**Sí, para la sub-etapa 19a (schema + dominio + mocks) — el proposal puede escribirse ya**, con
el corte de esta exploración como frontera de alcance explícita. La 19b queda registrada como
`blocked` con la razón nombrada (alta WSASS pendiente del dueño) — la etapa se deja lista para
consumir la credencial el día que exista, sin pedirla.
