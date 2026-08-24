# Archive Report: stage-19-fiscal-arca (sub-etapa 19a)

**Change**: `stage-19-fiscal-arca` (scope: 19a) · **Modo**: openspec (artefactos en archivo) ·
**HEAD al cierre**: `8576f6f` (main) · **Fecha de archivado**: 2026-08-24 · **Target de archivo**:
`openspec/changes/archive/2026-08-24-stage-19-fiscal-arca/`

## Trazabilidad de fuentes leídas

- `openspec/changes/stage-19-fiscal-arca/proposal.md`
- `openspec/changes/stage-19-fiscal-arca/explore.md`
- `openspec/changes/stage-19-fiscal-arca/design.md`
- `openspec/changes/stage-19-fiscal-arca/state.yaml`
- `openspec/changes/stage-19-fiscal-arca/tasks.md` (159/159 tareas cerradas, 76 mutation targets)
- `openspec/changes/stage-19-fiscal-arca/verify-report.md` (veredicto PASS WITH WARNINGS, 0 CRITICAL,
  3 WARNING — W1/W2/W3)
- `openspec/changes/stage-19-fiscal-arca/specs/{fiscal-arca,comprobante-fiscal,certificados-fiscales,
  numeracion-fiscal,comprobantes-venta,auxiliary-catalogs,operacion-de-pos,tenant-organization}/spec.md`
  (8 delta specs, fusionados en esta fase)

