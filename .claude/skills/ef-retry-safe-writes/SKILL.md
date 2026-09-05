---
name: ef-retry-safe-writes
description: "Trigger: new or modified `CreateExecutionStrategy().ExecuteAsync`, `EnableRetryOnFailure`, `db.X.Add(` inside a strategy lambda, `ServicioDeAuditoria.Registrar` inside a lambda, transactional write with audit rows, `FabricaDeEstrategiaSinReintento`. Every entity Added or mutated inside a retrying ExecuteAsync lambda ships either a retry-safe reset or the non-retrying strategy, proven with a real transient-failure retry."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load cuando se agrega o modifica un bloque `CreateExecutionStrategy().ExecuteAsync` con
`EnableRetryOnFailure`, cualquier `db.X.Add(` dentro de ese lambda, un
`ServicioDeAuditoria.Registrar` dentro de él, o una escritura transaccional que combina
filas de negocio con filas de auditoría. Nacida de la etapa 20: las bajas de
organización/usuario duplicaban filas de auditoría bajo reintento transitorio.

## Hard Rules

1. **Toda entidad agregada con `Add` dentro de un lambda `ExecuteAsync` que reintenta
   se duplica ante un reintento.** Tras un fallo transitorio de `SaveChangesAsync`, EF
   conserva en el `ChangeTracker` las entidades `Added` del intento N; el intento N+1
   vuelve a ejecutar el lambda completo y agrega un segundo set; el `SaveChangesAsync`
   final inserta ambos. Además, toda entidad CARGADA ANTES del lambda y mutada DENTRO
   de él arrastra la mutación del intento 1 al intento 2 (un `valorAnterior` leído de
   esa entidad registra un estado previo falso, porque ya viene mutado).

2. **Elegir una de dos formas y decir cuál se eligió:**
   - (a) **Lambda retry-safe**: `db.ChangeTracker.Clear()` como primera sentencia del
     lambda; toda carga ocurre DENTRO del lambda, después de cualquier lock; toda
     entidad se construye de cero dentro del lambda (nunca se reutiliza una instancia
     capturada de afuera).
   - (b) **`FabricaDeEstrategiaSinReintento`** para escrituras no idempotentes sin
     clave de idempotencia (patrón ya establecido en el repo: `ServicioDeVentas.AnularAsync`,
     `ServicioDeStock.AjustarAsync`, las bajas de organización/usuario de la etapa 20).
     Preferir (b) cuando un reintento también podría enmascarar un commit ambiguo (el
     reintento vuelve a leer la fila ya borrada y responde 404 a una baja que en
     realidad tuvo éxito).
   ```
   // (a) retry-safe
   await estrategia.ExecuteAsync(async () =>
   {
       db.ChangeTracker.Clear();
       var entidad = await db.Organizaciones.FirstAsync(o => o.Id == id);
       entidad.Estado = EstadoOrganizacion.Baja;
       db.RegistrosDeAuditoria.Add(ServicioDeAuditoria.Registrar(entidad, valorAnterior));
       await db.SaveChangesAsync();
   });

   // (b) sin reintento
   var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
   await estrategia.ExecuteAsync(async () => { /* misma escritura, un solo intento */ });
   ```
   DON'T: cargar la entidad o construir la fila de auditoría ANTES del `ExecuteAsync`,
   o hacer `Add` sobre una instancia capturada por closure desde afuera del lambda.

3. **Probarlo con un reintento real.** Usar
   `WaysApiFixture.CrearContextoDeAplicacionConReintentos(ITenantActual, params IInterceptor[])` más un
   `DbCommandInterceptor` que arroje un error transitorio de Npgsql (SqlState `40001`
   o `57P01`) en el primer INSERT; afirmar exactamente una fila por entidad y el
   `valorAnterior` verdadero. Referencia: `EscriturasSinReintentoTests` con
   `InterceptorQueRompeLaPrimeraEscritura(tabla, sqlState)` como plantilla (el interceptor
   nació en `BajasDeOrganizacionTests`, etapa 20, clavado en `auditoria`). Aplica
   `mutation-proof-tests`: revertir a la estrategia con reintento debe volver el test
   ROJO con el conteo duplicado — esa es la evidencia de mutación exigida.

