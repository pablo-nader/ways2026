> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 9. Dispositivos de escritorio (stage-desktop-pos)

### 9.4 Rendición de la cola offline y guarda de cierre de turno

El agujero que cierra: un turno se podía cerrar mientras un dispositivo de escritorio todavía
tenía ventas sin drenar en su cola local. La guarda existente era SOLO del cliente (`Pos.tsx`,
`CierreDeCaja.tsx`) y leía el IndexedDB LOCAL, así que cerrar desde OTRA máquina o desde la web
leía un almacén vacío y pasaba — esas ventas encoladas después drenaban y caían en el turno
siguiente, corrompiendo los dos arqueos (o rebotaban con `409 turno_no_abierto`, que es el caso
menos malo de los dos).

**Cuatro columnas nuevas en `reservas_numeracion` (migraciones `RendicionDeColaDelDispositivo` y
`RendicionSaldadaEnReservaNumeracion`, DB CHANGE GATE aprobado).** No hay tabla nueva: el bloque
vivo es exactamente el lugar donde vive la capacidad de vender offline
(`ExigirNumeroPreasignadoPropioAsync` rechaza cualquier número preasignado fuera de un bloque), así
que es el lugar donde vive su rendición.

| Columna | Tipo | Significado |
| --- | --- | --- |
| `entregado_hasta` | `bigint NULL` | Número más alto que el dispositivo ya le imprimió a un cliente (`proximo - 1` de su puntero local). `desde - 1` ⇒ todavía no repartió ninguno. NO es el `consumido_hasta` rechazado en §9.2: un número impreso que sigue en la cola local no tiene fila en `comprobantes_venta`, así que este dato NO es derivable del servidor. |
| `pendientes` | `integer NULL` | Ventas que el dispositivo declara sin llegar al servidor (outbox + rechazadas). Existe para que el rechazo del cierre pueda decir CUÁNTAS faltan. |
| `reportado_at` | `timestamptz NULL` | Cuándo rindió. `NULL` ⇒ nunca rindió, y la guarda falla CERRADO. |
| `rendicion_saldada_at` | `timestamptz NULL` | Cuándo un supervisor forzó un cierre **mientras este bloque, ya abandonado, lo bloqueaba**: sus números sin rendir quedaron aceptados como tales. `NULL` ⇒ el bloque sigue contando para la guarda, esté vivo o abandonado. Un bloque VIVO nunca se salda (sigue acumulando evidencia: no hay nada terminado que aceptar). |

`rendicion_saldada_at` no lleva CHECK a propósito (gate del owner): el forzado tiene que poder
saldar un bloque abandonado con reporte o sin ninguno — un bloque que nunca rindió es en sí mismo un
motivo de bloqueo, así que no hay ninguna invariante que afirmar contra las otras columnas. Su
migración trae un backfill obligatorio (`SET LOCAL app.acceso = 'plataforma'` en el mismo bloque,
`rls-migration-backfills`): al dejar de excluir los bloques abandonados, cada bloque abandonado
histórico habría entrado en el alcance de la guarda con `reportado_at NULL` — `SinReporte`,
bloqueando para siempre los cierres de su punto de venta sin que nadie pudiera rendirlos. Ese
backfill es correcto UNA vez: rodar su `Down` y volver a aplicar el `Up` saldaría también los bloques
abandonados posteriores al despliegue, incluidos los que estaban bloqueando de verdad con ventas sin
drenar (advertencia escrita en la migración).

**Tampoco lleva índice nuevo, y el costo va dicho completo:** las filas que la guarda escanea NO
están acotadas. `ix_reservas_numeracion_punto_venta` cubre su entrada `(id_punto_venta, id_tenant)`,
pero adentro entra TODO bloque no saldado del punto de venta —uno más cada ~81 ventas offline por
dispositivo (`UMBRAL_DE_REPOSICION` = 20 sobre `CANTIDAD_A_RESERVAR` = 100)— y cada uno paga su
propio escaneo lateral de `comprobantes_venta`; un bloque sale de ese escaneo solo si un forzado lo
salda o si se revoca el dispositivo. O sea que el costo de la guarda crece con la HISTORIA del punto
de venta, no con su estado. Acotarlo pide un índice parcial por `rendicion_saldada_at`: migración
aparte.

