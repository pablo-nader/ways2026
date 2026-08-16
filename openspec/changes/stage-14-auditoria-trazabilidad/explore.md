# Exploration: Etapa 14 — Auditoría y trazabilidad de operaciones sensibles

*(Producido por sdd-explore 2026-08-16 sobre main HEAD 9bd0e55, etapas 1-13
archivadas; persistido verbatim por el orquestador.)*

## Current State

El sistema **ya tiene tres ledgers inmutables** que funcionan como auditoría parcial — la pregunta de la etapa 14 no es "empezar de cero" sino "qué falta encima de lo que ya existe":

| Ledger existente | Tabla | Actor capturado | Detalle capturado |
|---|---|---|---|
| Stock | `movimientos_stock` | `id_empleado` (siempre) | `motivo` enum + `observaciones` text libre (ajuste/decomiso) |
| Cuenta corriente clientes | `movimientos_cuenta_corriente` | `id_empleado` (siempre) | `tipo` enum + `detalle text` (JSON serializado en reliquidación) |
| Precios | `precios` | **ninguno** | ninguno — solo `vigente_desde/hasta`, sin quién |

**Precios** (`src/Ways.Application/Precios/ServicioDePrecios.cs:80-202`, método `AbrirNuevoPrecioAsync`): motor completo de historial (abre/cierra filas, nunca UPDATE del monto), pero la fila `Precio` (`src/Ways.Domain/Precios/Precio.cs`, confirmado línea por línea: `Id`, `IdArticulo`, `IdListaPrecio`, `Monto`, `VigenteDesde`, `VigenteHasta` — nada más) **no tiene columna de actor**. `IContextoDeUsuario contexto` está inyectado en el servicio pero solo se usa para `ExigirTenantDeLaSesion()` (línea 499), nunca para poblar quién cambió el precio. Este es el gap más limpio de los cinco dominios nombrados por el doc: cero rastro de quién.

**Anulación de ventas** (`src/Ways.Application/Ventas/ServicioDeVentas.cs:472-620`, `AnularAsync`/`EjecutarAnulacionAsync`): `comprobantes_venta.id_empleado` (doc 10 línea 326) registra solo al **emisor** — la transición `emitido → anulado` (línea 525, `MarcarAnuladoAsync`) es un `UPDATE ... WHERE estado = 'emitido'` que toca `Estado`/`UpdatedAt`, nunca un actor de anulación. El `idEmpleado` de quien anula (línea 475) sí queda en las filas de reversa de `movimientos_stock` (línea 564-566, motivo `Anulacion`) y en el contramovimiento de `movimientos_cuenta_corriente` (línea 610+) — **pero solo si el comprobante tuvo líneas de producto o de cuenta corriente**. Un comprobante 100% de servicio (`EsProducto = false`) sin CC asociada, al anularse, no deja NINGÚN rastro de quién ni cuándo más allá de `updated_at` sin actor. Mismo patrón exacto en `src/Ways.Application/Compras/ServicioDeCompras.cs:472-500` (`AnularAsync`).

**Ajustes/decomiso/conteo/transferencia de stock** (`src/Ways.Application/Stock/ServicioDeStock.cs`): todos los caminos (`AjustarAsync` línea 51, `DecomisarAsync` línea 212, `ContarAsync` línea 670, `TransferirAsync` línea 294) ya escriben `movimientos_stock` con `id_empleado` + `observaciones` (ajuste/decomiso, línea 58 `ExigirObservaciones`) o sin observaciones (conteo/transferencia, que llevan motivo estructural en el enum). **Este dominio está prácticamente auditado ya** — el gap acá es de UI/consulta unificada, no de captura.