4. **Declarar el residual con honestidad, y hacerlo CENTRALMENTE.** Bajo la forma (b), un fallo
   transitorio se expone al operador aunque la escritura pueda haberse confirmado igualmente (el
   commit ocurrió justo antes de que la conexión se cortara). Eso ya NO se resuelve pantalla por
   pantalla: `ManejadorDeErrores` traduce los SQLSTATE transitorios (`40001`, `40P01`, `57P01`, la
   clase `08` entera) y `DbException.IsTransient` a un **`503` con código `resultado_incierto`**
   cuyo mensaje ya dice "verificá el listado antes de reintentar". El predicado vive UNA sola vez,
   en `Ways.Application.Abstracciones.FallosTransitorios`. Toda pantalla rinde `ErrorApi.mensaje`
   para un código que no conoce, así que la copia llega sola. Un sitio (b) nuevo no necesita copia
   propia; escribir una a mano es duplicarla.

   **Tres carve-outs, y son los únicos:**
   - **El residual central NO cubre el ARRANQUE.** `InicializadorDeBaseDeDatos` corre fuera de
     todo pipeline HTTP: no hay request, no hay `ManejadorDeErrores` y no hay operador del otro
     lado. Sacarle el reintento tiene un precio explícito — un blip transitorio **aborta el host**
     en vez de reintentar cinco veces — y se paga a cambio de no duplicar en silencio: el arranque
     siguiente vuelve a correr el backfill y es idempotente porque recalcula su conjunto pendiente
     desde la base. Un sitio de arranque documenta ESTO en su comentario, nunca "ya lo cubre el
     central".
   - **Un sitio con residual PROPIO sí escribe copia propia.** Si recuperarse del commit ambiguo
     necesita un paso que "verificá el listado" no nombra, la copia central miente por omisión:
     `ServicioDeAprovisionamiento.CrearTenantAsync` atrapa el fallo transitorio y tira un
     `ErrorDominio("resultado_incierto", …, 503)` que manda a restablecer la contraseña del admin,
     porque el `passwordTemporal` se devuelve UNA vez y se fue con la respuesta que nunca llegó.
     El código se conserva; lo que cambia es el mensaje.
   - **La copia del commit ambiguo es de las ESCRITURAS.** Sobre un método seguro
     (GET/HEAD/OPTIONS) el mismo fallo transitorio sale como `503 servicio_no_disponible` con copia
     neutra: una lectura no deja residual y no hay nada que verificar antes de reintentar.

## Decision Gates

| Situation | Action |
|---|---|
| Nuevo `ExecuteAsync` con reintento que agrega entidades | `ChangeTracker.Clear()` primero + toda carga/construcción dentro del lambda |
| Escritura no idempotente sin clave de idempotencia | `FabricaDeEstrategiaSinReintento` |
| Escritura no idempotente CON clave de idempotencia precomiteada (ej. `numero`) | Forma (a): el reintento es el único consumidor de esa clave — quitarlo la deja muerta |
| Fila de auditoría dentro de la transacción | Construida dentro del lambda, nunca capturada de afuera |
| Reintento puede enmascarar un commit ambiguo (ej. baja + relectura 404) | Preferir (b) sobre (a) |
| Test de la escritura | Interceptor transitorio real (`40001`/`57P01`) + conteo exacto + `valorAnterior` |
| Test de una escritura que NO inserta (baja lógica: `UPDATE deleted_at`) | `InterceptorQueRompeLaPrimeraEscritura(..., ClaseDeSentencia.Update)` — el interceptor de INSERT no dispara nunca |
| Barrido de sitios | Todos los proyectos (`Ways.Infrastructure` incluido), y re-verificar las exenciones heredadas contra este gate |

## Known Open Sites

**Alcance del barrido — leer esto antes de creerle a la lista de abajo.** El barrido de
`fix/retry-double-add` cubrió UNA sola clase de sitio: los que abren un lambda EXPLÍCITO
(`CreateExecutionStrategy().ExecuteAsync(...)`) y agregan o mutan entidades adentro. Nada más.

**OPEN — `SaveChangesAsync` IMPLÍCITO bajo el retry global.** `EnableRetryOnFailure` es global, así
que un `db.X.Add(...); await db.SaveChangesAsync();` SIN ningún lambda a la vista **también** se
reintenta: `SaveChangesAsync` resuelve su propia estrategia desde la configuración del `DbContext`
y reejecuta el guardado. Ante un commit ambiguo, la entidad sigue `Added` en el `ChangeTracker` —
el `SaveChanges` no llegó a aceptar los cambios— y el reintento la **re-INSERTA**. Es exactamente
el mismo defecto que la regla 1, sin el lambda que lo hacía visible. Ejemplo conocido:
`ServicioDeLotes.CrearAsync` (`src/Ways.Application/Stock/ServicioDeLotes.cs:483-484`).
**Esta clase NO se corrigió en este PR y queda fuera de su alcance a propósito**: es un
barrido nuevo (todo `SaveChangesAsync` de escritura del repo, en todos los proyectos) con su propia
tanda de pruebas, no un arrastre de éste. Al abrirlo, aplicar las mismas reglas 1–4 y actualizar
esta sección.