Tres CHECK sobre las otras columnas, con sus backstops de traducción en `ManejadorDeErrores`
(`db-error-backstops`):
`ck_reservas_numeracion_entregado_en_rango` (`entregado_hasta IS NULL OR entregado_hasta BETWEEN
desde - 1 AND hasta`), `ck_reservas_numeracion_pendientes_no_negativo` y
`ck_reservas_numeracion_reporte_consistente` (las tres columnas se escriben juntas o ninguna:
media rendición volvería ambiguo el fail-closed).

**`POST /api/pos/rendicion-de-cola`.** Device-only (`Politicas.RequiereDispositivo` apilada sobre
`OperacionDePos`) y siempre contra SU propio punto de venta (`PoliticaDeModoDePuntoVenta`, reusada
tal cual) — el cuerpo es `{ codigoTipoComprobante, entregadoHasta, pendientes }`, sin
`idPuntoVenta` ni `idDispositivo`, mismo criterio que `GET /api/pos/instantanea`. Responde `204`.
Escribe el bloque VIVO (`abandonada_at IS NULL`) con SQL crudo, por
`AsignadorDeNumeroComprobante.RegistrarRendicionAsync`, que sigue siendo el único escritor de las
columnas del reporte. La única otra escritura cruda de esta tabla es
`ServicioDeTurnos.MarcarRendicionSaldadaAsync`, que toca exclusivamente `rendicion_saldada_at`. Rechazos: `403 prohibido` (sin claim, o dispositivo revocado — nunca
`404`), `400 pendientes_invalido`, `400 tipo_comprobante_invalido`, `409
rendicion_sin_bloque_vivo`, `400 entregado_hasta_invalido`, `409 rendicion_regresiva`, `409
rendicion_de_bloque_reemplazado`.

**`entregado_hasta` es una MARCA DE AGUA: el reporte nunca puede bajarla.** El `UPDATE` lleva el
conjunto `$1 >= COALESCE(entregado_hasta, desde - 1)`, así que un POST en vuelo que llega tarde —o un
cliente cuyo puntero local se reinició— se rechaza con `409 rendicion_regresiva` en vez de pisar un
valor más alto (la igualdad sí pasa: un reintento honesto del mismo valor tiene que seguir
funcionando). El servicio pre-valida el piso sobre la fila que ya leyó para no pagar el statement en
el camino normal, pero quien lo IMPONE es ese conjunto, que además cubre la carrera entre dos
rendiciones concurrentes. Cuando el `UPDATE` no afecta ninguna fila, el servicio relee —por
IDENTIDAD de la reserva, no solo por rango— para separar las TRES causas: sin bloque vivo → `409
rendicion_sin_bloque_vivo`; el MISMO bloque vivo con la marca más alta → `409 rendicion_regresiva`;
OTRO bloque vivo → el bloque rotó en esa ventana (`ReservarBloqueAsync` abandona y repone en una sola
transacción) y el `UPDATE` murió contra el piso del bloque NUEVO → `409
rendicion_de_bloque_reemplazado`. Sin el término de identidad, ese tercer caso salía como
`rendicion_regresiva` citando un piso que el dispositivo nunca declaró, de un bloque sobre el que
nunca rindió.

**La guarda, en los DOS modos de cierre** (`POST /api/caja/turnos/{id}/cierre` y
`.../cierre-por-retiro`), dentro de la transacción del cierre y antes de escribir nada más, así un
rechazo deshace también el `UPDATE` guardado que ya marcó el turno cerrado. Por cada bloque del punto
de venta todavía NO saldado (`rendicion_saldada_at IS NULL`, vivo o abandonado) cuyo dispositivo siga
VIGENTE (`dispositivos.deleted_at IS NULL`), `ReglaDeRendicionDeCola` (pura, sin base) bloquea con
`409 rendicion_de_dispositivo_pendiente` si: (a) nunca rindió; (b) **estando VIVO**, el reporte tiene
más de `VentanaDeFrescura` = 5 minutos (15 ciclos de los 20 s de `INTERVALO_DE_SINCRONIZACION_MS`);
(c) declara `pendientes > 0`; o (d) faltan comprobantes en `[desde, techo]`, contando
`comprobantes_venta` por `(id_punto_venta, tipo)` vía `tipos_comprobante.codigo`.

