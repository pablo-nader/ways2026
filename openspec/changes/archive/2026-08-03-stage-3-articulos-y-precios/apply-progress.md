# Apply Progress: Stage 3 — Artículos y Precios

## Slice 4 — judgment-day ronda 2: fixes aplicados

Ronda de simplificación/docs/cobertura — NO hay defecto de comportamiento. Un REAL (condición
muerta simplificada + doc-comment reescrito, Judge A), un test de cobertura del happy path que
el REAL anterior dejó sin caso dedicado (Judge A) y una SUGGESTION de wording (Judge B). Todo
corregido en el mismo ciclo, SIN cambios de esquema.

### Item 1 — Simplificar el disparador del intercambio + corregir la narrativa (REAL, Judge A)

`ServicioDeListasPrecio.ActualizarAsync`: el disparador del intercambio que la ronda 1 dejó como
`datos.EsDefault && (!actual.EsDefault || datos.IdEmpresa != actual.IdEmpresa)` tiene un
disyunto muerto por construcción — `|| datos.IdEmpresa != actual.IdEmpresa` nunca aporta nada:
cuando `actual.EsDefault` es `false`, `!actual.EsDefault` solo ya es `true` y corta el OR por
cortocircuito; cuando `actual.EsDefault` es `true`, la guarda de la fuente (unas líneas arriba)
ya lanzó para cualquier cambio de `IdEmpresa` antes de llegar a esta línea. El doc-comment de la
ronda 1 además sobrevendía ese disyunto como si hiciera trabajo real ("el intercambio se dispara
al... MOVER de alcance manteniéndose default").

**Fix**: condición simplificada a `!actual.EsDefault && datos.EsDefault` (sin cambio de
comportamiento — el disyunto eliminado nunca era alcanzable). `DesmarcarDefaultActualAsync`
sigue apuntando a `datos.IdEmpresa` (el DESTINO), que es lo que realmente maneja la promoción +
el movimiento de alcance a la vez. Doc-comment reescrito para describir la realidad: la guarda
de la fuente bloquea mover de alcance una fila que hoy es default; el intercambio maneja las
promociones (incluida la promoción combinada con un movimiento de alcance), siempre desmarcando
el destino.

- `src/Ways.Application/Catalogos/ServicioDeListasPrecio.cs` (`ActualizarAsync`, ~línea 127) —
  condición simplificada + doc-comment reescrito.
- `openspec/changes/stage-3-articulos-y-precios/state.yaml` — nota de corrección de ronda 2
  agregada AL FINAL de la lista de notas (no se reescribe la nota de ronda 1, se corrige con una
  nota nueva que la referencia).

### Item 2 — Test del happy path de promoción + movimiento de alcance (REAL, Judge A)

La ronda 1 solo cubrió los tres casos RECHAZADOS (mover de alcance una fila que YA es default).
Faltaba el caso PERMITIDO simétrico: una fila que NO es default, en el alcance A, promovida a
default Y movida al alcance B en la misma operación, cuando B ya tiene su propia default.

**Test nuevo**: `PromoverAEsDefaultMoviendoDeAlcanceDeEmpresaAOtraConDefaultExistenteEsPermitido`
— empresa A con una default propia + una lista NO default; empresa B con una default propia; PUT
sobre la lista no-default de A con `IdEmpresa: B, EsDefault: true` → 200; la fila termina default
en B; la ex-default de B queda `EsDefault: false`; la default de A queda intacta (mismo
`IdEmpresa`, `EsDefault: true` sin tocar).

- `tests/Ways.IntegrationTests/ListasPrecioEndpointsTests.cs` —
  `PromoverAEsDefaultMoviendoDeAlcanceDeEmpresaAOtraConDefaultExistenteEsPermitido`.

### Item 3 — Mensaje específico para el bloqueo de movimiento de alcance (SUGGESTION, Judge B)

La guarda de la fuente (`actual.EsDefault && datos.IdEmpresa != actual.IdEmpresa`) reusaba el
mismo texto que la guarda de "quitar el default sin reemplazo" — mismo código de dominio
(`lista_default_requiere_reemplazo`, sin cambios, ambos casos son la misma familia de problema),
pero el mensaje no distinguía el escenario para el usuario.

**Fix**: mensaje propio para el bloqueo de movimiento de alcance: "No se puede cambiar el
alcance de una lista default; primero asigná el default a otra lista del alcance de origen."

- `src/Ways.Application/Catalogos/ServicioDeListasPrecio.cs` (`ActualizarAsync`, ~línea 112) —
  mensaje específico, mismo código `lista_default_requiere_reemplazo`.

### Build/test results (judgment-day ronda 2 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 86/86 | 86/86 |
| `Ways.Application.Tests` | 190/190 | 190/190 |
| `Ways.IntegrationTests` (real Postgres) | 214/214 | 214/214 |

214 = baseline 213 (ronda 1) + 1 nuevo
(`PromoverAEsDefaultMoviendoDeAlcanceDeEmpresaAOtraConDefaultExistenteEsPermitido`). Build clean
(0 warnings, 0 errors), ambas corridas idénticas, sin flakes. El test nuevo verificado estable en
3 corridas aisladas adicionales.

### Commit (work-unit, uno solo — simplificación + docs + test, mismo hallazgo)

`fix(catalogos): simplificar el disparador del intercambio de es_default y su narrativa` —
`ServicioDeListasPrecio.cs` (items 1/3), `ListasPrecioEndpointsTests.cs` (item 2),
`state.yaml`/`apply-progress.md` (docs).

---

## Slice 4 — judgment-day ronda 1: fixes aplicados

Un CRITICAL confirmado por AMBOS jueces ciegos, un REAL de cobertura de tests, una SUGGESTION
de cobertura de carrera y un RESOLVED de deriva de spec. Todo corregido en el mismo ciclo, SIN
cambios de esquema.

### Item 1 — Intercambio de `es_default` sensible al alcance (CRITICAL confirmado por ambos jueces)

`ServicioDeListasPrecio.ActualizarAsync` solo disparaba el intercambio (INTERCAMBIO
transaccional que desmarca la fila default actual del alcance ANTES de guardar la nueva) con
`!actual.EsDefault && datos.EsDefault`. Eso dejaba un camino silencioso: una fila que YA era
default podía cambiar de `IdEmpresa` (p.ej. compartida -> empresa) manteniendo
`EsDefault: true` sin pasar por ninguna de las dos guardas existentes (`actual.EsDefault &&
!datos.EsDefault` no aplica porque `datos.EsDefault` sigue en `true`; `!actual.EsDefault &&
datos.EsDefault` tampoco porque `actual.EsDefault` ya era `true`) — el `PUT` caía directo a
`base.ActualizarAsync`, que solo actualiza la fila. Resultado: el alcance de ORIGEN se quedaba
sin ninguna lista default (invariante rota, "One Default List Per Tenant" heredado de stage 2)
y, si el alcance DESTINO no tenía todavía una fila default, el `PUT` sucedía en silencio sin
haber pasado nunca por `DesmarcarDefaultActualAsync`.

**Fix** (opción simple elegida por el veredicto, en vez de construir reasignación completa de
la fuente): se agrega una guarda que PROHÍBE cambiar `IdEmpresa` en una fila que hoy tiene
`EsDefault: true` — mismo código/mensaje de dominio que "quitar el default sin reemplazo"
(`lista_default_requiere_reemplazo`, 409), porque es el mismo problema de fondo (el alcance de
origen se quedaría sin default). El disparador del intercambio se amplía a `datos.EsDefault &&
(!actual.EsDefault || datos.IdEmpresa != actual.IdEmpresa)`: con la guarda de arriba ya en
vigor, la segunda condición del OR solo es alcanzable cuando `actual.EsDefault` era `false`
(un alta de default en un alcance nuevo/distinto), así que el intercambio sigue desmarcando
siempre el alcance DESTINO (`datos.IdEmpresa`), nunca el de origen — se deja la condición
explícita (en vez de simplificarla a solo `!actual.EsDefault`) porque documenta la intención
completa que pedía el veredicto y sirve de defensa en profundidad si la guarda de arriba
cambiara en el futuro.

- `src/Ways.Application/Catalogos/ServicioDeListasPrecio.cs` (`ActualizarAsync`, ~línea 103) —
  guarda nueva de protección de la fuente + disparador del intercambio ampliado, con
  doc-comments explicando ambas decisiones inline.

### Item 2 — Cobertura del límite superior de `porcentaje` (REAL)

`ExigirPorcentajeValido` ya rechazaba `porcentaje >= 1000` en producción, pero solo el piso
(`<= -100`) tenía tests — el techo no tenía ningún caso cubriéndolo.

- `tests/Ways.Application.Tests/Catalogos/ServicioDeListasPrecioTests.cs` —
  `CrearDerivadaConPorcentajeMayorOIgualA1000EsRechazada` (`[Theory]`, casos 1000 y 1500),
  mismo shape que el test existente del piso.

### Item 3 — Carrera de default por empresa (SUGGESTION)

Mirror del test de carrera de alcance compartido (`LaAsignacionConcurrenteDeEsDefaultA...`)
contra `ux_listas_precio_default_empresa` en vez de `ux_listas_precio_default_compartido`: dos
listas del MISMO `id_empresa` reciben `PUT EsDefault: true` concurrentemente (mismo
rendezvous con `InterceptorDeRendezVousListasPrecio`) — exactamente una gana (200), la otra
choca contra el índice único parcial (409 `default_duplicado`).

- `tests/Ways.IntegrationTests/ListasPrecioEndpointsTests.cs` —
  `LaAsignacionConcurrenteDeEsDefaultDeEmpresaAOtrasDosListasDaExactamenteUnGanador`.

### Item 1 — Tests del intercambio sensible al alcance (las tres direcciones)

- `tests/Ways.IntegrationTests/ListasPrecioEndpointsTests.cs` — tres tests nuevos, uno por
  dirección, todos aserting 409 `lista_default_requiere_reemplazo` + que la fila NO se movió
  de alcance y sigue siendo default (sin éxito silencioso, ningún alcance queda en cero):
  `MoverDeAlcanceCompartidoAEmpresaManteniendoEsDefaultEsRechazado` (compartida -> empresa,
  usa la lista General ya-default de la aprovisión), `MoverDeAlcanceEmpresaACompartidoManteniendoEsDefaultEsRechazado`
  (empresa -> compartida) y `MoverDeAlcanceDeUnaEmpresaAOtraManteniendoEsDefaultEsRechazado`
  (empresa A -> empresa B). Nuevo helper `SembrarEmpresaAsync` (mismo patrón que
  `ArticulosEndpointsTests.SembrarEmpresaAsync`) y overload de `AltaFijaValida` con parámetro
  `idEmpresa`.

### Item 4 — Deriva de spec resuelta (RESOLVED)

`specs/listas-precio-minimal/spec.md`, requirement "Blocked Mode Switch Once History Exists":
la cláusula "(for derivada) has ever been read-resolved" nunca se diseñó ni se implementó —
la tabla de Protection Rules de `design.md` y la tarea 4.1 siempre acotaron la guarda a
historial de `precios`, que una lista `derivada` nunca acumula por diseño (no se crean filas
`precios` para ella). Se reescribió el requirement para reflejar el comportamiento
implementado (guarda acotada a historial de `precios`, `derivada -> fija` siempre permitido)
con una nota de superseded (decisión del orquestador, 2026-08-03) documentando el motivo.

- `openspec/changes/stage-3-articulos-y-precios/specs/listas-precio-minimal/spec.md` (~líneas
  54-58) — requirement reescrito + nota de superseded.

### Item 5 — INFO registrado en state.yaml (sin acción de código)

- (a) las referencias a una `id_lista_base` inactiva siguen permitidas para derivadas NUEVAS
  (comportamiento consistente de lectura/escritura); decisión de producto explícita pendiente
  si alguna vez importa.
- (b) `fk_listas_precio_lista_base` sigue siendo una FK simple (no compuesta) — el pre-check
  del servicio es la protección real; el hardening compuesto necesitaría una migración con DB
  CHANGE GATE aprobado (opción futura).

### Build/test results (judgment-day ronda 1 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 86/86 | 86/86 |
| `Ways.Application.Tests` | 190/190 | 190/190 |
| `Ways.IntegrationTests` (real Postgres) | 213/213 | 213/213 |

Baseline 86/188/209 + 2 nuevos en Application (item 2) + 4 nuevos en IntegrationTests (3 del
item 1 + 1 del item 3) = 86/190/213. Build clean (0 warnings, 0 errors), ambas corridas
idénticas, sin flakes. Los 5 tests nuevos también corridos aislados 3 veces cada uno (vía la
clase completa `ListasPrecioEndpointsTests`, 17/17 estable x3, y el `[Theory]` de porcentaje,
2/2 estable x3) antes de la corrida completa.

### Commits (work-unit, uno por naturaleza del cambio)

- `fix(catalogos): proteger el alcance de origen del intercambio de es_default en listas de
  precio` — item 1, solo `ServicioDeListasPrecio.cs`.
- `test(catalogos): cubrir el intercambio de es_default entre alcances y la carrera de
  empresa` — items 1 (tests)/2/3, `ServicioDeListasPrecioTests.cs` +
  `ListasPrecioEndpointsTests.cs`.
- `docs(sdd): corregir el spec de cambio de modo y registrar la ronda 1 de judgment-day del
  slice 4` — item 4/5, `spec.md` + `state.yaml` + `apply-progress.md`.

---

## Slice 3 — judgment-day ronda 3: fixes aplicados

Un CRITICAL triaged y verificado por el orquestador contra la query real (`BuscarPredecesorAsync`)
y dos sugerencias de higiene/documentación. Todo corregido en el mismo ciclo, SIN cambios de
esquema.

### Item 1 — Predecessor query determinístico y libre de filas muertas (CRITICAL triaged)

`ServicioDePrecios.BuscarPredecesorAsync`: el predicado `WHERE vigente_hasta = $limite AND
id_precio != $excluded` es AMBIGUO cuando un reemplazo mismo-fecha previo ("corregir el importe
manteniendo la fecha", camino legítimo de la rama `esPendiente`) dejó una fila MUERTA
(`vigente_desde == vigente_hasta`) compartiendo el mismo límite que el predecesor REAL. Postgres
no garantiza cuál de las dos filas devuelve sin `ORDER BY`; si devuelve la muerta, el cierre
subsiguiente la REABRE (le pisa `vigente_hasta`), resucitando un precio que el usuario ya había
reemplazado — invisible en la cobertura de las rondas 1/2 porque ninguna de esas secuencias
encadenaba un reemplazo mismo-fecha con un segundo reemplazo de fecha distinta.

**Fix**: dos defensas en la misma query — `AND vigente_desde <> vigente_hasta` (excluye TODA fila
muerta; el predecesor real siempre tiene una ventana con contenido) + `ORDER BY vigente_desde ASC
LIMIT 1` (determinístico incluso si llegara a haber más de un candidato con contenido — el
predecesor real siempre es el de menor `vigente_desde`).

**Test nuevo** (`PreciosEndpointsTests.cs`):
`ReemplazarUnaPendienteMismaFechaYLuegoConFechaDistintaNoResucitaLaFilaMuerta` — inmediato (100) →
programado(150, D) → programado(160, D, MISMA fecha, `confirmarReemplazo`) → programado(170, D2 >
D, `confirmarReemplazo`); verifica que el predecesor REAL (100) es el que se extiende a D2 (no la
fila muerta), que la fila muerta del reemplazo mismo-fecha (150) queda con la ventana vacía
intacta, que exactamente una fila satisface el predicado "vigente" en cada instante sondeado, y
que ninguno de los dos precios reemplazados (150, 160) se vuelve visible en ningún instante
sondeado. Verificado estable en 3 corridas aisladas adicionales.

**Nota de metodología**: se validó por sanity-check revirtiendo temporalmente el fix — la
ambigüedad de Postgres es PROBABILÍSTICA (depende del layout físico de la tabla, no reproducible
de forma determinística en cada corrida), así que el test no puede garantizar que el código sin
fix falle en toda corrida; el fix (exclusión de filas muertas + orden determinístico) es correcto
por construcción de la query, independientemente de si una corrida puntual expone la ambigüedad.
El test SÍ verifica, de forma determinística, que el código CON el fix mantiene los invariantes de
integridad del historial en la secuencia que dispara el camino ambiguo.

### Item 2 — Comentario de intención de reuso mismo-fecha (SUGGESTION)

`AbrirNuevoPrecioAsync`, rama `esPendiente`: comentario nuevo aclarando que reemplazar una
pendiente con la MISMA fecha es una operación legítima ("corregir el importe manteniendo la
fecha") y que es exactamente por eso que `BuscarPredecesorAsync` tiene que ser determinístico y
excluir filas muertas.

### Item 3 — Cref obsoleto (SUGGESTION)

Doc-comment de `AbrirNuevoPrecioAsync`: `ReabrirLimiteDelPredecesorAsync` ya no existe (renombrado
a `BuscarPredecesorAsync` en la ronda 1/2) — actualizado para referenciar `BuscarPredecesorAsync`
(localiza) + el `CerrarFilaAsync` inline que reabre (cierra), reflejando el shape actual del
método.

### Build/test results (judgment-day ronda 3 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 86/86 | 86/86 |
| `Ways.Application.Tests` | 170/170 | 170/170 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 196/196 | 196/196 |

196 = baseline 195 (ronda 2) + 1 nuevo
(`ReemplazarUnaPendienteMismaFechaYLuegoConFechaDistintaNoResucitaLaFilaMuerta`). Build clean (0
warnings, 0 errors), ambas corridas idénticas, sin flakes. El test nuevo además verificado estable
en 3 corridas aisladas adicionales (2 veces: con el fix restaurado, y como sanity-check con el fix
revertido temporalmente — ver nota de metodología del item 1).

### Commits (work-unit)

1. `fix(precios): excluir filas muertas y ordenar deterministicamente la busqueda del predecesor` —
   `ServicioDePrecios.BuscarPredecesorAsync`/`AbrirNuevoPrecioAsync` (items 1-3), test de
   integración nuevo.

---

## Slice 3 — judgment-day ronda 2: fixes aplicados

Dos jueces ciegos independientes convergieron en el MISMO gap CRITICAL sobre el motor de
precios (validación asimétrica en el camino de reemplazo de una pendiente). Un cambio de
esquema (CHECK) fue gate-aprobado por el usuario (2026-08-03) como backstop del mismo hallazgo.
Dos sugerencias de higiene/documentación. Todo corregido en el mismo ciclo.

### Item 1 — Symmetric predecessor-boundary validation (CRITICAL, ambos jueces)

`AbrirNuevoPrecioAsync`, rama `esPendiente`: el chequeo `!esPendiente && vigenteDesdeEfectivo <
fila.VigenteDesde` (contra la fila ACTIVA) no tenía equivalente contra el PREDECESOR cuando se
reemplaza una PENDIENTE — una fecha de reemplazo anterior o igual al `vigente_desde` del
predecesor pasaba sin chequeo y `ReabrirLimiteDelPredecesorAsync` re-cerraba el predecesor con un
límite ANTERIOR a su propio inicio, invirtiendo su intervalo (`vigente_hasta < vigente_desde`) —
silencioso hasta este mismo ciclo, que agrega la constraint de esquema que lo hubiera atrapado
recién en el `INSERT`/`UPDATE` (ver item 2).

**Fix**: la búsqueda del predecesor (renombrada `ReabrirLimiteDelPredecesorAsync` →
`BuscarPredecesorAsync`, ahora SOLO busca, no cierra) se adelanta a ANTES de tocar cualquier fila.
Si `fila` es pendiente y tiene un predecesor real, se valida `vigenteDesdeEfectivo <=
predecesor.VigenteDesde` y se rechaza con 400 `vigente_desde_invalido` — mismo código de dominio
que el chequeo simétrico de la fila activa — ANTES de llamar a `CerrarFilaAsync` sobre `fila` o
sobre el predecesor. Atómico por transacción: si el chequeo pasa, el cierre de `fila` y el
re-cierre del predecesor ocurren después, en el mismo orden que antes.

**Test nuevo** (`PreciosEndpointsTests.cs`): repro exacto de Judge A —
`ReemplazarUnaPendienteConUnaFechaAnteriorAlPredecesorRechazaCon400SinTocarNada`: inmediato (100,
a T) → programado(150, a T+20s) → programado(999, a T-1s dentro de la tolerancia de reloj,
`confirmarReemplazo`) → 400 `vigente_desde_invalido`; verifica que el predecesor (100) sigue
cerrado en su boundary original (T+20s, el `vigente_desde` de la pendiente) y que la pendiente
(150) sigue abierta — nada se tocó.

### Item 2 — `ck_precios_ventana_valida` (schema CHECK, GATE-APROBADO 2026-08-03)

Migración `PreciosVentanaValida`: `CHECK (vigente_hasta IS NULL OR vigente_hasta >=
vigente_desde)` sobre `precios` — backstop de esquema contra un intervalo INVERTIDO para una
escritura cruda/fuera de banda que bypasee el chequeo del item 1.

**Desviación documentada del texto literal aprobado**: el texto gate-aprobado especificaba `>`
estricto. Implementado así, la migración ROMPÍA una regresión de la ronda 1 YA EXISTENTE y
YA PROBADA
(`ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza`/
`ReemplazarUnPendienteConUnaFechaPosteriorALaOriginalNoDejaUnHueco`): el reemplazo de una
pendiente cierra esa fila deliberadamente en su PROPIO `vigente_desde` (ventana VACÍA,
`vigente_hasta == vigente_desde`, no invertida — decisión de diseño documentada desde el primer
commit de esta slice, ver más abajo "Corrección de diseño encontrada y aplicada ANTES del primer
commit") para que nunca se vuelva visible sin borrar la fila. Un `>` estricto rechaza ese estado
legítimo. Corregido a `>=`: sigue atrapando el bug real (un intervalo genuinamente invertido,
`vigente_hasta < vigente_desde`) sin romper la ventana vacía intencional. Confirmado con el
raw-SQL 23514 proof test (item 2) y con las dos corridas completas de la suite (ver abajo).

**Mapeo en `ManejadorDeErrores`**: caso específico (mismo criterio que `ck_clientes_cf_protegido`,
no el genérico por prefijo) — `23514` + `ck_precios_ventana_valida` → 400
`vigente_desde_invalido` (mismo código de dominio que el chequeo de servicio del item 1, título
"vigente_hasta no puede ser anterior a vigente_desde.").

**Test nuevo** (`PreciosEndpointsTests.cs`):
`UnPrecioConVigenteHastaAnteriorAVigenteDesdeViolaLaCheckConstraint` — INSERT crudo por SQL con
`vigente_hasta = now() - 1 día`, `vigente_desde = now()` (intervalo genuinamente invertido),
bypasseando `ServicioDePrecios` por completo; asserts `SqlState == "23514"` y
`ConstraintName == "ck_precios_ventana_valida"`.

`dotnet ef migrations has-pending-model-changes` limpio tras la migración.

### Item 3 — Wording + discoverability (SUGGESTIONS)

- (a) Los dos doc-comments "antes de leer nada" (`ServicioDePrecios.TomarLockDelParAsync` y el
  comentario espejo en `ManejadorDeErrores.ClasificarUnicidad`, rama `_vigente`) pasaron a "antes
  de leer nada de precios" — precisión: el lock/orden de lectura es específico de `precios`, no
  una afirmación general sobre cualquier lectura del sistema.
- (b) `PreciosEndpointsTests.ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza`
  ganó un párrafo en su doc-comment documentando su rol dual: también es la guarda de regresión de
  la exclusión por id en `BuscarPredecesorAsync` (judgment-day ronda 1, item 1) — el caso "primer
  precio del par es directamente un programado, sin predecesor real" donde, sin la exclusión, la
  pendiente recién cerrada matchearía como su propio predecesor.

### Build/test results (judgment-day ronda 2 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 86/86 | 86/86 |
| `Ways.Application.Tests` | 170/170 | 170/170 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 195/195 | 195/195 |

195 = baseline 193 (ronda 1, ver abajo) + 2 nuevos (`ReemplazarUnaPendienteConUnaFechaAnteriorAlPredecesorRechazaCon400SinTocarNada`,
`UnPrecioConVigenteHastaAnteriorAVigenteDesdeViolaLaCheckConstraint`). Build clean (0 warnings, 0
errors), ambas corridas idénticas, sin flakes. Los 2 tests nuevos además verificados estables en 3
corridas aisladas adicionales.

### Commits (work-unit, migración en su propio commit)

1. `feat(persistencia): agregar ck_precios_ventana_valida como backstop de intervalos invertidos` —
   `PrecioConfiguration`, migración `PreciosVentanaValida` + designer + snapshot.
2. `fix(precios): validar el limite del predecesor antes de reabrirlo en un reemplazo pendiente` —
   `ServicioDePrecios.AbrirNuevoPrecioAsync`/`BuscarPredecesorAsync`, mapeo 23514 en
   `ManejadorDeErrores`, tests nuevos, fixes de wording (item 3).

---

## Slice 3 — judgment-day ronda 1: fixes aplicados

Dos jueces ciegos convergieron (money-path code, motor de precios) — 1 CRITICAL con 2 modos de
falla, 1 confirmado (serialización real), 1 confirmado (timestamp post-lock), 1 defensivo
(resolución derivada), 1 batch de sugerencias. Todo corregido en el mismo ciclo, SIN cambios de
esquema.

### Item 1 — Predecessor re-close on pending replacement (CRITICAL, 2 modos de falla)

`AbrirNuevoPrecioAsync`: cuando una fila PENDIENTE se reemplaza (`confirmarReemplazo`), el
PREDECESOR (la fila cuyo `vigente_hasta` == el `vigente_desde` original de la pendiente
reemplazada) quedaba con un límite viejo — fecha nueva ANTERIOR a la original producía
SOLAPAMIENTO (dos filas satisfacían el predicado "vigente" en el rango entre ambas); fecha nueva
POSTERIOR producía un HUECO (ningún precio vigente en ese rango).

**Fix**: `ServicioDePrecios.ReabrirLimiteDelPredecesorAsync` localiza el predecesor por
`vigente_hasta = <vigente_desde original de la pendiente>` y lo re-cierra en el `vigente_desde`
EFECTIVO de la fila nueva, en la MISMA transacción.

**Bug real encontrado y corregido mientras se probaba este mismo fix**: la búsqueda del
predecesor tenía que EXCLUIR explícitamente el id de la fila pendiente recién cerrada — cuando el
reemplazo cierra la pendiente en su ventana muerta (`vigente_hasta == vigente_desde ==
limiteOriginal`), esa MISMA fila también matchea `vigente_hasta = limiteOriginal`, así que sin la
exclusión la pendiente se reabría a sí misma en el nuevo límite. Detectado porque hizo
REGRESIONAR un test YA EXISTENTE (`ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza`,
task 3.8) antes de agregar `id_precio != $5` a la query. Corregido antes del commit — nunca llegó
código malo a main.

**Tests nuevos** (`PreciosEndpointsTests.cs`):
- `ReemplazarUnPendienteConUnaFechaAnteriorALaOriginalRecierraElPredecesorSinSolapar` (secuencia
  a: inmediato → programado → inmediato-con-reemplazo, fecha nueva ANTERIOR): verifica que el
  predecesor se re-cierra en la fecha del reemplazo, no en t+3d, y que exactamente una fila
  satisface el predicado "vigente" en cada instante sondeado (el probe crítico es el punto medio
  del solapamiento viejo, donde ANTES coincidían dos filas).
- `ReemplazarUnPendienteConUnaFechaPosteriorALaOriginalNoDejaUnHueco` (secuencia b: inmediato →
  programado(t+3d) → programado(t+10d)-con-reemplazo, fecha nueva POSTERIOR): verifica que la
  consulta en el punto medio de la ventana [t+3d, t+10d) devuelve el precio ORIGINAL (no `null`),
  y que el predecesor cierra en t+10d.

### Item 2 — True serialization of concurrent writes (CONFIRMED, ambos jueces)

`SELECT ... FOR UPDATE` sobre la fila mutable solo podía lockear una fila YA EXISTENTE — para el
primer precio de un par no había nada que bloquear, así que dos altas concurrentes competían
directo contra `ux_precios_vigente` en el `INSERT` (task 3.11's "un ganador + un 409").

**Fix**: `AbrirNuevoPrecioAsync` toma un `pg_advisory_xact_lock` determinístico por par
(`idTenant`, `idArticulo`, `idListaPrecio`) PRIMERO en la transacción
(`TomarLockDelParAsync`/`ClaveDeLockDePar`), y recién ahí lee la fila abierta con un SELECT plano
(`BuscarFilaAbiertaAsync`, sin `FOR UPDATE` — ya no hace falta, el advisory lock ya serializa).
Esto da la semántica real de "esperar y actuar sobre el estado actual" que el doc-comment del
método siempre prometió, para CUALQUIER escritura sobre el mismo par (exista o no una fila
abierta todavía).

**Derivación de la clave** (`ClaveDeLockDePar`): `clave1 = idTenant` (cada tenant ocupa su propio
subespacio, sin motivo de colisión entre tenants); `clave2 = unchecked((idArticulo * 397) ^
idListaPrecio)` — combinación aritmética simple, DELIBERADAMENTE no `HashCode.Combine`: ese
incorpora una semilla aleatoria por PROCESO, así que dos instancias de la app (o la misma tras un
reinicio) calcularían claves DISTINTAS para el MISMO par, y el lock dejaría de serializarlas entre
sí — exactamente lo opuesto de lo que se busca. Una colisión de `clave2` entre dos pares
DISTINTOS del mismo tenant es tolerable: el peor caso es serializar de más (dos pares no
relacionados se esperan entre sí sin necesidad) — nunca una lectura incorrecta, porque el estado
real siempre se lee de la fila DESPUÉS de tomar el lock, nunca del hash en sí.

**Consecuencia honesta**: con el advisory lock, la carrera de `ux_precios_vigente` YA NO es
alcanzable por el camino de servicio — el backstop de esquema se mantiene igual, pero solo queda
alcanzable por una escritura cruda/fuera de banda (misma familia que `PK_articulos_empresas`,
Slice 2 judgment-day ronda 2). Comentarios actualizados en
`ManejadorDeErrores.ClasificarUnicidad` (rama `_vigente`) y en el doc-comment de
`AbrirNuevoPrecioAsync` reflejando esto.

**Tests** (`PreciosEndpointsTests.cs`):
- (a) adaptado: `LaCreacionConcurrenteDeDosPrimerosPreciosDaExactamenteUnGanador` →
  `LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden` — ahora afirma 2×201 (no
  1×201+1×409), historial de 2 filas con la cadena correcta. El rendezvous determinístico
  (`InterceptorDeRendezVousListasPrecio`) se mantiene para forzar overlap real de transacciones.
- (b) NUEVO: `LaModificacionConcurrenteDeUnPrecioYaExistenteSeSerializaYAmbosSuceden` — dos
  cambios inmediatos concurrentes sobre un par YA priceado: ambos 201, historial de 3 filas con
  cadena consistente (una cierra a la otra, ninguna con 409).
- Ambos verificados estables en 3 corridas aisladas adicionales.

### Item 3 — Post-lock timestamp (CONFIRMED)

`EstablecerPrecioAsync` capturaba `reloj.Ahora` ANTES de entrar a la transacción/lock y lo pasaba
como `vigenteDesde`. Bajo contención, un llamador que esperaba el lock podía terminar con un
`vigente_desde` MÁS VIEJO que el de la fila que ya ganó la carrera y confirmó, disparando un
`vigente_desde_invalido` espurio.

**Fix**: `AbrirNuevoPrecioAsync.vigenteDesde` pasó a `DateTimeOffset?` — `null` (caso inmediato)
se resuelve con `reloj.Ahora` capturado DESPUÉS del `TomarLockDelParAsync`; un valor (caso
programado) se sigue honrando tal cual. La misma `ahora` post-lock se usa para el cierre del
predecesor/fila actual y para `CreatedAt`/`UpdatedAt`. Cubierto indirectamente por la estabilidad
de las 3 corridas aisladas del test de carrera del item 2(b) — no se armó un rendezvous dedicado
adicional (habría duplicado el mismo mecanismo).

### Item 4 — Derived resolution hardening (DEFENSIVE)

`ResolvedorDePrecios.ResolverPrecioDerivado` ahora rechaza un resultado negativo (p.ej.
`porcentaje` menor a -100%) con `ErrorDominio("precio_derivado_invalido", ..., 422)` en vez de
devolver un número negativo silencioso. `ServicioDePrecios.ResolverPrecioAsync` reemplazó
`lista.Porcentaje!.Value` por un `??` explícito que lanza el MISMO código de dominio en vez de un
`NullReferenceException` crudo.

**Obligación hacia adelante (Slice 4)**: `ServicioDeListasPrecio` tiene que rechazar `porcentaje
<= -100` al ESCRIBIR la lista — registrado en `state.yaml`. Esta guarda de lectura queda como
defensa en profundidad, mismo criterio que la guarda de profundidad-1 de listas derivadas.

**Tests nuevos**:
- `ResolvedorDePreciosTests.UnPorcentajeMenorAMenos100DaUnPrecioNegativoYSeRechaza` (Domain).
- `ServicioDePreciosResolucionTests` (Application, InMemory, archivo nuevo) —
  `UnaListaDerivadaSinPorcentajeConfiguradoDaUnErrorDeDominioLimpio` y
  `UnPorcentajeMenorAMenos100PropagaElErrorDeDominioDelPrecioDerivado`.

### Item 5 — Cobertura y consistencia (SUGGESTIONS)

- (a) `ConsultarPreciosDeUnArticuloRealDeOtroTenantDevuelve404EnLasTresRutasDeGet` — cross-tenant
  con un id de artículo REAL de otro tenant (no inexistente) en las tres rutas GET de precios.
- (b) La divergencia entre `PrecioVigenteAsync` (resuelve cualquier lista por id explícito,
  activa o no) y `PreciosVigentesAsync` (filtra `Activo`) se mantuvo DELIBERADA y se documentó en
  ambos doc-comments; `UnaListaInactivaResuelvePorIdExplicitoPeroNoApareceEnElListadoDeTodas`
  prueba el comportamiento explícitamente.
- (c) El N+1 de `PreciosVigentesAsync` ganó un comentario INFO marcándolo para la etapa POS
  (etapa 5) — no se refactorizó.

### Build/test results (judgment-day ronda 1 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 86/86 | 86/86 |
| `Ways.Application.Tests` | 170/170 | 170/170 |
| `Ways.IntegrationTests` (real Postgres) | 193/193 | 193/193 |

86 = baseline 85 + 1 nuevo (`ResolvedorDePreciosTests`). 170 = baseline 168 + 2 nuevos
(`ServicioDePreciosResolucionTests`). 193 = baseline 188 + 5 netos (2 secuencias temporales + 1
test de carrera nuevo + 2 de cobertura del item 5; el test de carrera del item 2a se ADAPTÓ, no
sumó). Build clean (0 warnings, 0 errors), ambas corridas idénticas, sin flakes — los 4
tests nuevos/adaptados de temporal-sequence/carrera además verificados estables en 3 corridas
aisladas adicionales.

### Commit (work-unit, pendiente de listar acá tras crearlo)

`fix(precios): re-cerrar el predecesor en reemplazos pendientes y serializar con advisory lock` —
la ronda 1 completa de judgment-day sobre Slice 3 (items 1-5), en un solo work unit porque los
items 1-3 comparten el mismo método (`AbrirNuevoPrecioAsync`) y los items 4-5 son cambios
acotados sobre el mismo archivo de servicio/tests.

---

## Slice 3: Precios (PR 3) — DONE, ready for judgment-day

**Branch**: `feat/stage3-slice3-precios` (off `main`, PR 1 y PR 2 ya mergeados — no push/PR
todavía).

All 14 Slice 3 tasks (3.1–3.14) complete. NO database changes — schema ya existía desde Slice 1
(`ArticulosYPreciosEtapa3`), confirmado antes de empezar.

### What shipped this slice

**Domain** (`Ways.Domain.Precios`):
- `ResolvedorDePrecios.ResolverPrecioDerivado(precioBase, porcentaje)` (task 3.1) — función pura,
  `Math.Round(precioBase * (1 + porcentaje / 100m), 2, MidpointRounding.AwayFromZero)`, mismo
  criterio de redondeo que `SugeridorDePrecio` (Slice 2).

**Application** (`Ways.Application.Precios`, paquete nuevo):
- `ServicioDePrecios` (tasks 3.2/3.3): `AbrirNuevoPrecioAsync` es el ÚNICO punto de escritura de
  `precios` (design decision 3) — una sola transacción (`CreateExecutionStrategy` +
  `BeginTransactionAsync`, mismo patrón que `ServicioDeArticulos.CrearAsync`): bloquea la fila
  actualmente abierta del par `(articulo, lista)` con `SELECT ... FOR UPDATE` vía ADO.NET crudo
  (EF Core/Npgsql no tiene un equivalente mapeado — nunca `FromSqlRaw<T>()`, mismo motivo
  histórico que `AsignadorDeNumeroCliente`), decide si hace falta `confirmarReemplazo` (fila
  pendiente, `vigente_desde > ahora`), la cierra, e inserta la fila nueva. `EstablecerPrecioAsync`
  (inmediato, `vigente_desde = reloj.Ahora`) y `ProgramarPrecioAsync` (futuro, valida tolerancia
  de reloj) son envoltorios delgados sobre el mismo método.
  - **Corrección de diseño encontrada y aplicada ANTES del primer commit**: al reemplazar una fila
    PENDIENTE (`confirmarReemplazo: true`), la fila reemplazada se cierra en su PROPIO
    `vigente_desde` (ventana vacía, `vigente_hasta == vigente_desde`), NO en el `vigente_desde` de
    la fila nueva. Cerrarla ahí habría dejado el precio "reemplazado" brevemente VISIBLE entre su
    fecha original y la fecha nueva si esta última es posterior — exactamente lo que el spec dice
    que NO tiene que pasar ("the $150 pending row is REPLACED by the $160 one", no "vigente hasta
    que el nuevo empiece"). Para la fila ACTIVA (no pendiente) el criterio es el opuesto y
    correcto: se cierra en el `vigente_desde` de la fila nueva, porque esa fila SÍ estuvo vigente
    hasta ese momento. Cubierto por
    `PreciosEndpointsTests.ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza`
    (verifica explícitamente que la ventana intermedia da `null`).
  - `PrecioVigenteAsync`/`PreciosVigentesAsync`/`HistorialDePrecioAsync` (lectura): `fija` resuelve
    por consulta filtrada por fecha; `derivada` resuelve la base y aplica
    `ResolvedorDePrecios.ResolverPrecioDerivado` — guarda de profundidad 1 (orchestrator decision
    2) aplicada acá TAMBIÉN en lectura como defensa en profundidad (la escritura la bloquea
    `ServicioDeListasPrecio` recién en la Slice 4).
  - Contratos (task 3.4): `AltaPrecio`/`ProgramarPrecio` (sin `Id` — nunca hay edición de una fila
    existente) / `PrecioVigente` / `HistorialDePrecio`.

**API** (`Ways.Api.Endpoints.ArticulosEndpoints`):
- Precios nidificados bajo `/api/articulos/{id}/precios*` (task 3.5, folded in, no recurso
  top-level): `POST /precios` (inmediato), `POST /precios/programados` (futuro),
  `GET /precios` (todas las listas activas a una fecha), `GET /precios/{idListaPrecio}` (una
  lista), `GET /precios/{idListaPrecio}/historial`. Mismo grupo/policy `GestionDeCatalogo` que el
  resto de `ArticulosEndpoints`.
- `ManejadorDeErrores.ClasificarUnicidad`: la exención de la prueba de carrera de
  `precio_vigente_duplicado` (`_vigente`, desde Slice 1 task 1.10) CIERRA acá — ver el hallazgo de
  alcanzabilidad honesto abajo.

### Hallazgo honesto de alcanzabilidad de la carrera (task 3.11, db-error-backstops)

La carrera de `ux_precios_vigente` (dos primeros precios concurrentes para el mismo par, sin fila
que lockear) es GENUINA por construcción — pero un `Task.WhenAll` desnudo sobre 2 `POST` NO la
reproduce de forma confiable. Probado empíricamente: 5/5 corridas AISLADAS (un solo test por
corrida) exponen el 409 esperado, pero 3/3 corridas de la CLASE COMPLETA (14 tests) dieron 2×201
sin ninguna excepción. Causa: con el pool de conexiones/JIT ya "calientes" (el caso real de
`dotnet test` sobre la suite completa, nunca un test aislado), el segundo request tiende a
completar su `BEGIN` + `SELECT ... FOR UPDATE` DESPUÉS de que el primero ya hizo `COMMIT` — en ese
caso el segundo ve la fila recién confirmada y hace un cierre-y-apertura LEGÍTIMO en vez de
chocar contra el índice. Mismo mecanismo de fondo que el hallazgo de `ParametrosTests`
(judgment-day, slice 3 ronda 2 — el "ganador" puede terminar su `SELECT+INSERT+commit` antes de
que el segundo arranque su propia `SELECT`).

**Resuelto con el mismo fix que `ParametrosTests`**: un rendezvous determinístico vía
`DbCommandInterceptor` (`PreciosEndpointsTests.InterceptorDeRendezVousListasPrecio`, mismo
mecanismo que `ParametrosTests.InterceptorDeRendezVous`) que retiene las dos primeras consultas EF
a `listas_precio` — la ÚLTIMA consulta interceptable por EF antes de que `AbrirNuevoPrecioAsync`
abra su transacción y haga el `SELECT ... FOR UPDATE` crudo (que por ser ADO.NET puro, fuera del
pipeline de comandos de EF Core, NO es interceptable directamente) — hasta que ambas llegaron,
forzando que las dos transacciones arranquen al mismo tiempo. Verificado estable en 3 corridas de
la clase completa tras el fix (14/14 las tres veces).

### Tests

- `tests/Ways.Domain.Tests/Precios/ResolvedorDePreciosTests.cs` — 4 casos (task 3.6): descuento
  negativo, recargo positivo, recálculo tras cambio de base, empate de redondeo AwayFromZero.
- `tests/Ways.Application.Tests/Precios/ServicioDePreciosSuperficieTests.cs` — 3 casos (task 3.13,
  reflexión pura, sin DB): ningún método público con nombre de edición
  (Actualizar/Editar/Modificar), ningún método de escritura recibe un `idPrecio`/`IdPrecio`
  existente, ningún contrato de alta expone un `Id`/`IdPrecio` — la única forma de escribir es
  ABRIR una fila nueva.
- `tests/Ways.IntegrationTests/PreciosEndpointsTests.cs` — 14 casos (tasks 3.7–3.12, real
  Postgres):
  - 3.7: un cambio de precio cierra la fila vieja y abre una nueva (`vigente_hasta` de la vieja ==
    `vigente_desde` de la nueva); el historial completo queda consultable tras varios cambios.
  - 3.8: programar sin pendiente previo sucede sin afectar el precio vigente; programar un segundo
    pendiente sin confirmar → 409; con `confirmarReemplazo: true` reemplaza, y el precio
    reemplazado NUNCA se vuelve visible (ni en la ventana intermedia entre las dos fechas — la
    prueba directa de la corrección de diseño de arriba).
  - 3.9: consulta por fecha resuelve el precio vigente (hoy) o uno histórico (fecha pasada).
  - 3.10: una lista derivada resuelve su precio desde la base y sigue propagando cambios sin
    ningún write adicional (`db.Precios.CountAsync` de la derivada == 0); establecer un precio
    sobre una derivada → 400 `lista_no_es_fija`.
  - 3.11: la carrera genuina (rendezvous determinístico) → exactamente 1×201 + 1×409
    `precio_vigente_duplicado`.
  - 3.12: FK smoke — artículo inexistente → 404 (ADR-8); lista inexistente/de otro tenant → 400
    `referencia_invalida`.
  - Adicionales de scope (bounds, tolerancia de reloj, autorización): precio negativo → 400
    `precio_invalido`; `vigente_desde` en el pasado más allá de la tolerancia → 400
    `vigente_desde_en_el_pasado`; vendedor no puede establecer un precio → 403.

### Build/test results (this slice, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 85/85 | 85/85 |
| `Ways.Application.Tests` | 168/168 | 168/168 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 188/188 | 188/188 |

85 = 81 baseline (Slice 1+2 + judgment-day) + 4 nuevos (`ResolvedorDePreciosTests`). 168 = 165
baseline + 3 nuevos (`ServicioDePreciosSuperficieTests`). 188 = 174 baseline + 14 nuevos
(`PreciosEndpointsTests`). Idéntico en ambas corridas, sin flakes — el test de carrera además
verificado estable en 3 corridas adicionales de la clase completa tras el fix del rendezvous (ver
arriba).

### Commits (work-unit style, on `feat/stage3-slice3-precios`)

1. `feat(precios): agregar ResolvedorDePrecios (resolucion pura de precio derivado)` — función de
   dominio + sus tests unitarios.
2. `feat(precios): agregar ServicioDePrecios (motor de historial, precios programables, resolucion derivada)` —
   contratos, servicio, DI, tests de superficie por reflexión (task 3.13).
3. `feat(precios): exponer los endpoints de precios y cerrar el backstop de ux_precios_vigente` —
   endpoints, actualización de `ManejadorDeErrores`, tests de integración (incluye el rendezvous
   determinístico para la carrera).

### Next

Slice 3 está completa y verificada en runtime. Judgment-day (revisión dual ciega) corre a
continuación, según el protocolo de PR solo-dev, antes de crear este PR. Slice 4 (`listas_precio`
ABM) arranca recién cuando el PR de Slice 3 mergea.

## Slice 2: Artículos + codigos_barra + articulos_empresas + Margin Suggestion (PR 2) — DONE, ready for judgment-day

**Branch**: `feat/stage3-slice2-articulos` (off `main`, PR 1 already merged — no push/PR yet).

All 13 Slice 2 tasks (2.1–2.13) complete. NO database changes — schema already existed from
Slice 1 (`ArticulosYPreciosEtapa3`), confirmed before starting.

### What shipped this slice

**Domain** (`Ways.Domain.Precios`):
- `SugeridorDePrecio.Sugerir(costoNominal, costoLista, descuentoProveedor, margenGrupo,
  margenProveedor)` — pure static function (design decision 8, task 2.1). Base cost:
  `costoNominal` when present, else `costoLista * (1 - descuentoProveedor / 100)`. **Bug found
  and fixed during unit testing**: `descuento_proveedor` is a PERCENTAGE (same 0-100 scale as
  `margen`, column `numeric(5,2)`), not a raw 0-1 fraction — the first implementation divided
  by 100 for the margin but not for the discount, producing wildly wrong (negative) suggested
  prices whenever `costo_lista` + `descuento_proveedor` were used together. Caught by
  `SinCostoNominalUsaCostoListaMenosElDescuento` failing with `-5760` instead of `180`. Fixed
  before the first commit — no bad code ever landed.

**Application** (`Ways.Application.Articulos`):
- `ServicioDeArticulos` (task 2.2): list (search by nombre/codigo_interno/barcode,
  availability-aware via `idEmpresa` filter)/create/edit/soft-delete. `codigo_interno`:
  caller-supplied (pre-checked unique, normalized ≤30 chars) or autogenerated via
  `AsignadorDeCodigoInternoArticulo` inside a transaction (mirrors
  `ServicioDeClientes.CrearAsync` exactly — same transaction-blocked-provider caveat, so
  `CrearAsync`'s full happy path is untestable on InMemory, only its pre-transaction
  validations are). `codigo_interno` is immutable after creation (not in `EdicionArticulo`,
  same precedent as `Cliente.Numero`). Availability guard
  (`ReglaDeArticulos.ValidarRestriccionDeDisponibilidad`) applies on BOTH create (treating the
  domain default `true` as the implicit "before" state) and edit. `ArticuloEmpresa.IdTenant` is
  stamped MANUALLY (the Slice 1 carried obligation from `state.yaml`) — it doesn't inherit
  `EntidadTenant`, so the generic `EstamparTenant` loop never reaches it; RLS `WITH CHECK` is
  the backstop if this is ever missed.
- `AgregarCodigoBarraAsync`/`EliminarCodigoBarraAsync` folded into `ServicioDeArticulos` (task
  2.3, "implementer's call" — documented in the class doc-comment): barcodes have no
  authorization/lifecycle of their own, a separate `ServicioDeCodigosBarra` would only add a
  second injected service with no cohesion benefit. Barcode removal is a soft-delete (code
  becomes reusable after, same precedent as a de-activated proveedor's `cuit`).
- `SugerirPrecioAsync`: resolves `grupos.margen`/`proveedores.margen` from the artículo's own
  references, delegates the pure calculation to `SugeridorDePrecio`. Never persists a `precios`
  row (design decision 8 — "called but never auto-applied").
- `ArticuloConsultas.DisponibleEnEmpresa` (design: "Availability resolution" query extension):
  `IQueryable<Articulo>` extension — `DisponibleParaTodas || EXISTS(articulos_empresas ...)`,
  correlated subquery, reused today by `ListarAsync` and reusable by the future POS catalog
  query (stage 5).
- Contracts (task 2.4): `AltaArticulo`/`EdicionArticulo`/`ArticuloListado` (no embedded
  `CodigosBarra`/`IdsEmpresas` collections — mirrors `ClienteListado`/`ProveedorListado`'s flat
  shape; availability visibility is tested via the `idEmpresa` list filter, not by exposing the
  subset ids), `AltaCodigoBarra`/`CodigoBarraListado`, `SugerenciaDePrecio` (single
  `PrecioSugerido: decimal?` field — kept minimal, `null` when there's insufficient cost/margin
  data to suggest anything).
- `IWaysDbContext` gained `Articulos`/`CodigosBarra`/`ArticulosEmpresas`/`Precios` DbSets — first
  Application consumer of these Slice-1-modeled entities (`NumeracionesArticulos` deliberately
  NOT exposed: its only legitimate writer, `AsignadorDeCodigoInternoArticulo`, already receives
  `IWaysDbContext` by parameter and only needs `.Database`, same precedent as
  `NumeracionCliente`'s counter).

**API** (`Ways.Api.Endpoints.ArticulosEndpoints`, `Ways.Api.Program`):
- `/api/articulos` (list/create/edit/soft-delete), `/api/articulos/{id}/codigos-barra`
  (add/remove sub-routes), `/api/articulos/{id}/sugerencia-precio` (read-only) — all under
  `Politicas.GestionDeCatalogo` (tenant admin only, ADR pattern from clientes/proveedores).
  Availability toggle rides the edit (PUT) endpoint's `DisponibleParaTodas`/`IdsEmpresas`
  fields, not a separate endpoint — design.md/spec never called for one, and folding it in
  keeps the surface minimal (mirrors how `ServicioDeClientes`'s `Activo` toggle also rides the
  edit endpoint).
- `ManejadorDeErrores.ClasificarUnicidad`: the Slice 1 race-test EXEMPTION comments on
  `codigo_interno_duplicado`/`codigo_barra_duplicado` are now CLOSED — `ServicioDeArticulos` is
  the real write path this slice, and the deferred race tests (2.8/2.9) landed. Comment
  rewritten to document which race is GENUINE: `codigo_interno`'s autogenerated path is
  counter-serialized (same as `clientes.numero` — no real race reaches the backstop), so the
  genuine race is the USER-SUPPLIED `codigo_interno` (same family as `ux_proveedores_cuit`, not
  `ux_clientes_numero`); `codigo_barra` is ALWAYS a genuine race (client-supplied, no counter at
  all). This mirrors the `db-error-backstops` skill's vacuous-test lesson explicitly.

### Tests

- `tests/Ways.Domain.Tests/Precios/SugeridorDePrecioTests.cs` — 9 cases (task 2.6): grupo wins
  over proveedor, falls back to proveedor when grupo margin absent, `costo_nominal` precedence
  over `costo_lista * (1 - descuento)`, no-descuento default, no-costo-base/no-margen → `null`,
  AwayFromZero rounding on a tie.
- `tests/Ways.Application.Tests/Articulos/ServicioDeArticulosTests.cs` — 19 cases (task 2.7,
  InMemory, same transaction-blocked-provider caveat as `ServicioDeClientesTests`): required
  field validation, invalid clasificador/alicuota reference → 400 (including cross-tenant),
  availability guard on create AND edit, `codigo_interno` duplicate pre-check, cross-tenant 404
  (`ObtenerAsync`), full `ActualizarAsync`/`EliminarAsync`/barcode add-remove/`SugerirPrecioAsync`
  happy paths (none of these open a transaction, so InMemory covers them completely, unlike
  `CrearAsync`'s full happy path).
- `tests/Ways.IntegrationTests/ArticulosEndpointsTests.cs` — 23 cases (tasks 2.8–2.12, real
  Postgres):
  - 2.8: concurrent autogenerated `codigo_interno` (no gaps/dupes, no backstop exposed),
    user-supplied honored, duplicate → 409, concurrent duplicate user-supplied race → exactly
    1×201 + 1×409 SQLSTATE-asserted via the translated domain code.
  - 2.9: same barcode across 2 tenants allowed, same-tenant duplicate → 409, concurrent
    duplicate-barcode race → 1×201 + 1×409, remove-without-affecting-articulo.
  - 2.10: default-true visible to a later empresa, explicit-false subset excludes, cross-tenant
    empresa in subset → 400 `referencia_invalida`.
  - 2.11: FK smoke (`fk_articulos_area` nonexistent, `fk_articulos_categoria` cross-tenant,
    `fk_articulos_alicuota_iva` nonexistent → 400; `fk_codigos_barra_articulo` nonexistent
    articulo → 404 via `BuscarAsync`, not 400 — ADR-8 takes precedence for the sub-route's
    parent resource).
  - 2.12: admin create→soft-delete round trip, vendedor 403 on create/barcode-add/PUT
    (disponibilidad), ADR-8 404 uniform on GET/PUT/DELETE cross-tenant, margin suggestion never
    persists a `precios` row.
- **Gotcha found and fixed while writing the integration tests**: `ReadFromJsonAsync<T>()`/
  `GetFromJsonAsync<T>()` without explicit `JsonSerializerOptions` use the CLIENT's default
  options, which do NOT include `JsonStringEnumConverter` — the server (`Program.cs`) registers
  it, so `UnidadVenta` serializes as a string (`"Unidad"`), but the client's default numeric
  enum reader chokes on it. This is the exact same gotcha `CatalogosTests`/`OrganizacionTests`
  already document as `OpcionesJson` (their comment explains it never showed up before because
  every OTHER nullable-enum DTO in prior test suites happened to be `null` in the fixtures used —
  `ArticuloListado.UnidadVenta` is the first NON-nullable enum actually read back over HTTP in a
  new test file). Fixed by adding the same `OpcionesJson` static field
  (`PropertyNameCaseInsensitive = true` + `JsonStringEnumConverter()`) and passing it to every
  `ArticuloListado`/`PaginaDe<ArticuloListado>` deserialization.

### Build/test results (this slice, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 82/82 | 82/82 |
| `Ways.Application.Tests` | 161/161 | 161/161 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 168/168 | 168/168 |

82 = 74 Slice-1 baseline + 8 new (`SugeridorDePrecioTests`, one extra case beyond the 3 spec
scenarios: a `null`-when-no-descuento sanity check landed as its own fact,
`SinDescuentoProveedorElCostoListaSeUsaSinDescontar`, hence 8 not 3 — plus the AwayFromZero
rounding case = the domain suite grew by 8, not exactly "3 scenarios" 1:1, all still traced to
task 2.6's 3 named scenarios plus edge cases the spec doesn't cover with a scenario of its own).
161 = 142 Slice-1 baseline + 19 new (`ServicioDeArticulosTests`). 168 = 145 Slice-1 baseline + 23
new (`ArticulosEndpointsTests`). Identical counts both runs, no flakes; a third full-solution run
also confirmed the same counts (three runs total, all green).

### Commits (work-unit style, on `feat/stage3-slice2-articulos`)

1. `feat(precios): agregar SugeridorDePrecio (sugerencia de precio pura)` — domain function +
   its unit tests.
2. `feat(articulos): agregar ServicioDeArticulos (ABM, codigos_barra, sugerencia de precio)` —
   `IWaysDbContext` additions, contracts, query extension, the service itself, DI registration +
   its Application unit tests.
3. `feat(articulos): exponer ArticulosEndpoints y cerrar los backstops de carrera` — endpoints,
   `Program.cs` wiring, `ManejadorDeErrores` exemption-comment closure + integration tests.

### Next

Slice 2 is complete and runtime-verified. Judgment-day (dual blind review) runs next, per the
solo-dev PR protocol, before this PR is created. Slice 3 (`precios` — history engine) starts
only after Slice 2's PR merges.

## Slice 2 — judgment-day ronda 1: fixes aplicados

Dos CRITICAL confirmados por AMBOS jueces ciegos, con la MISMA causa raíz. Un tercer hallazgo
confirmado (dedup) y un batch de sugerencias de higiene. Todo corregido en el mismo ciclo, sin
cambios de esquema.

### Root cause (items 1+2, un solo fix)

`ReglaDeArticulos.ValidarRestriccionDeDisponibilidad` validaba la TRANSICIÓN
(`disponibleParaTodasActual && !disponibleParaTodasNuevo`), no el ESTADO RESULTANTE. Un
artículo YA restringido (`disponible_para_todas = false`) que se volvía a guardar con
`IdsEmpresas` en `null` (edición false -> false, sin cambiar el flag) esquivaba el guard —
`pasaDeDisponibleATodasARestringido` daba `false && true = false` — y reventaba más abajo, en
`ServicioDeArticulos.ExigirEmpresasValidasAsync`, con un `NullReferenceException` sin traducir
(500 crudo) al iterar `datos.IdsEmpresas!` nulo.

**Fix**: `ValidarRestriccionDeDisponibilidad(bool disponibleParaTodasNuevo, int
cantidadDeFilasSubset)` — perdió el parámetro `disponibleParaTodasActual` y ahora dispara
siempre que el estado NUEVO sea `false` con `cantidadDeFilasSubset == 0`, sin importar el
estado anterior. Código de dominio renombrado de `disponibilidad_restriccion_sin_subset` a
`subset_de_empresas_requerido` (más específico sobre qué falta). `IdsEmpresas: null` y
`IdsEmpresas: []` ahora dan el mismo 400 — ninguno de los dos casos llega a
`ExigirEmpresasValidasAsync`/al `RemoveRange` del subset existente, porque la excepción se
dispara ANTES en el método (ni siquiera se llega a tocar `ArticulosEmpresas`), así que un PUT
rechazado nunca borra el subset vigente.

- `src/Ways.Domain/Articulos/ReglaDeArticulos.cs` — firma nueva, doc-comment reescrito
  documentando la causa raíz.
- `src/Ways.Application/Articulos/ServicioDeArticulos.cs` (`CrearAsync`/`ActualizarAsync`) —
  llamadas actualizadas al nuevo shape.
- Item 2 (companion): `ObtenerAsync` ahora expone el subset actual en el DTO
  (`ArticuloListado.IdsEmpresas`, vacío cuando `DisponibleParaTodas = true`) — un cliente HTTP
  puede releer el detalle y reenviarlo tal cual sin perder el subset. `ListarAsync` deja el
  campo vacío por fila a propósito (evita un N+1 de una query por artículo listado);
  `CrearAsync`/`ActualizarAsync` también lo completan porque ya conocen el subset resuelto de
  la operación.

### Item 3 — Dedup de `IdsEmpresas`

`.Distinct()` sobre la lista entrante ANTES de validar/insertar, en `CrearAsync` y
`ActualizarAsync` — un payload `[5,5]` ahora inserta UNA sola fila, sin error. Defensa en
profundidad: `ManejadorDeErrores` gana un mapeo `PK_articulos_empresas` (match
case-insensitive, por la convención PascalCase por default de EF vs. el resto del esquema en
snake_case) → 409 `empresa_duplicada_en_subset`, para cualquier duplicado que esquive el
`.Distinct()` (p.ej. una carrera entre dos PUT concurrentes).

### Item 4 — Higiene (sugerencias)

- (a) `ExigirCodigoInternoDisponibleAsync` perdió el parámetro muerto `excluirId` —
  `codigo_interno` es inmutable, el único llamador (`CrearAsync`) nunca lo necesitaba.
- (b) Nuevo test HTTP: vendedor 403 en el DELETE de `codigos-barra` (ya existía para el POST).
- (c) Nuevos tests HTTP: 404 cross-tenant (ADR-8) en POST y DELETE de `codigos-barra` con un
  artículo padre de otro tenant (antes solo había un caso "no existe en absoluto", no
  "existe pero es de otro tenant").

### Tests nuevos (todos verdes)

- `tests/Ways.Domain.Tests/Articulos/ReglaDeArticulosTests.cs` — reescrito a la firma nueva;
  ganó `MantenerRestringidoSinFilasDeSubsetEsRechazado` documentando el caso que antes NO
  lanzaba (el bug).
- `tests/Ways.Application.Tests/Articulos/ServicioDeArticulosTests.cs` — 4 casos nuevos:
  `EditarUnArticuloYaRestringidoConIdsEmpresasNuloEsRechazado`,
  `EditarUnArticuloYaRestringidoConIdsEmpresasVacioEsRechazadoYNoBorraElSubset`,
  `EditarConIdsEmpresasDuplicadosInsertaUnaSolaFila`,
  `ObtenerUnArticuloRestringidoDevuelveSuSubsetDeEmpresas`.
- `tests/Ways.IntegrationTests/ArticulosEndpointsTests.cs` — 4 casos nuevos:
  `UnPutSobreUnArticuloYaRestringidoConIdsEmpresasNuloDevuelve400`,
  `UnVendedorNoPuedeEliminarCodigosDeBarra`, `AgregarCodigoBarraAUnArticuloDeOtroTenantDevuelve404`,
  `EliminarCodigoBarraDeUnArticuloDeOtroTenantDevuelve404`.

### Build/test results (judgment-day ronda 1 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 81/81 | 81/81 |
| `Ways.Application.Tests` | 165/165 | 165/165 |
| `Ways.IntegrationTests` (real Postgres) | 172/172 | 172/172 |

81 = baseline 82 - 1 (`ReglaDeArticulosTests` perdió un caso redundante al perder el parámetro
`disponibleParaTodasActual`: la vieja `AmpliarDeRestringidoATodasNuncaExigeSubset` quedó
idéntica a `MantenerDisponibleParaTodasSinSubsetEsPermitido` sin ese parámetro, así que se
retiró en vez de duplicarla). 165 = baseline 161 + 4 nuevos. 172 = baseline 168 + 4 nuevos.
Build clean (0 warnings, 0 errors), ambas corridas idénticas, sin flakes.

### Commit (work-unit, pendiente de listar acá tras crearlo)

`fix(articulos): validar el estado resultante de disponibilidad y deduplicar el subset de
empresas` — la ronda 1 completa de judgment-day sobre Slice 2 (items 1-4), en un solo work
unit porque los items 1 y 2 comparten la misma causa raíz y el mismo archivo de servicio.

---

## Slice 1: Schema, Domain Foundation & Counter (PR 1) — DONE, judgment-day round 1 clean

**Branch**: `feat/stage3-slice1-schema` (off `main`, no push/PR yet).

## Batch 2 — Post-gate: migration, RLS proofs, counter concurrency

DB CHANGE GATE (task 1.1) was approved 2026-08-02 exactly as presented in batch 1: 5 new
tables, `unidad_venta` enum, standard RLS on all 5, the 4 additive alternate keys on existing
tables, the AK naming convention call, `Precio.Monto`, no seed/backfill. The deferred
price-resolution functions (`SugeridorDePrecio`/`ResolverPrecioDerivado`) staying in Slices
2/3 per tasks.md was also confirmed correct — no change to that call.

### What shipped this batch

**Migration** (task 1.8):
- `UnidadVenta` registered in the three enum-mapping surfaces the gate summary called for:
  `WaysDbContextFactory` (design-time), `DependencyInjection.ConfigurarNpgsql` (production),
  and all three enum-list spots in `WaysApiFixture` (owner/app/`CrearContextoDeAplicacion`).
- First scaffold caught a real convention violation before it shipped: `dotnet ef migrations
  add` produced an auto-named PascalCase support index,
  `IX_articulos_empresas_id_articulo_id_tenant`, because `ArticuloEmpresaConfiguration` was
  missing an explicit `HasIndex` for `fk_articulos_empresas_articulo`'s `(IdArticulo,
  IdTenant)` columns — every *other* composite FK in this batch already had one. Fixed the
  config (added `ix_articulos_empresas_articulo`), deleted the bad scaffold by hand (no local
  Postgres for `ef migrations remove` — its own connection-string fallback isn't a live
  server), and regenerated cleanly. No `Ignore<T>()` isolation was needed: nothing beyond
  this gate's approved model exists in code yet (unlike stage 1, which had 3 gates' worth of
  entities modeled simultaneously), so the scaffold diff was exactly the 5 tables + 4 alternate
  keys + enum, nothing more.
- `HabilitarRlsDeTenant` hand-added at the end of `Up()` for the 5 new tables (`articulos`,
  `articulos_empresas`, `codigos_barra`, `numeraciones_articulos`, `precios`), same placement
  precedent as stage 2's migration (ADR-15 — same migration that creates the table enables its
  policy).
- `dotnet ef migrations has-pending-model-changes` — clean after the manual RLS addition.
- Migration file: `src/Ways.Infrastructure/Persistencia/Migraciones/20260803001552_ArticulosYPreciosEtapa3.cs`.

**Tests** (tasks 1.11, 1.13 — unblocked by the migration):
- `tests/Ways.IntegrationTests/ArticulosYPreciosRlsTests.cs` — parametrized theory over
  `articulos`/`articulos_empresas`/`codigos_barra`/`precios` (SELECT/UPDATE cross-tenant → 0
  rows via `USING`; INSERT with a foreign `id_tenant` → 42501 via `WITH CHECK`), plus an
  EF/LINQ filter proof for the 4 ORM-reachable entities (including `ArticuloEmpresa`'s manual
  filter) and 2 dedicated `numeraciones_articulos` tests (PK IS `id_tenant`, doesn't fit the
  parametrized shape — mirrors `ClientesYProveedoresRlsTests.NumeracionesClientesEsInvisibleParaOtroTenant`).
- `tests/Ways.IntegrationTests/AsignadorDeCodigoInternoArticuloConcurrenciaTests.cs` — mirrors
  `AsignadorDeNumeroClienteConcurrenciaTests`: 3 rounds × 2 concurrent
  `AsignarSiguienteAsync` calls, asserts exactly-consecutive-no-duplicates. Verified stable
  across 5 runs total (2 full-suite runs + 3 additional isolated runs) — the row lock on
  `numeraciones_articulos` serializes the race by construction, same mechanism already proven
  for `numeraciones_clientes`.

### Build/test results (batch 2, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 74/74 | 74/74 |
| `Ways.Application.Tests` | 142/142 | 142/142 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 145/145 | 145/145 |

145 = 129 baseline + 16 new (12 parametrized RLS cases + 2 `numeraciones_articulos` proofs + 1
EF/LINQ filter proof + 1 concurrency test). Identical counts both runs, no flakes.

### Commits (batch 2, work-unit style, migration in its own commit)

5. `feat(persistencia): registrar el enum unidad_venta en las fabricas de contexto` —
   `WaysDbContextFactory`, `DependencyInjection.cs`, `WaysApiFixture.cs`.
6. `fix(persistencia): nombrar en snake_case el indice de soporte de fk_articulos_empresas_articulo` —
   the missed `HasIndex` on `ArticuloEmpresaConfiguration`, found via the first scaffold.
7. `feat(persistencia): generar la migracion ArticulosYPreciosEtapa3 con RLS` — the migration
   itself + designer + model snapshot.
8. `test(articulos): agregar las pruebas de integracion de RLS y concurrencia del contador` —
   tasks 1.11/1.13.

### Next

Slice 1 is complete and runtime-verified. Judgment-day (dual blind review) runs next, per the
solo-dev PR protocol, before this PR is created. Slice 2 (`articulos` + `codigos_barra` +
`articulos_empresas` + margin suggestion, per the chained-PR plan) starts only after Slice 1's
PR merges.

### Status summary

| Bucket | Status |
|---|---|
| 1A — DB CHANGE GATE (1.1) | **Approved 2026-08-02**, exactly as presented |
| 1B — Domain (1.2–1.7) | Done |
| 1C — Migration (1.8) | Done |
| 1D — codigo_interno counter (1.9) | Done |
| 1E — db-error-backstops mapping (1.10) | Done |
| 1F — Tests (1.11–1.14) | Done — all 4 |

All 14 Slice 1 tasks complete. Slice 1 is runtime-verified end to end and ready for the
judgment-day review round.

### What shipped this batch

**Domain** (`Ways.Domain`):
- `Articulos/Articulo.cs` — `Articulo : EntidadTenant`, full field set (tenant-wide, no
  `id_empresa`).
- `Articulos/CodigoBarra.cs` — `CodigoBarra : EntidadTenant`.
- `Articulos/ArticuloEmpresa.cs` — junction, PK-only, no `EntidadBase`/`EntidadTenant` (no
  soft-delete).
- `Articulos/NumeracionArticulo.cs` — counter entity, mirrors `NumeracionCliente`.
- `Articulos/UnidadVenta.cs` — enum (`Unidad`, `Peso`).
- `Articulos/ReglaDeArticulos.cs` — pure rule `ValidarRestriccionDeDisponibilidad`.
- `Precios/Precio.cs` — `Precio : EntidadTenant`. **Deviation**: the money column is exposed
  as `Monto`, not `Precio` — C# forbids a member sharing its containing type's name (CS0542).
  Documented on the property; every other identifier matches the design/spec vocabulary.

**Infrastructure** (`Ways.Infrastructure.Persistencia.Configuraciones`):
- `ArticuloConfiguration`, `CodigoBarraConfiguration`, `ArticuloEmpresaConfiguration`,
  `NumeracionArticuloConfiguration`, `PrecioConfiguration` — new.
- `AreaConfiguration`, `MarcaConfiguration`, `GrupoConfiguration`, `ProveedorConfiguration` —
  each gained `HasAlternateKey(Id, IdTenant)` (design decision 7). Names follow the
  established full-column convention (`ak_{tabla}_{columna_id}_id_tenant`, same as
  `ak_categorias_id_categoria_id_tenant`/`ak_listas_precio_id_lista_precio_id_tenant`/
  `ak_empresas_id_empresa_id_tenant`) — **not** the literal `ak_areas_id_id_tenant` string in
  tasks.md, which reads as a markdown line-wrap artifact inconsistent with the convention it
  cites as authoritative. Used: `ak_areas_id_area_id_tenant`, `ak_marcas_id_marca_id_tenant`,
  `ak_grupos_id_grupo_id_tenant`, `ak_proveedores_id_proveedor_id_tenant`.
- `Articulo` itself also gained its own alternate key, `ak_articulos_id_articulo_id_tenant`
  (needed for `codigos_barra`/`articulos_empresas`/`precios`'s composite FKs into it) — not
  called out explicitly in design.md/tasks.md (which only calls out the 4 *existing*-table
  additions) but structurally required, same silent precedent as `empresas`/`categorias`/
  `listas_precio`'s own alternate keys.
- `WaysDbContext`: 5 new `DbSet<T>` (concrete class only, not `IWaysDbContext` yet — no
  Application consumer in this batch, same precedent as stage-1's tenant catalogs); manual
  tenant query filters for `NumeracionArticulo` and `ArticuloEmpresa` (neither inherits
  `EntidadTenant`, so the generic loop doesn't reach them); write-guard
  `RechazarEscriturasDeNumeracionArticulo` (mirrors the cliente one).

**Application** (`Ways.Application.Articulos`):
- `AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync`/`AsignarSiguienteAsync` — raw
  ADO.NET on the caller's transaction, mirrors `AsignadorDeNumeroCliente` exactly. Doc comment
  records orchestrator decision 1 (plain numeric, unpadded, `int`→`string` at the service
  layer) and the stage-5 forward dependency (<7 digits, documented not enforced).

**API** (`Ways.Api.Seguridad.ManejadorDeErrores`):
- `ClasificarUnicidad` ordering fix: `_codigo_interno` and `codigos_barra` branches inserted
  **before** the generic `_codigo` branch (both would otherwise silently fall into
  `codigo_duplicado`). New independent `_vigente` → `precio_vigente_duplicado` branch (no
  collision risk). Comment added at the `fk_` prefix branch confirming (no code change) it
  already covers all 16 new FK names this slice introduces.

**Tests** (migration-independent only, per the gate):
- `tests/Ways.Domain.Tests/Articulos/ReglaDeArticulosTests.cs` — 5 cases (task 1.12).
- `tests/Ways.Application.Tests/Persistencia/ModeloDeArticulosYPreciosTests.cs` — schema-shape
  assertions against the Npgsql-configured (but unconnected) model: unique indexes + filters,
  FK sets, the new alternate keys — mirrors `ModeloDeClientesYProveedoresTests`.
- `tests/Ways.Application.Tests/Persistencia/GuardDeNumeracionArticuloTests.cs` — mirrors
  `GuardDeNumeracionClienteTests` (InMemory, no DB).
- `tests/Ways.Application.Tests/Persistencia/FiltroDeTenantEnArticuloEmpresaTests.cs` — proves
  the manual tenant filter on `ArticuloEmpresa` (InMemory, no DB).
- 1.11 (RLS integration proofs) and 1.13 (counter concurrency integration) were deferred to
  batch 2 (see below) — both require the physical tables from the migration, blocked on gate
  approval at the time of this batch.

### Regression-hunting note (real bug found and fixed, not scope creep)

Adding `HasAlternateKey(Id, IdTenant)` to `Proveedor` (and, transitively, wiring
`Articulo`'s composite FK to it) reproducibly broke 9 existing `ServicioDeProveedoresTests`
against the EF Core **InMemory** provider only (never reproduces against Npgsql/Postgres):
`System.InvalidOperationException: The value of 'Proveedor.IdTenant' is unknown... property is
also part of a foreign key for which the principal entity in the relationship is not known.`

Root cause: `WaysDbContext.EstamparTenant()` stamped `IdTenant` on a newly `Added` entity via
a direct CLR property assignment (`entrada.Entity.IdTenant = ...`) *after* the entity was
already tracked with `IdTenant == 0`. For an entity whose `IdTenant` participates in **both**
a store-generated-adjacent alternate key (`Id` is identity, `(Id, IdTenant)` is the alternate
key) **and** a same-column composite FK to another table (`fk_proveedores_empresa`, sharing
`IdTenant`), the InMemory provider's change-tracker doesn't reliably pick up that later raw
mutation — `ListaPrecio` (which has the exact same key/FK shape) never hit this because its
only InMemory-tested seed path sets `IdTenant` in the object initializer under **Plataforma**
mode, never through the tenant-mode post-hoc stamping path.

**Fix**: `EstamparTenant()` now sets the value through the tracked-property API
(`entrada.Property(e => e.IdTenant).CurrentValue = ...`) instead of the raw CLR setter — same
end value, but it properly clears EF's internal "temporary key" state. This is a
provider-compatibility fix with no behavior change against Npgsql; verified via the full
Domain/Application/IntegrationTests suites (all green, see below). Flagged here because it's
exactly the kind of latent trap that `Articulo` (Slice 2, same alternate-key + tenant-mode-
created shape) would have hit again if left unfixed.

### Build/test results (this batch)

- `dotnet build Ways.slnx` — clean, 0 warnings, 0 errors.
- `dotnet test Ways.Domain.Tests` — **74/74** (baseline 69 + 5 new).
- `dotnet test Ways.Application.Tests` — **142/142** (baseline 128 + 14 new).
- `dotnet test Ways.IntegrationTests` (real Postgres via Testcontainers, Docker up) —
  **129/129**, unchanged from baseline (no new integration tests yet — correctly gated on
  1.8/migration).

### Commits (work-unit style, on `feat/stage3-slice1-schema`)

1. `feat(articulos): agregar las entidades de dominio y las claves alternas del gate` —
   domain entities, EF configurations, 4 existing-table alternate keys, `WaysDbContext`
   wiring (DbSets, manual tenant filters, write guard, and the `EstamparTenant`
   provider-compatibility fix found while validating this batch — same file, bundled in).
2. `feat(articulos): agregar el contador atomico de codigo_interno` —
   `AsignadorDeCodigoInternoArticulo`.
3. `fix(errores): ordenar los backstops de unicidad nuevos antes del generico _codigo` —
   `ManejadorDeErrores` ordering fix + `_vigente` branch.
4. `test(articulos): agregar las pruebas independientes de la migracion` — domain unit tests,
   model-shape tests, guard test, manual-filter test.

### Batch 1 next-steps — superseded, see "Batch 2" above

Everything listed here (migration generation, RLS proofs, counter concurrency) was completed
in batch 2 after gate approval. Left in place for the historical record of what batch 1 hadn't
done yet; see the "Batch 2" section above for what actually happened and the final "Next"
section for what comes after Slice 1.

## Judgment-day round 1 — confirmed doc/comment fixes (no behavior change)

Both blind review agents confirmed 5 documentation/comment issues; all fixed in a single
work-unit commit, no code path affected (build clean, `Ways.Application.Tests` 142/142 re-run
as smoke).

- `src/Ways.Api/Seguridad/ManejadorDeErrores.cs`, `ClasificarUnicidad`: added the missing
  race-test-exemption comment to the 3 new branches (`codigo_interno_duplicado`,
  `codigo_barra_duplicado` → deferred to Slice 2, `ServicioDeArticulos`, tasks 2.8/2.9;
  `precio_vigente_duplicado` → deferred to Slice 3, `ServicioDePrecios`, task 3.11), mirroring
  the existing `_cuit`/`_default` precedent.
- Same file, task-1.10 comment above the `fk_` prefix branch: corrected "8 FKs nuevas" → "16
  FKs nuevas" (the enumeration itself was already correct and complete). Same count fix applied
  to this progress doc's own batch-1 summary (`ManejadorDeErrores.ClasificarUnicidad` bullet,
  above), which had inherited the same wrong "8" figure.
- `docs/10-modelo-de-datos.md` §3: `codigo_interno` was still documented as `citext NULL`
  after the "mandatory and autogenerated" decision shipped (migration already has
  `nullable: false`, confirmed against `20260803001552_ArticulosYPreciosEtapa3.cs`). Doc now
  reads `NOT NULL` with a short note on the resolved decision.
- `docs/09-multi-tenancy.md`, scoping table (~line 84): moved `articulos`/`codigos_barra`/
  `precios` out of the `id_empresa NULL` Catálogo row into a new "Tenant-wide (disponibilidad
  por empresa)" row/category describing the `disponible_para_todas` + `articulos_empresas`
  model, pointing to doc 10 §3 for detail. `precios` stays there too — it's `id_tenant`, no
  `id_empresa`, same as `articulos` (confirmed: `Precio : EntidadTenant`, no `IdEmpresa`
  property).
- `src/Ways.Domain/Articulos/ArticuloEmpresa.cs` and `WaysDbContext.
  AplicarFiltroDeTenantEnArticuloEmpresa`: added an explicit callout that `IdTenant` is NOT
  auto-stamped on `ArticuloEmpresa` (it doesn't inherit `EntidadTenant`, so the generic
  `EstamparTenant` loop never reaches it) — whoever constructs the row MUST assign `IdTenant`
  by hand, and RLS `WITH CHECK` rejects the INSERT with SQLSTATE 42501 if it's missing.
  Forward instruction left for Slice 2, when `ServicioDeArticulos` becomes the real write path.

No test-affecting change was made — this round was documentation/comments only.

## Slice 2 — judgment-day ronda 2 batch: 2 pruebas nuevas (sin cambios de producción)

Ronda 2 confirmó (ambos jueces ciegos) el gap de cobertura de carrera para
`PK_articulos_empresas`/`empresa_duplicada_en_subset` (item 1, CONFIRMED) y sugirió un test de
round-trip end-to-end del escenario de no-op documentado en `ObtenerAsync` (item 2,
SUGGESTION). Ambos se resuelven agregando pruebas — **no se tocó código de producción**.

### Item 1 — Race test para `empresa_duplicada_en_subset`

Se intentó primero la forma preferida (dos PUT concurrentes restringiendo el MISMO artículo a
la MISMA empresa, arrancando sin subset previo) y **no fue reproducible tras intentos
honestos**: `ActualizarAsync` también hace `UPDATE` sobre la fila del propio `articulo` antes
del DELETE+INSERT del subset, así que esa fila actúa como mutex de facto entre las dos
transacciones — la segunda bloquea en el `UPDATE`, y cuando retoma (tras el commit de la
primera) su `DELETE` ya ve y borra la fila recién comiteada por la otra, evitando la colisión
de la PK por completo. Se corrió manualmente ~18 veces (6 corridas × loop interno de 3): solo
una mostró un mismatch de aserción (200/200 sin 409, sin excepción) y otra mostró un deadlock
retryable absorbido en silencio por `EnableRetryOnFailure` (`DependencyInjection.cs`) — nunca
un 23505 expuesto de forma confiable. Documentado en el propio doc-comment del test.

**Fallback aplicado** (según el contrato de la skill `db-error-backstops`): duplicar la fila de
`articulos_empresas` por SQL directo, bypasseando `ServicioDeArticulos` por completo — mismo
criterio que `BackstopClientesYProveedoresTests.UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad`.
Se confirmó que tampoco hay forma de ejercer este 409 vía HTTP: `ActualizarAsync` siempre borra
el subset completo del artículo (scoped por `IdArticulo`) ANTES de reinsertarlo, así que ninguna
secuencia de requests HTTP puede dejar dos filas con la misma PK compuesta para el mismo
artículo — el bypass solo es alcanzable con SQL directo.

- `tests/Ways.IntegrationTests/ArticulosEndpointsTests.cs` —
  `UnaFilaDeSubsetDuplicadaInsertadaPorFueraDelServicioViolaLaPk`: dos INSERT crudos por SQL con
  el mismo `(id_articulo, id_empresa)`, asertando `PostgresException.SqlState == "23505"` y
  `ConstraintName == "PK_articulos_empresas"`. Corrida 3 veces de forma aislada, sin flakes.

### Item 2 — Round-trip de no-op (sugerencia)

- `tests/Ways.IntegrationTests/ArticulosEndpointsTests.cs` —
  `UnPutDeNoOpConLosIdsEmpresasDelGetPreservaElSubset`: crea un artículo restringido, hace GET
  del detalle, toma `IdsEmpresas` verbatim, arma el PUT de no-op y verifica 200 + subset intacto
  (antes y después del PUT), cubriendo end-to-end el escenario documentado en
  `ServicioDeArticulos.ObtenerAsync`. Corrida 3 veces de forma aislada, sin flakes.

### Build/test results (judgment-day ronda 2 batch, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 81/81 | 81/81 |
| `Ways.Application.Tests` | 165/165 | 165/165 |
| `Ways.IntegrationTests` (real Postgres) | 174/174 | 174/174 |

174 = baseline 172 + 2 nuevos. Build clean (0 warnings, 0 errors), ambas corridas idénticas, sin
flakes. Ambas pruebas nuevas también corridas de forma aislada 3 veces cada una antes de la
corrida completa.

### Commit (work-unit, pendiente de listar acá tras crearlo)

`test(articulos): cerrar el gap de carrera de PK_articulos_empresas y sumar el round-trip de
no-op` — la ronda 2 de judgment-day sobre Slice 2 (items 1-2), solo tests, sin cambios de
producción.