Sobre la clase que SÍ se barrió (lambdas explícitos) no queda ninguno abierto. Los dos que esta
skill dejó abiertos en la etapa 20
(`ServicioDePrecios.AbrirNuevoPrecioAsync`, `ServicioDeUsuarios.CrearAsync`) se cerraron junto
con los otros nueve: `ServicioDeClientes.CrearAsync`, `ServicioDeArticulos.CrearAsync`,
`ServicioDeCertificados.RegistrarAsync`, `ServicioDeListasPrecio.CrearAsync`,
`ServicioDeOfertas.CrearAsync`/`ActualizarAsync`/`EliminarAsync`,
`ServicioDeAprovisionamiento.CrearTenantAsync` y
`InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync`.

`ServicioDeVentas.EmitirAsync` NO está en esa lista y NO usa la fábrica: es el único sitio del
audit que se corrigió con la forma (a) — ver la lección 3 de abajo.

Las dos correcciones del judgment-day `fix/retry-double-add`, que son las que hay que leer antes
de declarar un barrido completo:

- **El barrido de la etapa 20 se declaró completo y no lo era.** Solo miró `Ways.Application`.
  `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync` vive en `Ways.Infrastructure`
  y hacía `Add` de una `ListaPrecio` y un `Cliente` dentro de un lambda reintentable, con el
  número de Consumidor Final re-sorteado por intento: duplicado SILENCIOSO en el arranque, sin
  ningún índice único que lo frenara. **Barrer TODOS los proyectos, no solo el de aplicación.**
- **Un sitio de la lista "reintentable a propósito" fallaba el propio decision gate de esta
  skill.** `ServicioDeOfertas.EliminarAsync` es una baja lógica: el gate "reintento puede
  enmascarar un commit ambiguo (baja + relectura 404)" la nombra literalmente, y sin embargo
  estaba clasificada como intocable. **Una lista de exenciones se re-verifica contra el gate, no
  se hereda.**

Tres lecciones del barrido, para el próximo sitio:

1. **Un índice único NO es una mitigación, es un disfraz.** Donde el duplicado choca contra una
   unicidad, el reintento no duplica: devuelve un 409 sobre una operación que quizás sí
   persistió. Sigue siendo el mismo defecto y se corrige igual.
2. **Un paso de numeración por ADO crudo SÍ puede quedarse con la estrategia reintentable**, y
   conviene que se quede: reservar de nuevo solo avanza el contador ("gaps are accepted"), nunca
   duplica una fila.

3. **Quitar el reintento no es gratis: hay que preguntarse quién CONSUMÍA la clave de
   idempotencia.** `ServicioDeVentas.EmitirAsync` se convirtió a la forma (b) y eso rompió el
   checkout: su guarda `BuscarPorNumeroComprometidoAsync` solo dispara cuando el REINTENTO
   re-entra al lambda con el mismo `numero` ya comiteado. Sin reintento, la guarda queda muerta —
   `SolicitudDeVenta` no lleva número, así que el reenvío del cajero sortea uno NUEVO y emite un
   SEGUNDO comprobante, con su segundo descuento de stock y sus segundos movimientos de caja y
   cuenta corriente. La forma correcta ahí es la (a): `db.ChangeTracker.Clear()` como PRIMERA
   sentencia del lambda (mata la duplicación del tracker) + la guarda inmediatamente después
   (mata el commit ambiguo). Numeración reintentable en su propia transacción + escritura
   reintentable con tracker limpio y guarda es el precedente, no "numeración reintentable +
   escritura sin reintento".

Sitios que quedan reintentables A PROPÓSITO (no tocar sin releer esto): los pasos de numeración
cruda de Ventas/Remitos/Presupuestos/FacturacionDeRemitos/CuentaCorriente/`ServicioDeOrdenesDeCompra.EnviarAsync`,
el paso de ESCRITURA de `ServicioDeVentas.EmitirAsync` (forma (a), lección 3), el lambda de
reconciliación de `ServicioDeLotes` (`ServicioDeLotes.cs:219` — NO `CrearAsync`, que es de la clase
OPEN de arriba),
`ServicioDeListasPrecio.ActualizarAsync` y los lambdas de solo-UPDATE de
`ServicioDeOrganizacion.EnUnaTransaccionAsync`. Son diez, no nueve: el paso de numeración de
`EnviarAsync` faltaba en esta lista y es exactamente igual que los otros cinco.

La prueba estructural que congela la lista es
`Ways.Application.Tests.Abstracciones.EscriturasSinReintentoEstructuralesTests` (sin contenedor);
la conductual, `Ways.IntegrationTests.EscriturasSinReintentoTests`.

## Verification

Antes de commitear: identificar si el lambda es (a) o (b) y documentarlo en el nombre
del método o un comentario breve; correr el test con interceptor transitorio y
confirmar fila única; agregar el sitio a la lista congelada de
`EscriturasSinReintentoEstructuralesTests` (o justificar por qué queda reintentable).
Si es (b), no hace falta copia de UI nueva: `ManejadorDeErrores` ya devuelve
`503 resultado_incierto` con el texto correcto (regla 4).