**Cambios de rol y permisos** (`src/Ways.Application/Usuarios/ServicioDeUsuarios.cs:119-159`, `ActualizarAsync`): el cambio de `RolId` es un `UPDATE` plano (líneas 144-148) que solo bumpea `UpdatedAt`. `contexto.UsuarioId` está disponible (usado en `Actor`, línea 24, para autorización) pero **nunca se persiste como actor del cambio, y el valor viejo del rol se pisa sin dejar rastro**. Este es el segundo gap limpio — cero actor, cero valor anterior.

**Reliquidación de CC** (`src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs:57-140`): ya escribe `movimientos_cuenta_corriente` con `id_empleado` (línea 61, 123) y `detalle` como JSON serializado de `resultado.Detalle` (línea 121, `JsonSerializer.Serialize`) en la columna `detalle text` (doc 10 línea 661) — **este es el precedente más completo de "detalle estructurado" del repo**, aunque persistido como `text`, no `jsonb`, y con PascalCase de C# (no hay `JsonNamingPolicy.SnakeCase` configurado, a confirmar en proposal).

## Affected Areas

- `src/Ways.Application/Precios/ServicioDePrecios.cs` — gap de actor: `AbrirNuevoPrecioAsync` (líneas 80-202) necesita capturar `contexto.UsuarioId` en cada cambio de precio; hoy no lo hace.
- `src/Ways.Domain/Precios/Precio.cs` — entidad sin columna de actor; candidata a NO tocar si la etapa 14 usa tabla externa `auditoria` en vez de agregar columna acá.
- `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs:119-159` (`ActualizarAsync`) — gap de actor + valor anterior en cambio de rol.
- `src/Ways.Application/Ventas/ServicioDeVentas.cs:472-660` (`AnularAsync`/`EjecutarAnulacionAsync`) — actor parcial vía ledgers de reversa; comprobantes sin producto/CC quedan sin rastro.
- `src/Ways.Application/Compras/ServicioDeCompras.cs:472-560` (`AnularAsync`) — mismo patrón que ventas.
- `src/Ways.Application/Stock/ServicioDeStock.cs` — ya casi completo (actor + observaciones en todos los caminos); patrón de escritura a replicar en los demás dominios.
- `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs:57-140` — precedente de `detalle` JSON en columna `text`.
- `src/Ways.Application/Exportacion/*` (`TablaExportable.cs`, `GuardaDeTope.cs`, `OpcionesDeExportacion.cs`, `ExportacionDeListados.cs`) — infraestructura de la etapa 11 directamente reusable para la consulta exportable.
- `src/Ways.Infrastructure/Multitenancy/RlsMigrationBuilderExtensions.cs:65-77` (`HabilitarRlsDeTenant`) — patrón RLS estándar a aplicar sobre la tabla nueva.
- `tests/Ways.IntegrationTests/VentasCheckoutTests.cs:852-924` — guard de presupuesto de 16 queries del checkout; condiciona cómo se escribe la auditoría en el hot path de emisión.
- `docs/10-modelo-de-datos.md` (convenciones de schema, líneas 1-30, 316-428, 555-663) y `docs/09-multi-tenancy.md` (scoping, líneas 77-146) — contrato de diseño que la migración nueva tiene que respetar.
- `src/Ways.Web/src/paginas/Vencimientos.tsx`, `Reposicion.tsx` — precedente de pantalla con filtros (etapas 12/13) a replicar para la consulta de auditoría.

## Contexto de usuario disponible

`IContextoDeUsuario` (`src/Ways.Application/Abstracciones/IContextoDeUsuario.cs`) expone `UsuarioId`, `Rol`, `IdTenant`, `NombreUsuario`, resuelto en `src/Ways.Api/Seguridad/ContextoDeUsuarioHttp.cs:18-38` desde claims (`ClaimTypes.NameIdentifier`, `ClaimsWays.RolId`, `ClaimsWays.IdTenant`). Está inyectado y disponible en **los seis servicios candidatos** (`ServicioDeStock`, `ServicioDeVentas`, `ServicioDeCompras`, `ServicioDeArticulos`/`ServicioDePrecios`, `ServicioDeCuentaCorriente`/`ServicioDeReliquidacion`, `ServicioDeUsuarios`) — no hace falta agregar cableado nuevo para tener el actor en el punto de escritura. El gap nunca es "no tengo el usuario", es "tengo el usuario y no lo persisto".