La frescura de (b) es el ÚNICO disyunto que depende de que el bloque siga vivo, y la distinción no es
una sutileza: un bloque vivo todavía puede rendir y todavía puede repartir números nuevos, así que un
reporte viejo no dice nada de lo que vendió después de mandarlo. Un bloque abandonado no puede rendir
nunca más (`RegistrarRendicionAsync` solo toca el vivo) ni va a repartir un número más: su evidencia
quedó congelada al rotar, así que exigirle frescura rechazaría TODO cierre del punto de venta cinco
minutos después de cada reposición rutinaria —sin ningún hueco que mostrar, y sin salida para un
cajero Vendedor, que no puede forzar. Los otros tres disyuntos valen igual para vivos y abandonados.
Residual conocido de (c) sobre un abandonado: su `pendientes` quedó congelado y nadie puede
corregirlo, así que sigue bloqueando aun cuando esas ventas después hayan llegado — ahí la salida es
el forzado (que lo salda definitivamente) o revocar el dispositivo.

El `techo` de (d) lo deriva el SERVIDOR y no el reporte: es
`GREATEST(entregado_hasta declarado, máximo número del bloque que ya llegó)`. Sin eso, declarar
`desde - 1` con `pendientes = 0` vaciaba el rango esperado y el bloque pasaba con cualquier cosa en la
cola. Lo que (d) cubre, dicho sin adornos: **que falten comprobantes del tramo verificado**, y que
una retracción del dispositivo (declarar un techo por debajo de lo que el servidor ya vio llegar) ya
no pueda **TAPAR un hueco existente**, porque el rango esperado no se encoge con lo declarado.
Retractarse por sí solo NO bloquea: con llegadas contiguas el techo verificado es el máximo llegado y
los comprobantes del rango son exactamente los esperados. Lo que NO cubre, y no puede porque es
inforjable desde el servidor: un número que el
dispositivo imprimió, nunca mandó y nunca declara — de ese caso no hay ninguna traza del lado del
servidor. El reporte no queda "verificado"; queda acotado por lo que el servidor puede contradecir.

Dos válvulas de escape, ninguna de ellas un mecanismo nuevo: **revocar** el dispositivo muerto
(`DELETE /api/dispositivos/{id}`) saca sus bloques de la consulta, y un forzado supervisado **salda**
los bloques ABANDONADOS que informó como bloqueantes. Abandonar un bloque NO es una de ellas: `ReservarBloqueAsync`
abandona el bloque vivo en cada reposición (`UMBRAL_DE_REPOSICION`), o sea rutina, así que filtrar por
`abandonada_at` hacía que la rotación del bloque borrara el hueco sola. La carrera irreducible (rinde
limpio, pierde señal y vende mientras alguien cierra) se acepta sin locks —el cierre lockea la fila de
`turnos_caja` y corre en READ COMMITTED, así que no cubre `reservas_numeracion`, `dispositivos` ni
`comprobantes_venta`—: un dispositivo ONLINE manda sus ventas directo, y uno OFFLINE deja de poder
rendir, así que su reporte envejece y a los 5 minutos el cierre se bloquea solo.

**Override supervisado.** `SolicitudDeCierre`/`SolicitudDeCierrePorRetiro` aceptan
`forzarSinRendicion` + `motivoSinRendicion` (`dto-contract-honesty`: forzar sin motivo es `400
motivo_requerido`; motivo sin forzado es `400 motivo_sin_forzado`). Exige Supervisor o Admin,
aplicado DENTRO del servicio y en un solo lugar, porque una policy no puede ser condicional a un
campo del cuerpo (las dos rutas siguen bajo `OperacionDePos`: un Vendedor tiene que poder cerrar su
turno). Sin constante espejo en `Politicas`: una policy registrada que ninguna ruta apila no la
evalúa nadie, y el inerte sería justamente el que lleva nombre de control de seguridad. Un forzado
válido escribe `AccionAuditada.CierreForzadoSinRendicion = ("caja.forzado", "turno_caja")` con el
motivo y la lista de bloqueos encontrados — incluso si no había ninguno, para que el motivo declarado
nunca quede aceptado-y-descartado. Esa fila vive DENTRO de la transacción del cierre, así que se
escribe **si y solo si el cierre comitea**: un forzado válido cuyo cierre después falla
(`caja_sin_medio_efectivo_unico`, `arqueo_incompleto`, cualquier rollback) no deja rastro, porque no
hubo cierre forzado que auditar. Sacarla de la transacción para que "siempre" fuera literal rompería
la atomicidad del cierre, que vale más.

