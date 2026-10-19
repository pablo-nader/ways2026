# Compras: preparación para la carga asistida (etapa S2 del conector MCP)

## Objetivo

Dejar el módulo de compras listo para que una tool del conector MCP cargue una factura de
proveedor como BORRADOR de compra. La confirmación sigue siendo humana, en la web.

## Problema

- `CrearBorradorAsync` guarda header e hijos con dos `SaveChangesAsync` sin transacción: si falla
  el segundo, queda un header sin líneas.
- No hay forma de descartar un borrador, y un borrador retiene su número en
  `ux_comprobantes_compra_numero_externo`.
- No se distingue una compra cargada a mano de una cargada por el asistente.
- `numero_externo` es texto libre: "0003-00012345" y "3-12345" no chocan.
- No hay búsqueda de proveedor por CUIT que ignore guiones y espacios.

## Alcance y restricciones

- Fuera de alcance: todo `src/Ways.Api/ConectorMcp/`, tools MCP, la pantalla de confirmación más
  allá del aviso y el botón.
- Prioridad de la orquestadora: el origen y el chequeo de duplicado normalizado no pueden quedar
  afuera.
- Estrategia de entrega: `stacked-to-main`, un PR por unidad de trabajo.

## Decisiones

- **Alta transaccional**: forma (b) de `ef-retry-safe-writes` (`FabricaDeEstrategiaSinReintento`)
  y una sola transacción. `ExigirOrdenLigableAsync` pasa adentro de la transacción.
- **Descartar**: `DELETE /api/compras/{id}`, Admin (`GestionDeCatalogo`), 404 primero, lock
  `FOR UPDATE ... WHERE estado='borrador'`, borrado físico de header e hijos, auditoría
  `compra.descarte`. Estado no borrador: 409 `compra_no_descartable`.
- **Origen**: enum Postgres `origen_compra` (`manual`, `asistente`), NOT NULL default `manual`.
  Lo fija solo la capa de aplicación; la API REST nunca lo acepta.
- **Número canónico**: formato ARCA vigente `PPPPP-NNNNNNNN` (punto de venta de 5 dígitos, el mismo
  que imprime la factura legal en `impresion/plantillas.ts`). Solo para tipos que registran libro
  IVA. El chequeo de duplicado compara el valor numérico de cada segmento, así detecta también las
  filas viejas con punto de venta de 4 dígitos o texto libre con guion.
- **CUIT**: búsqueda exacta comparando solo dígitos, sin cambiar cómo se guarda.

## Tareas

- [x] **T1 — Alta transaccional + descartar borrador** (PR 1)
  - Alta en una transacción sin reintento; test con falla al guardar hijos sin header huérfano.
  - `DELETE /api/compras/{id}` con 404 primero, 409 estable, lock, auditoría, guardas estructurales.
  - Web: botón "Descartar borrador" con confirmación en el editor.
- [ ] **T2 — Origen de la compra** (PR 2)
  - Columna, enum, migración, doc 10 §5, lecturas con `origen`, filtro del listado.
  - Web: insignia "Asistente" con filtro y aviso en el editor del borrador asistido.
- [ ] **T3 — Número canónico + proveedor por CUIT** (PR 3)
  - Normalización al escribir para C-FA/C-FB/C-FC, 400 estable, chequeo de duplicado normalizado.
  - `GET /api/proveedores?cuit=` y método de servicio.

## Checks aplicables

- Build de `src` sin advertencias nuevas.
- `tests/Ways.Domain.Tests` y `tests/Ways.Application.Tests` completos.
- Integración filtrada: compras, proveedores, `SuperficieDeAutorizacion`, `EscriturasSinReintento`;
  para el enum nuevo, la suite completa queda al CI (regla de Docker).
- Vitest de los componentes tocados.

## Progreso y evidencia

Ruta por tarea, commits y resultado de judgment-day se anotan acá al cerrar cada una.

- **T1** (ruta: escritor delegado, disparador: 2+ archivos no triviales; commit be976719): alta en `EjecutarAltaDeBorradorAsync` (forma (b), una transacción,
  `ExigirOrdenLigableAsync` adentro); `DescartarBorradorAsync` + `DELETE /api/compras/{id}` (404 primero, lock
  `FOR UPDATE ... estado='borrador'`, 409 `compra_no_descartable`, borrado físico de hijos y encabezado por ADO, auditoría
  `compra.descarte` sin migración: la columna `accion` es `text`). Ninguna tabla puede referenciar un borrador: los seis
  FK a `comprobantes_compra` son los tres hijos (se borran antes), `gastos` (solo se liga a una compra confirmada,
  `ServicioDeGastos.ExigirCompraLigableAsync`) y los movimientos de stock y de cuenta corriente (nacen en
  `EjecutarConfirmarAsync`, después de pasar a confirmada). Guardas: `EscriturasSinReintentoEstructuralesTests` (+2 sitios) y
  `BajasEstructuralesTests` (`DELETE FROM` permitido en `ServicioDeCompras.cs`). Web: botón "Descartar borrador" con
  `ConfirmacionDeBaja`, ref de reentrancia y `useMontado` antes de navegar.

- **T1 checks**: Domain 1706 ok; Application 1046 ok (proyecto entero); integración filtrada (ComprasDescarte,
  EscriturasSinReintento, SuperficieDeAutorizacion, ComprasLifecycle, Ligadura, AuditoriaAnulacionCompra,
  ManejadorDeErroresCompras) 106 ok; Vitest CompraEditor 150 ok; tsc y oxlint ok. Judgment-day: APPROVED en ronda 1, sin
  hallazgos severos. Info: el catálogo web de acciones auditadas no incluye `compra.descarte` (ya le faltaban siete
  acciones previas: seguimiento aparte); el descarte no tiene test de reintento conductual, solo el estructural.

- **T2** (ruta: escritor delegado, disparador: 2+ archivos no triviales; sin commit todavía): enum nativo `origen_compra`
  (`manual`, `asistente`) y columna `comprobantes_compra.origen NOT NULL DEFAULT 'manual'` (migración
  `OrigenDeLaCompra`, sin UPDATE: el DEFAULT rellena al ALTER, no aplica `rls-migration-backfills`); `MapEnum` en
  `DependencyInjection.cs`, `WaysDbContextFactory.cs` y los 23 sitios de fixtures de integración que mapean `EstadoCompra`.
  `CrearBorradorAsync(solicitud, origen)` es el único lugar donde se fija; el overload sin origen delega con `Manual`;
  el PUT no lo toca y `SolicitudDeCompra` no lo lleva (miembro desconocido en el JSON: el binding lo ignora, como
  cualquier otro; sin campo no hay campo aceptado-y-descartado). `CompraDetalle`/`CompraListada` exponen `Origen` (string
  PascalCase, como `Estado`); filtro `origen` en `GET /api/compras` y `/export` por `ConstruirQuery`; el export no gana
  columna (sus cinco columnas son la hoja de conciliación y el test de igualdad las fija), solo respeta el filtro. Web:
  insignia "Asistente", filtro Origen y aviso en el editor del borrador asistido.
- **T2 checks**: Domain 1706 ok; Application 1046 ok; `ComprasOrigenTests` 7 ok; mutaciones: sin la cláusula del filtro fallan
  2 (listado y export), con `Origen = Manual` en el PUT falla 1; Vitest 437 ok (13 archivos), tsc y oxlint ok; mutaciones web
  (estado del aviso, parámetro del query, condición de la insignia) matan sus tests. La suite de integración entera queda al CI.

## Próximo paso

T2.
