> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 7. Caja: turnos, arqueos y tesorería

## 7. Caja: turnos, arqueos y tesorería

> **Vocabulario (etapa E1):** el *gasto* pasa a llamarse **pago**: tabla `pagos`, columna `id_pago` y tipo de
> movimiento de tesorería `pago`. Las rutas HTTP pasaron a `/api/pagos…` en E2 y las `/api/gastos…` se quitaron en E2-L (el valor JSON `Gasto` del tipo de
> movimiento sigue).

```sql
turnos_caja (                 -- [operativa]
    id_turno_caja, id_punto_venta,
    id_empleado_apertura, id_empleado_cierre NULL,
    fecha_apertura timestamptz, fecha_cierre timestamptz NULL,
    fondo_inicial numeric(14,2) NOT NULL DEFAULT 0,     -- cambio con el que abre
    estado estado_turno,                     -- enum: abierto | cerrado
    id_medio_pago_efectivo integer NULL,     -- ancla PINEADA al cierre, ver nota abajo
    fecha_recalculo timestamptz NULL,        -- último recálculo administrativo del arqueo
    id_empleado_recalculo integer NULL,      -- quién lo hizo (junto con fecha_recalculo o ninguno)
    observaciones
);
-- Un solo turno abierto por punto de venta: UNIQUE (id_punto_venta) WHERE estado = 'abierto'.
-- El comprobante nace con el id_turno_caja abierto: se acabó el "cerrada = 0" implícito.

> **Estado (judgment-day JD-E5a-2 en el cierre por retiro, DB CHANGE GATE ejercido y aprobado):
> `turnos_caja.id_medio_pago_efectivo` implementada.** `ALTER TABLE turnos_caja ADD COLUMN
> id_medio_pago_efectivo integer NULL` + `fk_turnos_caja_medio_pago_efectivo` compuesta MATCH
> SIMPLE (mismo shape que `fk_arqueos_turno_medio_pago`, con `id_tenant`) +
> `ix_turnos_caja_medio_pago_efectivo` + `ck_turnos_caja_medio_efectivo_solo_cerrado`
> (`id_medio_pago_efectivo IS NULL OR estado = 'cerrado'`). Metadata-only en PG 11+ (columna
> nullable sin default), sin table rewrite.
>
> `medios_pago.comportamiento` es editable después del hecho (`PUT
> /api/catalogos/medios-pago/{id}`) — sin esta columna, el resumen de un cierre re-resolvía el
> medio ancla (efectivo) contra el catálogo ACTUAL en cada lectura, y un turno cerrado hace
> tiempo podía terminar leyendo la fila de `arqueos_turno` equivocada (o ninguna, diferencia
> fantasma `0`) si el comportamiento cambió desde el cierre. Se fija UNA vez, en la MISMA
> transacción de cierre, en los DOS modos (cierre clásico y cierre por retiro), inmediatamente
> después de resolver el ancla — nunca vuelve a re-resolverse para ese turno.
>
> Backfill (migración `TurnosCajaMedioPagoEfectivo`): los turnos YA cerrados heredan el único
> medio del tenant con `comportamiento = efectivo` cuando existe exactamente uno (mismo criterio
> de resolución que `ResolvedorDeMedioDeCajaFisica.Resolver` — todas las filas del catálogo, sin
> filtrar `activo`); si el tenant tiene cero o más de uno, el turno queda `NULL` a propósito
> ("fail-closed, nunca adivinar", mismo criterio que el backfill de `id_remito`). El lector cae al
> catálogo actual solo para ese caso legado (`NULL`).

movimientos_caja (            -- [operativa]  — plata física fuera de la venta
    id_movimiento_caja, id_turno_caja,
    tipo tipo_movimiento_caja,               -- enum: retiro | refuerzo | apertura_cajon
    importe numeric(14,2),                   -- 0 en apertura_cajon (el F12 del legacy, auditado)
    motivo text, id_empleado, creado_el
);

arqueos_turno (               -- el cierre: contado vs esperado, POR MEDIO DE PAGO
    id_arqueo, id_turno_caja, id_medio_pago,
    importe_esperado numeric(14,2),          -- calculado: cobros + fondo − vueltos − pagos − retiros
    importe_declarado numeric(14,2),         -- lo que el cajero contó
    diferencia numeric(14,2),                -- esperado − declarado (el "saldo" del legacy, por medio)
    importe_esperado_original numeric(14,2) NULL  -- esperado del cierre, si un recálculo lo cambió
);
```