## Presupuesto de queries del checkout — tensión real, no hipotética

`tests/Ways.IntegrationTests/VentasCheckoutTests.cs:898-923` prueba un guard duro: el checkout emite **exactamente 16** round-trips (bajó de 17 en etapa 12), constante independiente de la cantidad de líneas. El guard cuenta cualquier comando que dispare `ReaderExecuting`/`ScalarExecuting` (interceptor `ContadorDeComandos`, línea 864-882) — un `SaveChangesAsync` de EF cuenta, un `INSERT ... RETURNING` vía `ExecuteScalarAsync` cuenta, pero **un `INSERT` plano vía `ExecuteNonQueryAsync` NO cuenta** (doc-comment explícito en `ServicioDeVentas.cs:1045-1054`, mismo patrón en `UpsertStockAsync` línea 1080-1083). Esto es el precedente exacto que resuelve la tensión "misma transacción rompe el presupuesto": una escritura de auditoría en el hot path del checkout, si se implementa como INSERT crudo sin `RETURNING` (no necesita devolver el id generado a la aplicación), **es invisible al guard y no lo rompe**. La anulación de venta y la reliquidación no tienen guard de presupuesto — solo el checkout de emisión lo tiene.

## Patrones de schema (doc 09/10) para una tabla nueva

- Toda operación sensible nombrada por el doc (precio, anulación, ajuste stock, rol, reliquidación) es **por definición operativa** salvo cambios de rol/permisos, que tocan `usuarios`, tabla `[global]` sin `id_punto_venta` — un registro de auditoría que unifique ambos casos no puede ser estrictamente "operativa por PV": necesita `id_tenant` + `id_punto_venta NULL` (como `turnos_caja`/`movimientos_stock` cuando aplica, NULL cuando la operación es tenant-wide como un cambio de rol).
- Precedente RLS de tabla operativa: `RlsMigrationBuilderExtensions.HabilitarRlsDeTenant` (`src/Ways.Infrastructure/Multitenancy/RlsMigrationBuilderExtensions.cs:65-77`) — misma policy estándar `id_tenant = app_tenant_actual()`, aplicable directo.
- Precedente de `id_empleado` como FK simple (no compuesta) a `usuarios.id_usuario`, sin `id_tenant` en la FK (doc 10 líneas 563-567) — mismo criterio a reusar si la tabla nueva lleva `id_actor`.
- Precedente de columna de detalle libre: `detalle text` con JSON serializado (`ServicioDeReliquidacion.cs:121`) — la decisión abierta "jsonb vs text" tiene un precedente real que usa `text`, no `jsonb` nativo; el proposal debería decidir si esta etapa migra ese patrón a `jsonb` (mejor para filtrar/exportar valor-anterior/nuevo estructurado) o mantiene `text`.
- Naming: `auditoria` o `registro_auditoria` (singular tabla append-only, sin plural de colección como `movimientos_x` que implica "movimiento de saldo") es la convención más cercana; el doc 03/10 no tiene precedente de tabla puramente de bitácora sin saldo asociado.

**Veredicto preliminar de gate de DB — CASI SEGURO requiere migración.** Modelo mínimo tentativo a presentar en el gate:

