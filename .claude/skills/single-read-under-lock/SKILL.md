---
name: single-read-under-lock
description: "Trigger: new or modified write path that takes a row or advisory lock (`FOR UPDATE`, `FOR SHARE`, `FOR KEY SHARE`, `pg_advisory_xact_lock`, `TomarLock*Async`, `Bloquear*Async`) and then mutates an EF-tracked entity, or derives a `valorAnterior`/`estadoAnterior` for an audit row. The entity gets exactly ONE read, born inside the transaction, after the lock — a second read resolves against EF's identity map and changes nothing."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load cuando un camino de escritura abre transacción y toma un lock (de fila o advisory) y
después muta una entidad TRACKEADA por EF, o cuando arma un `valorAnterior`/`estadoAnterior`
para una fila de auditoría a partir de una entidad.

Nacida de un barrido de cuatro ocurrencias en el mismo repo:
`ServicioDeOrganizacion.ActualizarModoPuntoVentaAsync` (los dos modos de falla),
`ServicioDeOfertas.ActualizarAsync` (lost update silencioso),
`ServicioDeOrganizacion.EliminarTenantAsync` y `ServicioDeUsuarios.EliminarAsync`
(rastro que miente). Las cuatro tenían el lock correcto y la transacción correcta: lo que
estaba mal era DÓNDE nacía la lectura.

## Hard Rules