> **Recálculo administrativo del arqueo (DB CHANGE GATE aprobado).** Un admin puede editar o dar
> de baja un pago de caja de un turno ya cerrado. En la misma transacción se recalcula
> `importe_esperado` de ese turno con la derivación del cierre y el ancla pineada: las filas que
> cambian guardan su esperado del cierre en `importe_esperado_original` (solo la primera vez), un
> medio que ganó actividad recibe una fila nueva (original `0`, declarado `0`) y ninguna fila se
> elimina (un medio sin actividad queda en esperado `0`). `importe_declarado` nunca cambia: el
> modo de cierre (clásico o por retiro) no queda persistido. El turno guarda la marca del último
> recálculo en `fecha_recalculo`/`id_empleado_recalculo` (`fk_turnos_caja_empleado_recalculo` a
> `usuarios`, simple como la de cierre, y `ck_turnos_caja_recalculo_consistente`: los dos o
> ninguno). Los ledgers no se reescriben: los ajustes de cuenta corriente de proveedor y de
> tesorería se agregan como movimientos `ajuste` nuevos.

**Cerrar caja deja de ser un INSERT de 30 columnas.** Los totales del turno (por área,
por medio de pago, tickets, pagos) se **derivan** de los comprobantes y pagos que
apuntan al turno — no se copian a mano por POST manipulable como hoy (D7 ⚠). El cierre:
calcula esperados, registra `arqueos_turno` con lo declarado, marca `estado = cerrado`
y encadena tesorería. Todo en una transacción.

```sql
movimientos_tesoreria (       -- [operativa] — ex cajaz: el fondo fuera de la caja diaria
    id_movimiento, id_empresa, id_punto_venta, fecha,
    tipo tipo_movimiento_tesoreria,          -- enum: retiro_caja | deposito | pago | ajuste
    id_turno_caja NULL, id_pago NULL, concepto,
    inicio, ingreso, egreso, final,          -- encadenado inicio→final por (id_tenant, id_empresa)
    id_empleado
);

arqueos_recargas / arqueos_recargas_canales  -- se mantienen como en el doc 03 (cajaV)
```