**Estado final vs. snapshot de verify (autoridad de estado final)**: el `verify-report.md` registra
tres WARNING no bloqueantes (W1: título "four gates" en `comprobante-fiscal/spec.md:64`; W2: mismo
residual en `operacion-de-pos/spec.md:23`; W3: total final de `Ways.IntegrationTests` no restablecido
verbatim en `tasks.md`). El commit `8576f6f` ("docs(sdd): remedia W1-W3 del verify de la 19a — six
gates en los specs y el cierre de suites asentado"), posterior al verify, remedia los tres: ambos
specs dicen ahora "six gates" (confirmado en esta fase por lectura directa de los 8 deltas — ver
sección de specs fusionados) y `tasks.md:144` registra la línea explícita "Integration **1725/1725**".
Este reporte de archivo documenta el estado FINAL (post-8576f6f), no el estado del snapshot de
verify — no hay contradicción no resoluble entre fuentes: el remedio está confirmado en el propio
árbol de archivos, no solo afirmado.

---

## 1. Resumen ejecutivo

La sub-etapa 19a entrega el **circuito fiscal completo de ARCA (ex-AFIP) construido y probado contra
mocks del contrato oficial, sin ninguna credencial real**: schema fiscal (2 tablas nuevas, 3 `ALTER`
aditivos, 2 enums, 8 índices, 8 CHECKs, 5 FKs), el protocolo WSAA/WSFE hand-rolled sobre SOAP 1.1
aislado en un único archivo, la máquina de estados del CAE con sus cuatro invariantes, la numeración
fiscal con disciplina transaccional opuesta a la interna, el almacenamiento cifrado (AES-256-GCM) de
certificados X.509, y la emisión fiscal end-to-end (seis gates 409, QR RG 4291) — todo verificado
contra un set de fixtures pineado al manual oficial (`manual-desarrollador-ARCA-COMPG-v4-0.pdf` rev.
15/01/2025 + `Especificacion_Tecnica_WSAA_1.2.2.pdf`). **Cero bytes salen hacia un servidor ARCA
real**; el checkout no fiscal (`ServicioDeVentas`) queda byte-idéntico a `main` antes de esta
sub-etapa. El objetivo explícito de 19a (OD1) es que, el día que el dueño complete el alta WSASS, lo
único que falte sea apuntar el cliente ya probado a `wswhomo.afip.gov.ar` y cargar el certificado
real — 19a no pide esa alta, la documenta como bloqueo nombrado y verificable de 19b.

El verify (`verify-report.md`) cerró con **PASS WITH WARNINGS — 0 CRITICAL**, sobre las 13 criterios
de verificación vinculantes (design.md + gate de DB), las 8 specs delta / 32 requerimientos, las 159
tareas / 5 slices / 76 mutation targets, y los docs 09/10/11. Los tres WARNING (residuo textual "four
gates" en dos specs y una cifra de suite no restablecida verbatim) fueron remediados en el commit
`8576f6f`, posterior al verify, antes de esta fase de archivo.

---

## 2. Pull Requests (#159–#163)

| PR | Rama | Slice | Merge SHA | Contenido en una línea |
|---|---|---|---|---|
| #159 | `feat/stage19a-slice1-schema-fiscal` | 1 | `b5b3b35` | Schema fiscal completo (2 enums, 2 tablas, 3 `ALTER` aditivos, 8 índices, 8 CHECKs, RLS forzada, 10 ramas de error, docs 09/10) — sin ningún caller de escritura todavía |
| #160 | `feat/stage19a-slice2-wsaa` | 2 | `3fd2d79` | `SobreSoap` + `GeneradorDeTra` + `FirmanteCms` (`SignedCms`) + `ClienteWsaa` + caché de TA en memoria con single-flight + certificado de prueba autogenerado + fixtures WSAA pineadas al manual — sin consumidor de producción todavía |
| #161 | `feat/stage19a-slice3-wsfe-y-cae` | 3 | `757acc4` | `ClienteWsfe` (`FECAESolicitar`/`FECompConsultar`/`FECompUltimoAutorizado`/`FEParamGet*`) + `ComposicionDeTotalesFiscales` + `MaquinaDeEstadosCae` con `PermisoDeSolicitud` + fixtures de las tres respuestas + taxonomía de errores + backoff/circuit breaker — sin consumidor todavía |
| #162 | `feat/stage19a-slice4-numeracion-certificados` | 4 | `3c30f21` | `AsignadorDeNumeroFiscal` (invariante I1, lock singleton-prefijo probado contra `pg_locks`) + `CifradoDeClavesFiscales` (AES-256-GCM con AAD atada a la fila) + ABM `AdministracionFiscal` |
| #163 | `feat/stage19a-slice5-emision-y-qr` | 5 | `7606aab` | `ServicioDeFacturacionFiscal` — emisión end-to-end contra mocks (seis gates 409, invariante I2 vía `FECompConsultar`, reintento con recomposición completa de totales) + payload QR RG 4291 |

Los cinco merges fueron confirmados contra `git log` de `main` en el propio verify (`verify-report.md`
sección 3), en el orden declarado por `tasks.md`, byte a byte.

---

## 3. Decisiones

### 3.1 Decisiones del orquestador (OD1, OD2)

- **OD1 — la etapa 19 se ejecuta en tres sub-etapas alineadas al corte del explore.** 19a (este
  change): schema fiscal + dominio + máquina de estados CAE + generador TRA/CMS con certificado de
  prueba autogenerado + cliente WSAA/WSFE contra mocks con el contrato real del manual + numeración
  fiscal + almacenamiento cifrado de certificados + QR con `codAut` sintético. **19b nace BLOCKED**:
  homologación real contra `wswhomo.afip.gov.ar`, carga del certificado real, confirmación de los
  catálogos `FEParamGet*`, un ciclo de CAE real — razón de bloqueo nombrada y verificable: alta WSASS
  pendiente del dueño (login con Clave Fiscal Nivel 2), **nunca pedida, solo documentada**. **19c
  (pendiente, no bloqueada)**: impresión fiscal con QR, UI de configuración de certificado/PV/condición
  fiscal, contingencia operativa (cola offline + CAEA), tipo fiscal de la consolidación de remitos con
  su escritor, libro IVA.
- **OD2 — ningún artefacto de 19a puede contener un endpoint real de ARCA como valor por defecto**
  (criterio de verify 8, confirmado PASS); ningún slice de 19a depende de una credencial.

### 3.2 Las 13 decisiones del proposal

1. **Certificado ausente es el gate, no un feature flag** — sin certificado activo, `409` y cero
   llamadas de red (invariante I4).
2. **Los mocks son el contrato**, y cada fixture cita su sección del manual, pineado por `REVISION.md`.
3. **La máquina de estados del CAE tiene cuatro invariantes** (I1 sin huecos, I2 idempotencia vía
   `FECompConsultar`, I3 terminalidad, I4 inercia) — la razón de ser de la sub-etapa.
4. **El número fiscal se asigna con la disciplina opuesta al contador interno**: dentro de la
   transacción de emisión, nunca comiteado antes en una transacción chica propia.
5. **El checkout del POS no cambia** — el guard `EsFiscal` de `ServicioDeVentas.cs:1162` se angosta
   (endpoint propio para lo fiscal), nunca se remueve; reproducir la clase de "venta fantasma" del
   PRE-latente de la etapa 17 aquí sería legalmente irreversible.
6. **Cripto solo BCL** — `SignedCms` para el CMS, `AesGcm` para la clave privada en reposo, sin
   dependencia de terceros.
7. **DB Change Gate**: una sola migración `FiscalArcaEtapa19a`, cero artefactos irreversibles (cero
   `ALTER TYPE ADD VALUE`), ratificado por el orquestador el 2026-08-21 con verificación independiente
   registrada.
8. **SOAP 1.1 a mano, aislado en un único archivo (`SobreSoap`)**, `System.ServiceModel.Http`
   rechazado — un proxy generado del WSDL serían miles de líneas irrevisables contra el presupuesto de
   review adversarial de 400 líneas, y movería los mocks del cable a una interfaz.
9. **Los mocks están versionados** — cada fixture cita su sección del manual y el set entero se pinea
   a la revisión exacta del PDF; cuando ARCA revise el manual, el diff entre sets es el análisis de
   impacto.
10. **TA en memoria detrás de un puerto**, con `IRelojDelSistema` y margen de seguridad — persistir un
    TA es persistir una credencial portadora; diferido a 19b (tabla `tickets_acceso_fiscal` registrada
    como ítem de gate).
11. **El mapeo `codigo_afip` es DATA con doble red**, sin ningún `ALTER` — las tres tablas ya tenían la
    columna. `Exento`/`No gravado` quedan `NULL` por regla (no son alícuotas). `NO_RESP` es la única
    incertidumbre flaggeada, a confirmar en 19b.
12. **El certificado de prueba lo generan los tests en runtime** (`CertificateRequest`, BCL) — cero
    material de clave en el repo jamás, con barrido de verificación.
13. **Numeración fiscal = tabla propia** (`numeraciones_fiscales`), nunca una extensión de
    `numeraciones_comprobante` — las dos disciplinas transaccionales son opuestas y mezclarlas
    produciría un mantenedor futuro reusando el asignador equivocado con consecuencia legal.

### 3.3 Decisiones de diseño clave

- **D1 — lock singleton-prefijo.** `numeraciones_fiscales` entra al orden total de locks en la
  POSICIÓN 0, estrictamente antes de `turnos_caja`, y la transacción de emisión fiscal no toma ningún
  otro lock de fila existente. Al ser un singleton en posición 0, el camino fiscal es un
  "prefix-singleton": acíclico por construcción, y ningún camino existente puede quedar en cola detrás
  de ARCA — un stall de WSFE no puede alcanzar al POS, porque un checkout nunca toca esta fila y esta
  transacción nunca toca `turnos_caja`, `stock` ni `clientes`. Probado con una encuesta en vivo de
  `pg_locks` desde una segunda conexión (criterio de verify 12, ambas mitades).
- **Goldens byte a byte.** Cada envoltorio SOAP generado (TRA, `LoginCms`, `FECAESolicitar`) se compara
  byte a byte contra un XML de referencia transcripto del manual — `SaveOptions.DisableFormatting` +
  `XDeclaration` sin indentación. Es el mecanismo que capturó el CRITICAL del orden `ImpIVA`/`ImpTrib`
  invertido (ver judgment log, Slice 3 ronda 2).
- **D4 — `PermisoDeSolicitud` como clase sellada tras el CRITICAL del struct.** El diseño original
  declaraba `PermisoDeSolicitud` como `readonly record struct` con constructor `internal`, asumiendo
  que eso lo hacía "irrepresentable" fuera de `MaquinaDeEstadosCae`. Judgment-day (Slice 3, ronda 1,
  juez B) encontró que todo `struct` de C# sintetiza un constructor sin parámetros invocable desde
  cualquier ensamblado — una puerta trasera que fabricaba un permiso con `IdComprobante = 0` sin pasar
  por `AutorizarSolicitud`. Corregido a `sealed record` (tipo por referencia): un tipo de referencia
  sin constructor público no tiene constructor sintetizado invocable externamente, cerrando la vía. El
  doc-comment se corrigió (ya no dice "irrepresentable" sin matiz) y el test se reforzó con una
  aserción estructural (`!IsValueType`).

---

## 4. Log de judgment-day por slice

Cinco slices, cada uno con al menos una ronda de judgment-day (dos jueces ciegos independientes,
hallazgos confirmados corregidos, re-ronda hasta ronda limpia). Resumen por slice:

| Slice | Rondas | CRITICAL | Cierre |
|---|---|---|---|
| 1 (schema) | 1 | 0 | Juez B APPROVE (1 MINOR de etiquetado + 1 SUGGESTION + 1 WARNING diferido a slice 5); juez A APPROVE con cero hallazgos. Ronda limpia directa |
| 2 (WSAA) | 2 | 1 (de ledger/proceso) | Ronda 1 juez B APPROVE; ronda 2 juez A: 1 CRITICAL de honestidad de ledger (un checkbox de judgment marcado a medias por el fix agent — corregido por autoridad del orquestador, no un defecto de código) + 1 WARNING + 4 SUGGESTIONs. Fixes `ef5871c`/`49b6d05`. Ronda limpia |
| 3 (WSFE + CAE) | 2 | 2 (código) | Ronda 1 juez B: **CRITICAL de la puerta trasera del struct** (`PermisoDeSolicitud`). Ronda 2 juez A: **CRITICAL del orden `ImpIVA`/`ImpTrib` invertido contra el manual**. Fixes `30de47c`/`4bdcfd3`. Ronda limpia |
| 4 (numeración + certificados) | 2 | 1 (código) | Ronda 1 juez B: 3 MAJOR test-only (sin CRITICAL). Ronda 2 juez A: **CRITICAL de `CryptographicException` pelada** ante ciphertext corrupto. Fixes `48a4ed8`/`fb70eec`/`2dddb53`. Ronda limpia |
| 5 (emisión + QR) | 2 | 2 (código) | Ronda 1 juez B: **CRITICAL de evidencia inflada** (el TOCTOU del target 68 no existía de verdad). Ronda 2 juez A: **CRITICAL de la re-emisión con ceros fabricados**. Fixes `ab185d5`/`5bea411`. Ronda limpia — la última del programa autónomo |

### Los cuatro CRITICAL "estrella" y su lección

1. **La puerta trasera del struct (`PermisoDeSolicitud`, Slice 3 ronda 1, juez B).**
   Un `readonly record struct` con constructor `internal` sintetiza igual un constructor sin
   parámetros invocable desde cualquier ensamblado, fabricando un permiso con `IdComprobante = 0` sin
   pasar por la máquina de estados. **Lección**: "irrepresentable por tipo" en C# exige un tipo por
   referencia (`class`/`sealed record`), nunca un `struct` — el constructor `internal` por sí solo no
   cierra la vía cuando el tipo subyacente es un `struct`.

2. **El orden del manual invertido (`ImpIVA`/`ImpTrib`, Slice 3 ronda 2, juez A).**
   El slice emitía `ImpIVA` antes de `ImpTrib`, autoconsistente en tres lugares (el mapper, el golden
   XML, y el orden posicional del record `SolicitudDeCae`) — pero el orden exacto transcripto del
   manual (`explore.md:87-96`) es el inverso. La nota de `REVISION.md` decía que la desviación sería
   "indetectable hasta 19b sin acceso directo al PDF", pero el cross-check in-repo (la transcripción de
   `explore.md`) existía y no se usó — de hecho habría cazado el CRITICAL. **Lección**: un golden byte
   a byte solo protege si el propio golden fue verificado contra la fuente citada; una transcripción
   independiente ya presente en el repo debe cruzarse activamente contra cada fixture antes de darlo
   por confiable, no darse por sentado porque "está pineado".

3. **La evidencia inflada del TOCTOU (target 68, Slice 5 ronda 1, juez B).**
   El test que decía probar la condición de carrera real dejaba la fila ya en `aprobado` ANTES de
   invocar `ReintentarAsync`, así que moría en el filtro de lectura externa (404) — el `UPDATE`
   guardeado bajo prueba nunca se alcanzaba, y el conjunct sobrevivía 9/9 a su eliminación sin que
   ningún test lo notara. **Lección**: un mutante que sobrevive N/N veces a su propia eliminación no es
   evidencia de robustez — es evidencia de que el camino bajo prueba nunca se ejecutó; la corrección
   exige construir la carrera real (interceptor de pausa + segunda conexión cruda que gana la carrera)
   y no solo simular su resultado final.

4. **La re-emisión con ceros fabricados (`ReintentarAsync`, Slice 5 ronda 2, juez A).**
   La rama de reintento no-adoptada construía `SolicitudDeCae` con `ImpTotConc=0`/`ImpOpEx=0`/`Iva[]=[]`
   hardcodeados, sin releer nunca los ítems del comprobante — para una factura con líneas
   Exento/No-gravado, esto rompía el invariante de totales del propio spec en el cable real hacia
   ARCA. **Lección**: un camino "secundario" (reintento) que reconstruye un payload de cero, en paralelo
   al camino "primario" (emisión), diverge tarde o temprano si no comparte la misma función de
   composición — la corrección extrajo un `ComponerLineasFiscalesDesdeItemsAsync` compartido para que
   ambos caminos nunca puedan divergir de nuevo.

---

## 5. Suites finales del programa

Sobre `main@21c3294` (post-#163, primera pasada, cero flakes) — confirmado en `tasks.md:143-147` y
asentado explícitamente por el remedio W3 en `8576f6f`:

| Suite | Resultado |
|---|---|
| Ways.Domain.Tests | **545/545** |
| Ways.Application.Tests | **373/373** |
| Ways.IntegrationTests | **1725/1725** (el 1715 de la tarea 5.25 era la cifra pre-rondas de judgment-day; +10 tests netos de las rondas 1/2 del slice 5) |
| vitest (Ways.Web) | verde completo |

76 mutation targets cerrados (23+13+15+12+13 por slice), cada uno con ciclo mutación-RED-revert-GREEN
o, para las filas estructurales `[S]`, la aserción de definición/estado equivalente.

---

## 6. Lecciones del programa

1. **Un tipo "irrepresentable" en C# necesita ser por referencia.** Un `struct` con constructor
   `internal` sigue exponiendo un constructor sin parámetros público — la irrepresentabilidad real
   exige `class`/`sealed record`. Aplica a cualquier invariante futuro modelado como "solo esta clase
   puede construir este valor" (D4, `PoliticaDeRoles`-style).
2. **Un golden byte a byte es tan confiable como el cross-check activo contra su fuente citada, no
   solo su existencia pineada.** Una transcripción independiente del manual ya en el repo
   (`explore.md`) debe cotejarse contra cada fixture antes de asumir que el pineo a `REVISION.md`
   basta — el CRITICAL de `ImpIVA`/`ImpTrib` habría sido cazado por ese cruce, disponible desde el
   explore.
3. **Un mutante que sobrevive N/N veces a su propia eliminación es una señal de alarma, no de
   fortaleza** — casi siempre significa que el camino bajo prueba nunca se ejecutó de verdad (el
   fixture deja el estado ya resuelto antes de invocar la acción). Construir la condición de carrera
   real (interceptor de pausa + segunda conexión) es la única evidencia aceptable para un TOCTOU.
4. **Dos caminos que producen el mismo payload de dominio (emisión y reintento) deben compartir la
   misma función de composición**, nunca reconstruir el payload por separado — la divergencia entre
   caminos "primario" y "secundario" es un patrón recurrente de CRITICAL en este programa (visto
   también, con otra forma, en etapas previas).
5. **La honestidad del ledger de judgment-day es en sí misma un artefacto verificable**: un checkbox
   marcado a medias por un fix agent (sin autoridad para declarar una ronda cerrada) es tratado como un
   CRITICAL de proceso, con la misma disciplina de mutación-revert que un defecto de código — la regla
   18 (solo el orquestador cierra una ronda) se sostuvo sin excepción a lo largo del programa.
6. **Un lock en posición 0 con conjunto singleton es la única forma segura de sostener un lock de fila
   durante un round-trip de red** (D1) — cualquier otra posición en el orden total convierte un timeout
   de ARCA en un stall del negocio completo (POS bloqueado detrás de `turnos_caja`).

---

## 7. Backlog para 19b (BLOCKED)

**Estado**: BLOCKED. **Razón nombrada y verificable**: alta WSASS pendiente del dueño (login con Clave
Fiscal Nivel 2 en el Administrador de Relaciones de ARCA). No existe CUIT anónimo de testing para
WSFE/WSAA — el `20111111112` de los ejemplos oficiales es de WSPadrón, no de WSFE. **Esto no se pide,
se documenta**: sin slice, sin estimación, sin checkbox que aparente ser deuda del equipo.

Ítems registrados para cuando el bloqueo se levante (tomados y consolidados de `verify-report.md`
sección 6):

1. El `int.Parse` sobre el código de fault WSAA (`ClienteWsaa.cs:71`, `MapearFalla`) asume la
   numeración numérica que pide el proposal (500/501/502/600/601/602); si el cable real emite un fault
   simbólico (`ns1:cms.sign.invalid` y similares — T3), esto lanza `FormatException` en vez de mapear a
   un error de dominio. 19b debe confirmar contra el cable real y decidir si el parseo necesita un
   `TryParse` defensivo con fallback a `wsaa_error_no_mapeado`.
2. Confirmar que `NO_RESP` mapea a `codigo_afip = 15` contra `FEParamGetCondicionIvaReceptor` —
   actualmente un seed provisional (RG 5616 "IVA No Alcanzado") nunca consumido por ninguna decisión de
   runtime (el gate de rechazo evalúa `Codigo`, jamás `CodigoAfip`), pero el valor sembrado en sí está
   sin confirmar.
3. Los fixtures WSFE tienen pedigrí más bajo que los de WSAA — los de WSAA fueron cruzados
   directamente contra el PDF del manual; los de WSFE fueron reconciliados contra la transcripción
   in-repo de `explore.md` (que ya cazó un defecto real de orden `ImpIVA`/`ImpTrib` en judgment-day
   Slice 3 ronda 2), no contra el manual directamente. El primer trabajo de 19b (diff fixtures-vs-
   realidad, T4) debe tratar el set WSFE con menor confianza.
4. Confirmar la taxonomía exacta de códigos de fault WSAA (T3) contra los strings simbólicos del cable
   — la numeración del proposal está sin verificar contra la especificación.
5. Tabla `tickets_acceso_fiscal` — ítem de gate registrado (decisión 10): persistir el TA se difiere
   porque ningún TA real es obtenible hasta 19b; el caché en memoria + puerto single-flight es la forma
   interina.
6. El diff fixture-vs-manual real como primera tarea literal de 19b (T4, ambos sets WSAA y WSFE).

---

## 8. Backlog para 19c (pendiente, no bloqueado salvo un CAE real impreso)

Tomados y consolidados de `verify-report.md` sección 6:

1. **(BINDING, T1)** El plan de escritura fiscal es `comprobante + items` ÚNICAMENTE (D12); 19c debe
   agregar las escrituras de `movimientos_stock`/`pagos_comprobante`/`movimientos_cuenta_corriente`/
   guard de turno junto con la pantalla que las alimenta, y el test de cero-filas del target 75 debe
   pasar a RED como trip-wire probando que el gap se cerró a propósito (`FA`/`FB`/`FC` cargan
   `afecta_stock = true` en el catálogo hoy — una inconsistencia nombrada, segura solo por I4).
2. **(T2)** El camino de liberación del operador de I1 (liberar un número fiscal atado-pero-no-resuelto
   por acción explícita del operador) no está enviado; 19a envía solo la mitad exigible. Registrado
   para 19c junto con la cola offline durable.
3. La deriva de la letra en pantalla en un reintento — `ReintentarAsync` recalcula
   `ResolvedorDeLetraComprobante.Resolver` en cada intento; si la condición fiscal del emisor/receptor
   cambió entre la emisión original y un reintento, la letra recalculada puede divergir de la letra con
   la que el comprobante fue emitido originalmente. Hoy es puramente informativo/visual (nunca validado
   contra un valor de catálogo persistido en el reintento) — registrado como nota UI-facing de 19c.
4. UI: impresión fiscal con QR, pantallas de configuración de certificado/PV/condición fiscal — 19a
   entrega solo la API/payload; nadie puede apretar un botón todavía.
5. Contingencia operativa: la cola offline durable y CAEA como último recurso, con sus propios valores
   de enum y sus escritores (regla de junio 2026: CAEA es solo-contingencia, tope 5%/mes).
6. El tipo fiscal de la consolidación de remitos (parametrización de
   `ServicioDeFacturacionDeRemitos`, TXR permanece como historia válida) — se envía junto con su
   escritor, por la regla de la etapa 17.
7. Libro IVA ventas/compras.
8. **Ya remediado en esta fase de archivo (no requiere acción de 19c)**: el residual textual "four
   gates" en `proposal.md:724,749,835` y `design.md:377,537,563` permanece como drift histórico
   cosmético de documentos ya cerrados (no se reabren retroactivamente); los dos spec files
   (`comprobante-fiscal/spec.md:64`, `operacion-de-pos/spec.md:23`) fueron corregidos a "six gates" en
   `8576f6f`, confirmado en esta fase por lectura directa antes de la fusión.
9. **Informativo, ya cerrado**: la tensión de DI de doble registro de
   `RepositorioEnMemoriaDeTicketDeAcceso` (registrado tanto como singleton concreto propio como vía el
   puerto), notada en slice 2, fue resuelta en slice 5 — `ObtenerOFirmarAsync` se elevó a
   `IRepositorioDeTicketDeAcceso` y el registro del tipo concreto se retiró. Sin acción pendiente.

---

## 9. Specs fusionados a `openspec/specs/`

Ver sección de ejecución mecánica más abajo para el detalle por dominio, la evidencia de `diff -r`, y
la nota sobre el requerimiento MODIFIED cuyo título cambió junto con su contenido.