1. **La entidad que se muta se lee UNA sola vez, ADENTRO de la transacción y DESPUÉS del
   lock.** Una lectura previa al lock es una foto de un estado que esta transacción nunca
   observó bajo el lock.

   ```csharp
   // DO
   return await EnUnaTransaccionDeBajaAsync(async () =>
   {
       await TomarLockDePuntoVentaAsync(id, ct);

       var puntoVenta = await BuscarPuntoVentaAsync(id, ct);   // la ÚNICA lectura

       var modoAnterior = puntoVenta.Modo;
       puntoVenta.Modo = modo;
       ...
   }, ct);

   // DON'T
   var puntoVenta = await BuscarPuntoVentaAsync(id, ct);       // foto pre-lock
   return await EnUnaTransaccionDeBajaAsync(async () =>
   {
       await TomarLockDePuntoVentaAsync(puntoVenta.Id, ct);
       puntoVenta.Modo = modo;                                 // muta la foto vieja
       ...
   }, ct);
   ```

   El repo ya tenía la regla escrita en dos lugares antes de este barrido, y los dos son
   buenas referencias: el doc-comment de `ServicioDeCatalogo.EliminarAsync` ("el LOCK se toma
   ANTES de cargar la entidad... cargarla primero serviría el valor STALE del identity map") y
   `PayloadDeAuditoria.ReliquidacionDeCc` ("`saldoAnterior` sale del `SELECT … FOR UPDATE` ya
   tomado, nunca de un re-read"). Estaba escrita en dos y violada en cuatro: por eso existe
   esta skill.

2. **Agregar una SEGUNDA lectura no arregla nada.** Con la instancia ya trackeada, la
   relectura resuelve contra el identity map de EF y devuelve la MISMA instancia vieja: EF
   no refresca una entidad trackeada con los valores de una consulta posterior. Lo único que
   la relectura sí detecta es la DESAPARICIÓN de la fila (el filtro `BajaLogica` se evalúa en
   SQL, así que una fila ya dada de baja devuelve 0 filas y da un 404 limpio) — por eso
   `EliminarTenantAsync` y `ServicioDeUsuarios.EliminarAsync` parecían cubiertos y no lo
   estaban. Si hay una pre-lectura, hay que BORRARLA, no acompañarla.

3. **Son DOS fallas independientes y cada una necesita su propia aserción.**
   - **Fidelidad del rastro**: cualquier `valorAnterior`/`estadoAnterior` derivado de la foto
     pre-lock afirma un estado previo que la transacción nunca vio. El perdedor de la carrera
     escribe una fila de auditoría que MIENTE.
   - **Lost update SILENCIOSO** (la peor): esa foto es el valor ORIGINAL de EF para la
     detección de cambios. Si el valor pedido COINCIDE con el original viejo, EF no detecta
     cambio y OMITE la columna del `UPDATE` — la escritura desaparece y el endpoint contesta
     200 con el valor del ganador. Suele pasar desapercibido porque el `UPDATE` igual se emite
     por `updated_at`, así que no hay ni un `SaveChanges` vacío que llame la atención.

4. **Antes de "arreglar", verificar que el lock REALMENTE serializa al escritor en pugna.**
   Nombrar al otro escritor y la columna que los dos tocan. Si el lock no cubre la fila que se
   muta, mover la lectura es cosmético: no cierra nada y disimula el hueco real, que es un lock
   faltante. El caso que fundó esta regla fue `ServicioDeArticulos.ActualizarAsync`: tenía la
   forma del defecto, pero sus cinco `BloquearSiEstaVivaAsync` son `FOR KEY SHARE` sobre los
   CATÁLOGOS referenciados (área/categoría/marca/grupo/proveedor), nunca sobre `articulos`, así
   que dos ediciones concurrentes del mismo artículo no se serializaban con nada. Se reportó como
   deuda de LOCK y se cerró aparte agregando el `FOR UPDATE` que faltaba sobre la propia fila —
   no moviendo la lectura, que sola no habría cerrado nada.

   Cuando la conclusión es "falta un lock", el lock nuevo va PRIMERO en la transacción y hay que
   escribir por qué no abre un ciclo. En artículos el orden es fila del artículo (`FOR UPDATE`) y
   después los catálogos (`FOR KEY SHARE`): la baja de un catálogo toma el orden inverso —lockea
   el catálogo y después LEE `articulos`— pero lo lee SIN lock (`InspectorDeUso` es read-only), así
   que nunca espera por la fila del artículo y no hay ciclo. Verificar esa asimetría con el
   inventario de locks en la mano, no de memoria.

   Y la precisión que sale de mutar ese lock, que es fácil describir mal: el lock explícito NO es
   lo que hace que los dos escritores se serialicen. El `UPDATE` de `SaveChangesAsync` toma su
   propio lock de fila y espera igual. Lo que el lock explícito agrega es serializar ANTES DE LEER:
   sin él el perdedor lee su foto, espera recién al escribir, y escribe valores derivados de un
   estado ya pisado. Consecuencia para los tests: una aserción de "se observó bloqueado"
   (`pg_stat_activity`, `wait_event_type = 'Lock'`) NO mata al mutante que borra el lock —
   sobrevive, porque el bloqueo sigue ocurriendo. Lo que lo mata es la aserción sobre la FILA
   RELEÍDA. Comprobado corriéndolo, no razonándolo.

5. **Lo que NO está afectado — decirlo explícitamente en vez de "arreglarlo".**
   - Escrituras 100% ADO crudo (`UPDATE ... RETURNING`, upsert): el lock y la mutación son un
     solo statement atómico y no hay entidad trackeada. Es el patrón dominante del repo
     (Ventas, Compras, Stock, Turnos, CuentaCorriente) y es correcto.
   - Lecturas por PROYECCIÓN (`.Select(...)` a DTO/record/tupla), escalares/agregados
     (`AnyAsync`, `CountAsync`, `SumAsync`) o `AsNoTracking()`: no pasan por el identity map.
   - Pre-lecturas que solo VALIDAN y cuya instancia nunca se muta.
   - Inserts (`db.X.Add`) de entidades nuevas: no hay foto vieja.
   - Columnas cuyo original viejo NUNCA puede igualar el valor nuevo: `deleted_at` de `null` a
     un instante siempre es un cambio detectado, y una baja concurrente ya murió en el 404 de
     la regla 2 (por eso `EliminarEmpresaAsync`/`EliminarPuntoVentaAsync` están sanas).

6. **Si hace falta un valor ANTES del lock, sacarlo de una proyección escalar de una columna
   INMUTABLE.** La clave del lock es el caso típico: no se puede tomar el lock sin conocerla.
   `ServicioDeUsuarios.EliminarAsync` resuelve el `id_tenant` del sujeto con
   `.Select(u => new { u.IdTenant })` — inmutable para una cuenta, no materializa entidad, y ese
   mismo statement da el 404 barato sin pagar transacción. Nunca usar una entidad trackeada para
   esto.

7. **El test es un rendezvous determinístico, nunca una carrera probabilística.** Un
   `DbTransactionInterceptor` que pausa en `TransactionStartedAsync` (después de
   `BeginTransaction`, antes del primer statement, o sea después de la pre-lectura del mutante y
   antes del lock). El perdedor corre sobre una factory con el interceptor
   (`fixture.WithWebHostBuilder`), el ganador sobre el cliente pelado de `fixture` para que nunca
   se pause. Plantilla:
   `OrganizacionTests.ElFlipDeModoQuePierdeLaCarreraAuditaYEscribeSobreElEstadoQueVioBajoElLock`.

8. **Para el lost update, el valor que pide el perdedor tiene que COINCIDIR con la foto vieja.**
   Si pide algo distinto, EF detecta el cambio, la columna entra al `UPDATE` y el mutante
   SOBREVIVE. Y la aserción discriminante es la FILA releída desde un contexto nuevo: el cuerpo
   de la respuesta suele proyectar la instancia ya mutada en memoria, así que afirma `true`
   mientras la base dice `false` — el cuerpo no mata el mutante, la fila sí.

9. **Evidencia de mutación, una por afirmación** (`mutation-proof-tests`). El mutante es
   exactamente la forma original: devolver la lectura a antes del lock. Si el test afirma las dos
   fallas de la regla 3, hay que ver morir a CADA UNA por separado (neutralizando temporalmente
   las otras aserciones), porque el primer assert que falla tapa a los que siguen.

10. **Son DOS cláusulas y cada una tiene su propio mutante — no confundirlas.** Verificado
   corriéndolo, no razonándolo:
   - **Cláusula "existe una lectura post-lock"**: mata al ghost edit (escribir sobre una fila que
     otro escritor ya dio de baja). Su mutante es BORRAR la lectura post-lock entera. Un mutante
     que devuelve la lectura a antes del lock pero CONSERVA una consulta post-lock descartada
     (`_ = await BuscarAsync(id, ct);`) deja el test VERDE, y está bien que lo deje: el filtro
     `BajaLogica` de esa consulta ya devuelve 0 filas y el 404 sale igual. Ese mutante no
     reintroduce ningún ghost edit.
   - **Cláusula "la instancia mutada ES la lectura post-lock"**: mata al lost update silencioso.
     Su mutante es exactamente ese `_ = await ...` descartado.
   Un solo test no cubre las dos. Si el doc-comment de un test afirma matar un mutante, hay que
   CORRERLO: acá una de esas afirmaciones era falsa y solo la corrida lo mostró.

11. **Mover la lectura puede invalidar tests de concurrencia que ya existían, sin que su garantía
   cambie.** Un rendezvous montado sobre "la primera consulta EF a la tabla" estaba apuntando, sin
   decirlo, a la lectura PRE-lock. Con la lectura adentro, ese punto pasa a estar DESPUÉS del
   lock: el primer participante llega al barrier con el lock tomado, el segundo se queda esperando
   el lock y nunca llega al barrier, y el barrier muere por timeout — 500 que tapa lo que el test
   afirmaba (ocurrió en los tres tests de concurrencia de `OfertasEndpointsTests`). El arreglo NO
   es relajar el test: es mover su punto de encuentro a `TransactionStartedAsync`, el último lugar
   que los dos participantes alcanzan SIN haber pedido el lock. Y si el test dependía de una
   ASIMETRÍA entre los dos lados (uno gateaba pre-lock y el otro post-lock, para forzar un ganador
   determinístico), esa asimetría desaparece: hay que pasar a pausa-y-liberación (uno pausado en
   `TransactionStartedAsync`, el otro corriendo entero sobre el cliente sin interceptor). Después
   de cambiar el mecanismo, re-probar que el test sigue matando a SU mutante original — el de la
   cláusula que ese test existe para cubrir, que puede no ser el de esta skill.

## Decision Gate

| Situación | Acción |
|---|---|
| Lock + mutación de entidad trackeada | La lectura nace adentro de la transacción, después del lock |
| Ya hay una pre-lectura y se agrega una relectura | No alcanza: borrar la pre-lectura (regla 2) |
| `valorAnterior` sale de una entidad | Tiene que salir de la lectura post-lock, con su propia aserción |
| El lock no cubre la fila que se muta | Deuda de LOCK, no de lectura: reportarla, no maquillarla (regla 4) |
| Se decide cerrar esa deuda | El lock nuevo va PRIMERO en la transacción, con su análisis de ciclo escrito (regla 4) |
| Escritura ADO cruda / proyección / escalar / insert | No afectado — decirlo explícitamente |
| Solo se muta `deleted_at` bajo el filtro de baja lógica | No afectado: el original `null` siempre difiere (regla 5) |
| Hace falta la clave del lock antes del lock | Proyección escalar de columna inmutable (regla 6) |
| Escribiendo el test | Rendezvous en `TransactionStartedAsync`, valor pedido = foto vieja, aserción sobre la fila releída |
| Probando el ghost edit | Mutante = borrar la lectura post-lock ENTERA, no devolverla a pre-lock (regla 10) |
| Un test de concurrencia que ya existía se pone rojo o tira 500 | Su rendezvous apuntaba a la lectura pre-lock: moverlo a `TransactionStartedAsync`, o a pausa-y-liberación si dependía de una asimetría (regla 11) |