En el mismo `COMMIT`, el forzado **salda** (`rendicion_saldada_at`) EXACTAMENTE los bloques
ABANDONADOS que informó como bloqueantes, por id, nunca todos los del punto de venta: un bloque que no
bloqueaba nada no tiene números sin rendir que alguien esté aceptando, y saldarlo lo volvería ciego
para el cierre siguiente. El bloque VIVO queda afuera por la misma razón, y es la mitad
indispensable de la regla: saldarlo lo volvería invisible para siempre —el único conjunto de alcance
de la guarda es `rendicion_saldada_at IS NULL`— mientras el dispositivo sigue vendiendo sobre él, o
sea exactamente la corrupción que la guarda existe para evitar. La convergencia, dicha en voz alta:
un hueco en un bloque VIVO cuesta un forzado por cada cierre hasta que ese bloque rote, y exactamente
UNO después de rotar (ahí su evidencia quedó congelada y, si todavía muestra el hueco, ese forzado lo
salda de forma permanente).

El rastro `caja.forzado` lleva, por bloqueo: `id_reserva` (qué fila), `bloque_vivo` (si esa fila
quedó saldada o no — un bloqueo VIVO se registra pero NO se salda, y vuelve a bloquear el cierre
siguiente; sin este campo el rastro se leería como si lo hubiera aceptado), `id_dispositivo`,
`dispositivo`, `tipo_comprobante`, `bloqueo`, `pendientes`, `desde`, `entregado_hasta` (lo DECLARADO)
y `techo_verificado` (el rango que de verdad se aceptó como sin rendir; es el que informa el mensaje
del hueco, mientras los otros tres motivos no muestran rango).

Un bloque VIVO que bloquea y nunca rota —el dispositivo se rompió y no vuelve— pide un forzado en
cada cierre: ahí el arreglo de fondo es revocar el dispositivo, que lo saca de la guarda de una
vez.

**Estado: completo.** Migraciones `RendicionDeColaDelDispositivo` y
`RendicionSaldadaEnReservaNumeracion`. El dispositivo rinde su cola como
último paso de cada ciclo de `useSincronizacionOffline` (después del drenado, para que el conteo
refleje el estado post-drenado); sin bloque local no rinde nada, y una rendición que falla no rompe
el ciclo. `CierreDeCaja.tsx` revela el override recién ante un `409
rendicion_de_dispositivo_pendiente`, nunca por defecto. Las guardas locales
(`outboxBloqueaCierre`, `irACerrarCaja`) se conservan como defensa en profundidad: siguen
bloqueando al dispositivo que tiene SU propia cola sin drenar, que es un caso que tiene que
drenar, no forzar.

**Hueco conocido, no resuelto:** descartar una venta rechazada (`descartarVentaConError`) la saca de
la cola pero su número ya se gastó y `proximo` nunca rebobina — eso deja un `HuecoDeComprobantes` que
el dispositivo no puede cerrar rindiendo: el cierre siguiente de ese punto de venta pide override.
Mientras ese bloque siga VIVO el override no lo salda, así que el costo es un forzado por CADA cierre
hasta que el bloque rote; una vez abandonado, el primer forzado lo salda y no vuelve (o se revoca el
dispositivo). O sea: no es permanente, pero **cada** bloque que arrastre un número descartado va a
exigir forzados supervisados hasta rotar, y uno más después. Contablemente es correcto
(hay un comprobante en la mano de un cliente que no va a estar en ningún arqueo), pero empuja al
override habituado. El arreglo sería que el dispositivo reporte los números que renunció para
restarlos del esperado: columna nueva, etapa separada.