```
auditoria (                          -- [operativa, id_punto_venta NULL para eventos tenant-wide]
    id_auditoria      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    id_tenant         integer NOT NULL,
    id_punto_venta    integer NULL,       -- NULL en cambios de rol/permisos (global al tenant)
    id_actor          integer NOT NULL REFERENCES usuarios(id_usuario),   -- FK simple, sin id_tenant (mismo criterio que id_empleado)
    accion            text NOT NULL,       -- 'precio.cambio' | 'venta.anulacion' | 'compra.anulacion' | 'stock.ajuste' | 'usuario.rol' | 'cc.reliquidacion' ...
    entidad           text NOT NULL,       -- 'precio' | 'comprobante_venta' | 'usuario' | ...
    id_entidad        integer NOT NULL,
    valor_anterior    jsonb NULL,
    valor_nuevo       jsonb NULL,
    creado_el         timestamptz NOT NULL DEFAULT now()
);
-- índice compuesto (id_tenant, entidad, id_entidad, creado_el) para la consulta filtrable
-- RLS: HabilitarRlsDeTenant("auditoria") — patrón estándar, sin desvío
```

Bigint PK (no `integer`) porque es append-only de alto volumen esperado (a diferencia de `precios`/`usuarios`, escala con toda operación sensible del sistema, potencialmente miles/mes por tenant chico). Sin `EntidadBase` completo (no tiene sentido `updated_at`/soft-delete en un hecho inmutable — mismo criterio que `movimientos_stock`, que tampoco lo lleva).

## Consulta/export — reuso de la infraestructura de la 11

`TablaExportable`/`GuardaDeTope`/`ExportacionDeListados` (`src/Ways.Application/Exportacion/`) son directamente reusables: `GuardaDeTope.Exigir` (`GuardaDeTope.cs:18-28`) con `OpcionesDeExportacion.TopeDeFilas` (25.000 default, bindable — `OpcionesDeExportacion.cs:14`) aplica igual a un listado de auditoría paginado con `COUNT(*)` previo (mismo patrón "listado" que ventas/compras, `ExportacionDeListados.cs` líneas 46-82). El patrón de pantalla con filtros de las etapas 12/13 (`src/Ways.Web/src/paginas/Vencimientos.tsx`, `Reposicion.tsx`) es el precedente de UI a copiar para la pantalla de consulta filtrable (rango de fecha, tipo de operación, actor, entidad).

## Approaches

1. **Tabla única genérica (`auditoria`) con `accion`/`entidad` + `jsonb` valor_anterior/valor_nuevo**
   - Pros: una sola consulta/export cubre los 5+ dominios; un solo patrón de escritura a replicar en cada servicio; RLS/gate se aprueba una vez.
   - Cons: `jsonb` sin schema fuerte por acción — riesgo de inconsistencia entre qué guarda cada dominio; una consulta que necesita comparar "todos los cambios de precio" tiene que filtrar dentro del JSON.
   - Effort: Medium — una migración, un helper de escritura (`ServicioDeAuditoria.RegistrarAsync`), y tocar 6 puntos de escritura para invocarlo.

2. **Registro por dominio (extender cada ledger existente con lo que le falta)**
   - Pros: aprovecha al máximo lo que ya existe (stock/CC ya casi completos); cada tabla mantiene su forma tipada.
   - Cons: **no resuelve el mandato explícito del doc** ("consulta filtrable y exportable" unificada — con 5 tablas distintas, la consulta cruzada exige un UNION manual o una vista); dos gaps reales (`precios`, `usuarios`) igual necesitan tabla/columna nueva porque hoy no tienen NADA, así que el ahorro real es solo sobre ventas/compras/stock/CC.
   - Effort: Medium-High — mismo trabajo de tocar 6 puntos de escritura, pero sin el beneficio de una sola vista de consulta.

3. **Tabla única, pero solo para los dos gaps reales (precios, roles) + vista SQL que UNIONea los ledgers existentes para consulta**
   - Pros: minimiza escritura nueva (solo 2 servicios tocan una tabla nueva); reusa el 100% del trabajo ya hecho en stock/CC/ventas; la vista de consulta resuelve "quién hizo esto" sin duplicar dato.
   - Cons: la vista UNION es frágil ante cambios de schema en cada ledger; "anulación de venta sin líneas de producto" sigue sin actor a menos que la anulación TAMBIÉN escriba en la tabla nueva (rompe la premisa de "solo 2 gaps").
   - Effort: Medium — pero exige que el proposal decida caso por caso qué operación entra en cada camino, más carga de diseño que las otras dos.