> **Estado (admin-panel-expense-upload, PR1 — RESUELTO):** la cadena `inicio`/`final` se
> re-ancla de `id_punto_venta` a `(id_tenant, id_empresa)` — varios puntos de venta de la misma
> empresa comparten un solo fondo de tesorería, en vez de uno por punto de venta. `id_empresa`
> aterriza NOT NULL (FK compuesta a `empresas`, RESTRICT), resuelto server-side igual que en
> `pagos`, con backfill de una sola vez para las filas preexistentes: el backfill re-computa
> `inicio`/`final` de TODAS las filas existentes como una suma acumulada de
> `(ingreso − egreso)` ordenada por `(fecha, id)` dentro de cada partición `(id_tenant,
> id_empresa)` — el invariante `final = inicio + ingreso − egreso`
> (`ck_movimientos_tesoreria_cadena`) se preserva por construcción. El append a la cadena se
> serializa por `(id_tenant, id_empresa)` con `pg_advisory_xact_lock`
> (`EscriturasDeTesoreria.TomarLockDeEmpresaAsync`), tomado ANTES de leer el último `final` de la
> cadena. `id_pago NULL` aterriza la columna + FK compuesta a `pagos.ak_pagos_id_pago_id_tenant`
> (RESTRICT) y su índice único parcial (`ux_movimientos_tesoreria_id_pago`, solo cuando no es
> nulo).
>
> **Estado (stage-gastos-origen-fondos-pos, PR2 — RESUELTO):** `id_pago` ya tiene escritor: un
> pago de origen `tesoreria` (`ServicioDePagos.EscribirMovimientoDeTesoreriaAsync`) agrega, en la
> MISMA transacción del insert del pago, una fila `tipo = 'pago'` con `id_pago` = ese pago,
> `ingreso = 0`, `egreso = importe` — mismo protocolo lock+lectura+append que el cierre, extraído a
> `EscriturasDeTesoreria` (única clase que escribe esta tabla). El cierre cambia de forma: su fila
> `retiro_caja` pasa a llevar SIEMPRE `egreso = 0` (antes, PR1: `egreso` = suma de TODOS los pagos
> del turno) — un pago `caja_turno` ya salió del cajón, y un pago `tesoreria` ya escribió su
> propio movimiento en el momento en que se registró; restarlo de nuevo al cerrar lo descontaría
> dos veces. `ingreso` sigue siendo la suma de los retiros físicos del turno, sin cambios.
>
> **Estado (stage-tesoreria-por-empresa, PR5 — RESUELTO):** confirmado, UNA sola cadena por
> `(id_tenant, id_empresa)` — hay exactamente tres escritores, los dos de arriba más el pago de
> administración (`ServicioDePagos.RegistrarDeAdministracionAsync`, mismo camino de
> `EscriturasDeTesoreria` que un pago de origen `tesoreria` del POS): cierre de turno
> (`tipo = 'retiro_caja'`, `egreso` SIEMPRE 0), pago de origen `tesoreria` desde el POS y pago de
> administración (ambos `tipo = 'pago'`, `egreso = importe`). La lectura
> (`ServicioDeTesoreria.ListarAsync`/`GET /api/reportes/tesoreria`) toma `id_empresa` como filtro
> OBLIGATORIO (ADR-8) y admite `id_punto_venta` como filtro OPCIONAL adicional sobre esa misma
> cadena — un filtro por punto de venta muestra un SUBCONJUNTO de filas (las originadas ahí), que
> no se encadenan entre sí porque la cadena entera vive a nivel empresa, nunca por punto de venta.

### Cuentas de tesorería

```sql
cuentas_tesoreria (           -- [catálogo por empresa: id_empresa NOT NULL] — Mercado Pago, banco
    id_cuenta_tesoreria, id_empresa,
    nombre citext,                           -- único por (id_tenant, id_empresa) entre las vivas
    saldo_inicial numeric(14,2) NOT NULL DEFAULT 0,
    activo, created_at, updated_at, deleted_at
);

movimientos_cuenta (          -- [operativa — libro append-only] encadenado por cuenta
    id_movimiento_cuenta, id_cuenta_tesoreria, fecha,
    tipo tipo_movimiento_cuenta,             -- enum: cobro | comision | retencion_iibb | retencion_iva |
                                             -- retencion_ganancias | ajuste | transferencia | deposito | retiro
    id_turno_caja NULL, id_medio_pago NULL, concepto,
    inicio, ingreso, egreso, final,          -- encadenado inicio→final por (id_tenant, id_cuenta_tesoreria)
    id_empleado,
    base_bruta numeric(14,2) NULL,           -- base sobre la que se calculó el porcentaje, congelada
    porcentaje_aplicado numeric(6,3) NULL    -- porcentaje vigente al escribir la fila, congelado
);
```

