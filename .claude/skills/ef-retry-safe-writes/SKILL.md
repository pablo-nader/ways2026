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
   `WaysApiFixture.CrearContextoDeAplicacionConReintentos(params IInterceptor[])` más un
   `DbCommandInterceptor` que arroje un error transitorio de Npgsql (SqlState `40001`
   o `57P01`) en el primer INSERT; afirmar exactamente una fila por entidad y el
   `valorAnterior` verdadero. Referencia: el test de la etapa 20
   `BajasDeOrganizacionTests` (`InterceptorQueRompeElRastro`) como plantilla. Aplica
   `mutation-proof-tests`: revertir a la estrategia con reintento debe volver el test
   ROJO con el conteo duplicado — esa es la evidencia de mutación exigida.

4. **Declarar el residual con honestidad.** Bajo la forma (b), un fallo transitorio se
   expone al operador como un 500 aunque la escritura pueda haberse confirmado
   igualmente (el commit ocurrió justo antes de que la conexión se cortara). El texto
   de la UI para ese caso debe indicar verificar el listado antes de reintentar la
   acción, no reintentar a ciegas.

## Decision Gates

| Situation | Action |
|---|---|
| Nuevo `ExecuteAsync` con reintento que agrega entidades | `ChangeTracker.Clear()` primero + toda carga/construcción dentro del lambda |
| Escritura no idempotente sin clave de idempotencia | `FabricaDeEstrategiaSinReintento` |
| Fila de auditoría dentro de la transacción | Construida dentro del lambda, nunca capturada de afuera |
| Reintento puede enmascarar un commit ambiguo (ej. baja + relectura 404) | Preferir (b) sobre (a) |
| Test de la escritura | Interceptor transitorio real (`40001`/`57P01`) + conteo exacto + `valorAnterior` |

## Known Open Sites

Ninguno. Los dos que esta skill dejó abiertos en la etapa 20
(`ServicioDePrecios.AbrirNuevoPrecioAsync`, `ServicioDeUsuarios.CrearAsync`) se cerraron junto
con los otros ocho que el barrido completo encontró: `ServicioDeClientes.CrearAsync`,
`ServicioDeArticulos.CrearAsync`, `ServicioDeVentas.EmitirAsync` (solo el paso de ESCRITURA),
`ServicioDeCertificados.RegistrarAsync`, `ServicioDeListasPrecio.CrearAsync`,
`ServicioDeOfertas.CrearAsync`/`ActualizarAsync` y
`ServicioDeAprovisionamiento.CrearTenantAsync`.

Dos lecciones del barrido, para el próximo sitio:

1. **Un índice único NO es una mitigación, es un disfraz.** Donde el duplicado choca contra una
   unicidad, el reintento no duplica: devuelve un 409 sobre una operación que quizás sí
   persistió. Sigue siendo el mismo defecto y se corrige igual.
2. **Un paso de numeración por ADO crudo SÍ puede quedarse con la estrategia reintentable**, y
   conviene que se quede: reservar de nuevo solo avanza el contador ("gaps are accepted"), nunca
   duplica una fila. Separar numeración (reintentable) de escritura (sin reintento) es la forma
   correcta cuando el número ya está comiteado y existe una guarda de commit ambiguo que lo
   relee — `ServicioDeVentas.EmitirAsync` es el precedente.

Sitios que quedan reintentables A PROPÓSITO (no tocar sin releer esto): los pasos de numeración
cruda de Ventas/Remitos/Presupuestos/FacturacionDeRemitos/CuentaCorriente,
`ServicioDeLotes`, `ServicioDeOfertas.EliminarAsync`, `ServicioDeListasPrecio.ActualizarAsync`
y los lambdas de solo-UPDATE de `ServicioDeOrganizacion.EnUnaTransaccionAsync`.

La prueba estructural que congela la lista es
`Ways.Application.Tests.Abstracciones.EscriturasSinReintentoEstructuralesTests` (sin contenedor);
la conductual, `Ways.IntegrationTests.EscriturasSinReintentoTests`.

## Verification

Antes de commitear: identificar si el lambda es (a) o (b) y documentarlo en el nombre
del método o un comentario breve; correr el test con interceptor transitorio y
confirmar fila única; si es (b), confirmar que la copia de error de la UI menciona
verificar el listado.