## Recommendation

**Approach 1** (tabla única genérica) es la más alineada con el mandato del doc ("consulta filtrable y exportable" — singular, no cinco pantallas) y con el criterio de esta etapa ("mecánicamente simple, pero toca muchos puntos del código" — doc 11:211-212, que ya anticipa tocar varios servicios sin ganar nada mezclando formas de tabla distintas). El costo de un `jsonb` sin schema fuerte se mitiga documentando por convención qué claves usa cada `accion` (igual que `ServicioDeReliquidacion.Detalle` ya hace informalmente). Escribir en la MISMA transacción, vía `ExecuteNonQueryAsync` sin `RETURNING` (precedente exacto de `InsertarMovimientoStockAsync`), evita romper el guard de 16 queries del checkout y mantiene consistencia — la escritura diferida introduce una ventana de pérdida (si el proceso muere entre el commit de negocio y el registro diferido, la operación queda sin auditar, exactamente lo que la etapa busca evitar).

## Risks

- Volumen: sin política de retención, `auditoria` crece sin límite — a diferencia de `precios`/`movimientos_stock` (que tienen valor de negocio permanente), un registro de auditoría de bajo valor después de N meses es candidato a partición/archivado; el doc deja esto abierto y el proposal tiene que fijar un número o decisión explícita de "sin retención por ahora".
- El caso "comprobante de venta 100% servicio, anulado, sin CC" queda sin actor HOY — si el proposal decide "primera pasada = extender los ledgers existentes" sin agregar la tabla genérica, este caso específico sigue sin resolverse a menos que se liste explícitamente.
- `jsonb` con PascalCase vs snake_case: el precedente de reliquidación serializa con el naming default de C# (PascalCase) dentro de una columna `text`, no `jsonb` — el proposal debe fijar la política de naming (¿`JsonNamingPolicy.SnakeCase` para consistencia con el resto del schema en español/snake_case, o se acepta la inconsistencia ya sembrada?).
- Autorización de la consulta: quién puede LEER auditoría (¿solo Admin, como `GestionDeCatalogo`? ¿un tenant ve auditoría de otro PV?) no está explorado — impacta el diseño de RLS de lectura más allá del aislamiento por tenant estándar.

## Ready for Proposal

Sí. Preguntas concretas que el proposal tiene que decidir (más allá de las 4 ya abiertas en el doc):

1. ¿Tabla única `auditoria` con `jsonb`, o approach 3 (tabla nueva solo para precios/roles + vista sobre ledgers existentes)?
2. ¿`valor_anterior`/`valor_nuevo` completos, o solo el delta relevante por acción (ej. precio: monto viejo/nuevo; rol: rol viejo/nuevo; anulación: solo el hecho, ya que el ledger de reversa tiene el detalle)?
3. Retención: ¿sin política por ahora, partición mensual, o TTL con archivado a export?
4. Misma transacción (con el patrón `ExecuteNonQueryAsync` sin `RETURNING` para no romper el guard de 16 queries) vs diferida — recomendación: misma transacción, pero es decisión de negocio, no solo técnica.
5. Primera pasada: ¿se listan las 5 operaciones del doc + `usuarios.EliminarAsync`/`DesbloquearAsync`/`CambiarPasswordAsync` (también sensibles, no nombradas por el doc), o se acota estrictamente a las 5?
6. Autorización de lectura del registro de auditoría — ¿mismo criterio que `GestionDeCatalogo`, o un permiso nuevo?
7. Naming JSON dentro de `jsonb` — snake_case explícito vs PascalCase heredado del precedente de reliquidación.