> **Estado (cuentas de tesorería, esquema):** la tesorería de efectivo (`movimientos_tesoreria`,
> una cadena por empresa) y las cuentas digitales (`movimientos_cuenta`, una cadena por cuenta) son
> libros separados: cada uno con su propia cadena y su propio candado. Hay dos escritores: el cierre de turno y el ajuste manual
> de saldo (ver "Quién escribe el libro"); el ABM de cuentas solo lee.
>
> `cuentas_tesoreria`: clave alterna `ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant` (destino de
> las FK compuestas), FK compuesta `fk_cuentas_tesoreria_empresa` a `empresas` (RESTRICT), único
> parcial `ux_cuentas_tesoreria_nombre_empresa` `(id_tenant, id_empresa, nombre) WHERE deleted_at IS
> NULL`, índices `ix_cuentas_tesoreria_tenant` e `ix_cuentas_tesoreria_empresa`. RLS estándar.
>
> `movimientos_cuenta`: `ck_movimientos_cuenta_cadena` (`final = inicio + ingreso − egreso`, igual
> que la tesorería de efectivo), más dos CHECK que `movimientos_tesoreria` no tiene:
> `ck_movimientos_cuenta_importes_no_negativos` (`ingreso` y `egreso` no negativos) y
> `ck_movimientos_cuenta_un_solo_lado` (`ingreso = 0 OR egreso = 0`; una fila con los dos lados en
> cero es válida). FKs compuestas, todas RESTRICT: `fk_movimientos_cuenta_cuenta_tesoreria`,
> `fk_movimientos_cuenta_turno`, `fk_movimientos_cuenta_medio_pago`; `fk_movimientos_cuenta_empleado`
> es simple a `usuarios`, como en la tesorería de efectivo. Índices (ninguno implícito `IX_`): `ix_movimientos_cuenta_tenant`;
> `ix_movimientos_cuenta_cuenta_id` `(id_cuenta_tesoreria, id_tenant, id_movimiento_cuenta)`, que
> sirve de soporte de la FK a la cuenta y de lectura de la cola de la cadena y del libro;
> `ix_movimientos_cuenta_turno`, `ix_movimientos_cuenta_medio_pago` e `ix_movimientos_cuenta_empleado`
> como soporte de las otras FK. `ux_movimientos_cuenta_cierre` es único parcial `(id_tenant,
> id_turno_caja, id_medio_pago, tipo) WHERE id_turno_caja IS NOT NULL AND tipo IN ('cobro',
> 'comision', 'retencion_iibb', 'retencion_iva', 'retencion_ganancias')`: respaldo de idempotencia de las
> filas que escribe el cierre de turno; un ajuste o una transferencia con turno queda fuera. Como
> `id_medio_pago` admite `NULL`, no cubre una fila de turno sin medio. RLS estándar.
>
> **ABM (`/api/cuentas-tesoreria`, solo Admin, lectura incluida).** `ServicioDeCuentasDeTesoreria`: alta con empresa del 
> tenant (400 `referencia_invalida` si no existe o es de otro tenant), edición de nombre, estado y saldo inicial (la empresa no 
> cambia), baja lógica. El nombre repetido entre las vivas de la empresa da 409 `cuenta_tesoreria_duplicada` (pre-chequeo y 
> backstop del 23505 de `ux_cuentas_tesoreria_nombre_empresa`). El saldo inicial puede ser negativo y no cambia una vez que hay 
> movimientos (409 `saldo_inicial_con_movimientos`): la edición lo comprueba tras el `FOR UPDATE` de la fila de la cuenta, con la 
> que choca el `FOR KEY SHARE` de la FK de un primer movimiento concurrente. Para que la protección sea completa, el escritor de movimientos debe leer `saldo_inicial` DENTRO de su transacción y DESPUÉS de tomar un lock sobre la fila de la cuenta que choque con ese `FOR UPDATE`; leerlo antes puede armar el primer movimiento con un saldo inicial viejo. Desactivar una cuenta no desvincula los medios de pago que apuntan a ella. Todos los campos del cuerpo son obligatorios: uno ausente da 400 `id_empresa_requerido`, `nombre_requerido`, `saldo_inicial_requerido` o `activo_requerido`. La baja solo procede sin referencias de 
> `medios_pago` ni `movimientos_cuenta`, incluidas las filas dadas de baja (409 `cuenta_tesoreria_en_uso`); desactivar siempre 
> se puede. El listado trae el saldo actual (`final` del último movimiento o `saldo_inicial`) y `saldoInicialEditable`.
>
> **Quién escribe el libro.** Dos escritores: el cierre de turno y el ajuste manual (más abajo).
>
> **Cierre de turno.** `EscriturasDeCuentaDeTesoreria`, dentro de la transacción compartida por
> los dos modos de cierre (`InsertarArqueosYTesoreriaAsync`), después de la fila de tesorería de efectivo. Por cada medio
> con pagos distintos de cero en el turno y con cuenta, el bruto es el total de pagos del medio en el turno (el mismo número
> del resumen de cierre) y `CalculadorDeMovimientosDeCuenta` (puro) lo descompone en filas, en este orden: `cobro` por el
> bruto (`ingreso` si es positivo, `egreso` si es negativo), y `comision`, `retencion_iibb`, `retencion_iva` y
> `retencion_ganancias` por `bruto × porcentaje / 100`, cada una redondeada por separado a 2 decimales con el punto medio
> hacia afuera del cero, del lado contrario al cobro cuando el bruto es negativo. Cada componente se redondea por separado y
> nada compensa el redondeo: la suma puede pasar del bruto por centavos (bruto 0,50 con cuatro porcentajes de 25 % da cuatro
> egresos de 0,13 y un neto de −0,02), y se acepta. Un componente que redondea a cero se omite y un bruto cero no escribe nada. Cada fila congela `base_bruta` (el bruto con signo) y
> `porcentaje_aplicado` (nulo en el cobro), lleva `id_turno_caja`, `id_medio_pago` (siempre, porque el índice único no
> deduplica `NULL`), `id_empleado` (quien cierra) y la `fecha` del cierre, la misma de la fila de tesorería de efectivo. Los
> medios se procesan por id ascendente, así que dos medios de la misma cuenta encadenan en ese orden. Una cuenta inactiva
> recibe movimientos (desactivar solo impide asignarla a medios nuevos); un medio sin cuenta, con la cuenta dada de baja o con
> una cuenta de otra empresa que la del turno no escribe nada y nunca frena el cierre. Un pago de un medio nunca es negativo
> (`ck_cobros_importe_no_negativo`) y `ValidadorDePagos` rechaza los pagos en una nota de crédito, así que hoy el bruto del cierre no es negativo; el escritor lo soporta como una capacidad que ningún camino actual
> ejerce.
>
> **Ajuste manual (`POST /api/cuentas-tesoreria/{id}/ajustes`, solo Admin).** `ServicioDeAjusteDeCuentaDeTesoreria` escribe UNA fila
> `tipo = 'ajuste'` para conciliar la cuenta con lo que informa la plataforma, sin turno, sin medio, con `base_bruta` y
> `porcentaje_aplicado` nulos, `id_empleado` = quien ajusta y `fecha` = ahora. El cuerpo lleva `concepto` (obligatorio, hasta 200
> caracteres) y EXACTAMENTE uno de `importe` (con signo, distinto de cero, hasta 2 decimales: positivo es `ingreso`, negativo es
> `egreso`) y `saldoObjetivo` (el saldo real de la plataforma: el servidor calcula la diferencia contra el saldo vigente BAJO el
> lock, no contra el que la pantalla mostraba). Una cuenta inactiva se puede ajustar; una dada de baja o ajena da 404 antes de
> validar el cuerpo. Códigos: 400 `concepto_requerido`, `concepto_muy_largo`, `importe_o_saldo_objetivo_requerido`,
> `importe_y_saldo_objetivo_excluyentes`, `importe_invalido`, `saldo_objetivo_invalido`, `saldo_resultante_fuera_de_rango` (el
> `final` no cabe en `numeric(14,2)`); 409 `ajuste_sin_diferencia` (el saldo objetivo ya es el saldo vigente: no se escribe nada).
> Como el ajuste manual de cuenta corriente, no lleva clave de idempotencia ni fila de auditoría: un reenvío del mismo importe
> escribe dos filas (el del mismo saldo objetivo no, porque la segunda diferencia da cero) y la protección es la guarda de
> re-entrada de la pantalla; un 5xx no se reintenta solo. Es el camino para corregir lo que el libro todavía no refleja.
>
> **Lectura (`GET /api/reportes/tesoreria/cuentas?idEmpresa=` y `.../cuentas/{id}/libro`, Supervisor y Admin).**
> `ServicioDeLibroDeCuentas`. Las cuentas vivas de la empresa con su saldo (`final` del último movimiento por id, o
> `saldo_inicial`) y la fecha de ese movimiento, más el aviso de los medios electrónicos activos sin cuenta utilizable (sin cuenta,
> con una dada de baja o con una de otra empresa: la misma regla con la que el cierre descarta una cuenta). El libro de una cuenta
> se ordena por id y nunca por fecha, usa el rango inclusivo del libro de efectivo y trae el saldo de apertura (el `final` del
> último movimiento por id con fecha anterior al rango, o el saldo inicial) y los totales por tipo de TODO el rango. Un medio o un
> usuario dado de baja deja su nombre en `null` y conserva el id. No hay exportación.
>
> **Lo que el libro todavía NO refleja.** (a) Una devolución por nota de crédito de una venta cobrada con un medio digital no
> descuenta de la cuenta, que queda por encima de lo real en el importe devuelto (neto de comisión y retenciones) hasta un
> ajuste manual (ver "Ajuste manual"). (b) Un pago hecho con un medio digital reduce el esperado de ese medio en el arqueo pero no debita la
> cuenta; también se corrige con un ajuste manual. Hoy una devolución no mueve dinero por ningún medio: `ValidadorDePagos` (regla 0c) rechaza con 400
> `pago_en_comprobante_negativo` un comprobante de total negativo con algún pago de importe o vuelto positivo, así que el lector del
> turno, que suma los importes sin el signo del documento, nunca cuenta como cobrado el dinero de una nota de crédito (fijado por
> `UnaNotaDeCreditoConPagosSeRechazaYSinPagosNoTocaLaCuentaNiElArqueo`). La regla vive en el servicio: ninguna CHECK puede
> ligar `cobros` con el total de `comprobantes_venta`, así que una fila anterior a la regla no está protegida por la base.
>
> **Locks del escritor.** Orden: fila del turno (exclusivo) → advisory de la empresa (tesorería de efectivo) → medios
> `FOR SHARE` por id ascendente → cuentas de la empresa del turno `FOR NO KEY UPDATE` por id ascendente (las de otra empresa no se bloquean porque no se acreditan). El medio se lee una sola vez bajo su lock (cuenta
> y porcentajes consistentes hasta el commit; una edición del medio en curso hace esperar al cierre) y cada cuenta se lee con
> su lock (`saldo_inicial` y empresa) antes de leer la cola de la cadena. `FOR NO KEY UPDATE` choca con el `FOR UPDATE` del ABM y
> con sí mismo, pero no con el `FOR KEY SHARE` de los chequeos de FK, que no se bloquean. La idempotencia la da primero el
> UPDATE guardado por estado del turno (409 `turno_ya_cerrado`); si una fila de cierre repetida llegara a la base,
> `ux_movimientos_cuenta_cierre` da 23505 y `ManejadorDeErrores` lo traduce al mismo 409 (supone que solo el cierre escribe filas cubiertas por ese índice). Recalcular el arqueo de un turno
> cerrado (edición de pagos) no toca `movimientos_cuenta`.
>
> **Lock del ajuste manual.** Toma solo la fila de la cuenta con el MISMO `FOR NO KEY UPDATE` y la misma lectura de cola que el cierre
> (`EscriturasDeCuentaDeTesoreria.BloquearYLeerSaldoAsync`, sin filtrar por empresa porque un ajuste no pertenece a un turno), de modo
> que su fila y las del cierre se serializan en una sola cadena; lee la cola DESPUÉS del lock, sin reintento, y el saldo objetivo se
> resuelve contra esa cola. No toma el lock de la empresa ni los de los medios, así que no puede cerrar un ciclo con el cierre.
>
> **Fechas monótonas por cuenta.** Cada escritor fija su hora ANTES de esperar el lock de la cuenta (el cierre la fija al empezar), así que un cierre que espera podía comitear un id mayor con una fecha anterior a la de un ajuste ya comiteado. Por eso, bajo el lock, toda fila que se agrega a una cuenta (componentes del cierre y ajuste) toma `max(hora propia, fecha del último movimiento de la cuenta)`, leída junto con el `final` de la cola (`EscriturasDeCuentaDeTesoreria.FechaMonotona`); las filas de un cierre para una misma cuenta comparten esa fecha. La fila de la tesorería de efectivo conserva la hora del cierre. Con eso, para cada cuenta el orden por fecha (con empates) es el orden por id, y por eso vale que el saldo de apertura de un rango (el `final` del último movimiento anterior a `desde`) más el neto del rango es el saldo al cierre. Consecuencia: si el último movimiento de una cuenta tiene fecha futura (reloj desfasado o una fila mal cargada), TODA fila posterior de esa cuenta lleva esa fecha hasta que la hora real la alcance; se cura sola, no se detecta, y la pantalla avisa cuando el ajuste recién registrado cae fuera del rango que muestra. Las filas anteriores a esta regla no se reescriben: un rango que las cruce podría seguir sin cuadrar.
>
> **Saldo.** La cadena de una cuenta parte de `saldo_inicial`: el `inicio` del primer movimiento
> debe ser igual a él. El saldo actual es el `final` del último movimiento, o `saldo_inicial` si no
> hay movimientos; nunca se suman. `saldo_inicial` no se modifica una vez que la cuenta tiene
> movimientos: lo deberá exigir el servicio de cuentas (sin trigger en la base).
>
> **Lo que la base NO exige y debe exigir un servicio:**
> - La continuidad de la cadena: la base solo valida cada fila (`final = inicio + ingreso - egreso`).
>   Que el `inicio` de un movimiento sea el `final` del anterior y que el primero parta de
>   `saldo_inicial` lo garantiza el escritor del cierre, que agrega bajo el lock de la cuenta
>   y lee `saldo_inicial` y la cola después de tomarlo; que el libro sea solo de altas (sin UPDATE ni
>   DELETE) lo sostiene que ningún servicio los emite.
> - La cuenta de un medio de pago pertenece a la empresa del medio; un medio de todo el tenant
>   (`id_empresa NULL`) solo puede tener cuenta en un tenant de una sola empresa. Lo exige
>   `ServicioDeMediosPago` al asignarla o al cambiar la empresa del medio. Si el tenant suma una empresa
>   después, un medio de todo el tenant que ya tenía cuenta la conserva: la regla se revisa al escribir, no hay trigger.
> - El turno y el medio de un movimiento pertenecen a la empresa de la cuenta, y el medio apunta a esa cuenta:
>   el escritor del cierre lo cumple (descarta una cuenta de otra empresa y lee la cuenta del medio bajo lock); el ajuste
>   manual no escribe turno ni medio; un escritor futuro (transferencias) deberá exigirlo por su cuenta.
> - La baja lógica de una cuenta solo se permite sin referencias (medios o movimientos): la baja lógica
>   esquiva RESTRICT.
> - Cambiar `comportamiento` de un medio con cuenta o porcentajes distintos de cero a algo que no es
>   `electronico` falla con 23514 si no se limpian: `ServicioDeMediosPago` los limpia (cuenta en `NULL`, porcentajes en 0)
>   cuando el cuerpo no los envía, y rechaza con 400 si los envía con valor.
> - Un `final` negativo es válido a propósito (no hay CHECK de saldo).
> - Ninguna CHECK liga `tipo` con un lado: un bruto negativo se escribe como `cobro` del lado `egreso`.
