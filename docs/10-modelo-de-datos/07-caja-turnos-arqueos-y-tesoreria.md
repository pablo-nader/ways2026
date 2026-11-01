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
    motivo text, id_empleado, creado_el,
    es_retiro_de_cierre boolean DEFAULT false,  -- el retiro que inserta el cierre por retiro
    deleted_at timestamptz NULL                 -- baja lógica administrativa de un retiro
);

arqueos_turno (               -- el cierre: contado vs esperado, POR MEDIO DE PAGO
    id_arqueo, id_turno_caja, id_medio_pago,
    importe_esperado numeric(14,2),          -- calculado: cobros + fondo − vueltos − pagos − retiros
    importe_declarado numeric(14,2),         -- lo que el cajero contó
    diferencia numeric(14,2),                -- esperado − declarado (el "saldo" del legacy, por medio)
    importe_esperado_original numeric(14,2) NULL  -- esperado del cierre, si un recálculo lo cambió
);
```

> **Corrección administrativa de retiros.** Los refuerzos y las aperturas de cajón son
> append-only; un retiro manual lo puede corregir un Admin (`PUT`/`DELETE /api/caja/retiros/{id}`)
> con el turno abierto o cerrado, mientras el cierre del turno no esté conciliado. La edición
> actualiza `importe` y `motivo` en el lugar (la auditoría `retiro.edicion` lleva los valores
> anteriores) y la baja es lógica (`deleted_at`, auditada como `retiro.baja`): todo lector de
> `movimientos_caja` excluye las bajas por el filtro de EF. El retiro del cierre por retiro
> (`es_retiro_de_cierre`) no se corrige. Con el turno cerrado se recalcula el arqueo como abajo y
> la cadena de `movimientos_tesoreria` recibe un `ajuste` por la diferencia, porque el cierre
> acreditó la suma de los retiros. Backfill (migración `EdicionDeRetiros`): se marcan los retiros
> con el motivo fijo del cierre y `creado_el` igual a la `fecha_cierre` del turno.

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
    tipo tipo_movimiento_tesoreria,          -- enum: retiro_caja | deposito | pago | ajuste | ajuste_manual
    id_turno_caja NULL, id_pago NULL, concepto,   -- ajuste = compensación del sistema (edición/baja de un pago); ajuste_manual = ajuste del Admin (POST /api/tesoreria/efectivo/ajustes)
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
> administración (ambos `tipo = 'pago'`, `egreso = importe`). La cadena vive a nivel empresa, nunca
> por punto de venta. Ya no tiene un reporte propio: el libro de tesorería
> (`ServicioDeLibroDeTesoreria`/`GET /api/reportes/libro-tesoreria`) se calcula desde las fuentes y de
> esta tabla solo lee los `ajuste_manual`.

### Cuentas de tesorería

```sql
cuentas_tesoreria (           -- [catálogo por empresa: id_empresa NOT NULL] — Mercado Pago, banco
    id_cuenta_tesoreria, id_empresa,
    nombre citext,                           -- único por (id_tenant, id_empresa) entre las vivas
    saldo_inicial numeric(14,2) NOT NULL DEFAULT 0,
    activo, created_at, updated_at, deleted_at
);

movimientos_cuenta (          -- [operativa — libro encadenado por cuenta, ordenado por (fecha, id)]
    id_movimiento_cuenta, id_cuenta_tesoreria, fecha,
    tipo tipo_movimiento_cuenta,             -- enum: cobro | comision | retencion_iibb | retencion_iva |
                                             -- retencion_ganancias | ajuste | transferencia | deposito | retiro | pago
    id_turno_caja NULL, id_medio_pago NULL,
    id_pago NULL,                            -- solo y siempre en las filas tipo pago (débito de un pago)
    concepto,
    inicio, ingreso, egreso, final,          -- encadenado inicio→final por (id_tenant, id_cuenta_tesoreria)
    id_empleado,
    base_bruta numeric(14,2) NULL,           -- base sobre la que se calculó el porcentaje, congelada
    porcentaje_aplicado numeric(6,3) NULL    -- porcentaje vigente al escribir la fila, congelado
);
```

> **Estado (cuentas de tesorería, esquema):** la tesorería de efectivo (`movimientos_tesoreria`,
> una cadena por empresa) y las cuentas digitales (`movimientos_cuenta`, una cadena por cuenta) son
> libros separados: cada uno con su propia cadena y su propio candado. Hay tres escritores: el cierre de turno, el ajuste manual
> de saldo y los débitos de pago (ver "Quién escribe el libro"); el ABM de cuentas solo lee.
>
> `cuentas_tesoreria`: clave alterna `ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant` (destino de
> las FK compuestas), FK compuesta `fk_cuentas_tesoreria_empresa` a `empresas` (RESTRICT), único
> parcial `ux_cuentas_tesoreria_nombre_empresa` `(id_tenant, id_empresa, nombre) WHERE deleted_at IS
> NULL`, índices `ix_cuentas_tesoreria_tenant` e `ix_cuentas_tesoreria_empresa`. RLS estándar.
>
> **Orden de la cadena:** `(fecha, id)`. El cierre y el ajuste agregan al final (su fecha nunca es anterior a la cola), pero la
> reconstrucción del historial (`POST /api/cuentas-tesoreria/reconstruccion`) inserta filas con la fecha de cierre de turnos viejos
> y reescribe `inicio`/`final` de las filas posteriores de la cuenta; `ingreso` y `egreso` de cada fila no cambian, así que un ajuste
> manual conserva su diferencia pero no el saldo objetivo que lo originó.
>
> `movimientos_cuenta`: `ck_movimientos_cuenta_cadena` (`final = inicio + ingreso − egreso`, igual
> que la tesorería de efectivo), más dos CHECK que `movimientos_tesoreria` no tiene:
> `ck_movimientos_cuenta_importes_no_negativos` (`ingreso` y `egreso` no negativos) y
> `ck_movimientos_cuenta_un_solo_lado` (`ingreso = 0 OR egreso = 0`; una fila con los dos lados en
> cero es válida), y `ck_movimientos_cuenta_pago` (`(tipo = 'pago') = (id_pago IS NOT NULL)`, en una migración posterior a la que
> agrega el valor del enum). FKs compuestas, todas RESTRICT: `fk_movimientos_cuenta_cuenta_tesoreria`,
> `fk_movimientos_cuenta_turno`, `fk_movimientos_cuenta_medio_pago`, `fk_movimientos_cuenta_pago` (a `pagos`); `fk_movimientos_cuenta_empleado`
> es simple a `usuarios`, como en la tesorería de efectivo. Índices (ninguno implícito `IX_`): `ix_movimientos_cuenta_tenant`;
> `ix_movimientos_cuenta_cuenta_fecha_id` `(id_cuenta_tesoreria, id_tenant, fecha, id_movimiento_cuenta)`, que
> sirve de soporte de la FK a la cuenta y de lectura de la cola de la cadena y del libro, que se ordenan por `(fecha, id)`;
> `ix_movimientos_cuenta_turno`, `ix_movimientos_cuenta_medio_pago`, `ix_movimientos_cuenta_pago` `(id_pago, id_tenant)` e
> `ix_movimientos_cuenta_empleado` como soporte de las otras FK. `ux_movimientos_cuenta_cierre` es único parcial `(id_tenant,
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
> **Quién escribe el libro.** Tres escritores: el cierre de turno, el ajuste manual y los débitos de pago (más abajo).
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
> **Débitos de pago.** `DebitosDePagoEnCuenta`, al final de la transacción de todo escritor de ítems de pago (alta del POS, alta
> administrativa, pago de compra, pago de sueldo, edición y baja), antes del commit. Objetivo por `(cuenta, medio)`: la suma de los
> ítems vivos del pago, ruteados por la cuenta ACTUAL del medio, solo si la cuenta está viva y es de la empresa del pago; ya debitado:
> la suma de las filas `pago` de ese `id_pago` (egreso − ingreso). Cada diferencia distinta de cero escribe una fila
> (`CalculadorDeDebitosDePago`, puro): `egreso` si sube, `ingreso` si baja, con `id_pago`, `id_medio_pago`, `id_turno_caja` nulo,
> `concepto` `Pago {id}`, `id_empleado` = quien escribe y `fecha` monótona sobre la hora real (nunca la fecha de negocio del pago).
> No hay índice único: una edición agrega filas. Una baja devuelve todo; un medio sin cuenta, efectivo o con una cuenta de otra
> empresa no escribe nada. Locks: advisory de la empresa (reentrante con el de la tesorería de efectivo) → medios `FOR SHARE` →
> cuentas `FOR NO KEY UPDATE`, el mismo orden que el cierre, siempre después del lock del pago; las filas existentes se leen
> después del advisory y la cola después del lock de la cuenta. Sin medios con cuenta ni débitos previos no se toma ningún lock
> (decidido con una lectura sin lock: un medio al que se le asigna cuenta en ese instante deja al pago como anterior a la
> asignación). El libro de tesorería no lee estas filas (las calcula desde los pagos), así que no hay doble conteo.
>
> **Lectura.** La cadena de cada cuenta ya no tiene un reporte propio. El libro de tesorería
> (`ServicioDeLibroDeTesoreria`/`GET /api/reportes/libro-tesoreria`, Supervisor y Admin) lee de esta tabla los cobros, las
> comisiones y las retenciones de cada cierre, agrupados por turno, y los `ajuste` manuales. Su saldo se calcula desde las fuentes,
> no desde `final`.
>
> **Lo que el libro todavía NO refleja.** (a) Una devolución por nota de crédito de una venta cobrada con un medio digital no
> descuenta de la cuenta, que queda por encima de lo real en el importe devuelto (neto de comisión y retenciones) hasta un
> ajuste manual (ver "Ajuste manual"). (b) Los pagos anteriores a los débitos de pago no debitaron la cuenta hasta que la
> reconstrucción los cargue. Hoy una devolución no mueve dinero por ningún medio: `ValidadorDePagos` (regla 0c) rechaza con 400
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

### Conciliación de la tesorería

```sql
conciliaciones_tesoreria (    -- [operativa — marcas append-only] una fila por conciliación de una empresa
    id_conciliacion_tesoreria, id_empresa,
    hasta timestamptz NOT NULL,              -- instante hasta el cual la tesorería está conciliada, inclusive
    id_usuario,                              -- el Admin que concilió
    creado_el timestamptz NOT NULL,
    nota text NULL                           -- hasta 500 caracteres, la valida el servicio
);
```

> **Estado (libro de tesorería, T5a).** Un Admin marca que la tesorería de una empresa está revisada hasta un instante; la marca
> vigente de la empresa es el mayor `hasta`. Todo lo que alimenta el libro de tesorería con fecha menor o igual (inclusive) queda
> congelado y se corrige con movimientos nuevos (ajustes manuales, pagos nuevos). Las marcas no se editan ni se borran. Tabla de
> tenant sin baja lógica (como `movimientos_tesoreria`), RLS estándar, FK compuesta `fk_conciliaciones_tesoreria_empresa` y simples
> `fk_conciliaciones_tesoreria_tenant` y `fk_conciliaciones_tesoreria_usuario`, todas RESTRICT. Índices:
> `ix_conciliaciones_tesoreria_tenant`, `ix_conciliaciones_tesoreria_empresa_hasta` `(id_empresa, id_tenant, hasta)` (soporte de la FK y
> lectura del máximo) e `ix_conciliaciones_tesoreria_usuario`. Sin índice único: la monotonía la sostiene el lock del escritor, y
> ninguna FK recibe un id del cliente sin validar (la empresa se resuelve con 404 antes de escribir).
>
> **Endpoint (`POST /api/tesoreria/conciliaciones`, solo Admin).** Cuerpo `{ idEmpresa, hasta, nota? }`, responde 201. Orden: 404 de la
> empresa, 400 del cuerpo (`hasta_requerido`, `nota_muy_larga`) y 409: `conciliacion_futura` (`hasta` posterior a la hora del
> servidor), `conciliacion_anterior` (`hasta` menor o igual a la marca vigente) y `conciliacion_con_turno_abierto` (algún turno
> abierto de la empresa con `fecha_apertura <= hasta`). `GET /api/reportes/libro-tesoreria` trae `conciliadoHasta` (la marca vigente,
> sin importar el rango pedido; `null` si nunca se concilió).
>
> **Qué se congela.** Solo lo que cambia los números del libro: la edición y la baja de un pago (POS o administración) con
> `fecha` menor o igual a la marca, o cuyo turno cerró en o antes de ella (409 `periodo_conciliado`); el alta de un pago con una fecha
> menor o igual (el pago de administración con fecha pasada, el pago de una compra, el pago de personal con origen tesorería, y
> cualquier alta del POS que pierda la carrera contra la marca); el cierre de un turno fechado en o antes de la marca (solo por
> carrera: la marca no se puede poner con un turno abierto que empezó antes); y el cambio de cuenta o de comportamiento de un medio
> de pago que tiene líneas de pago o movimientos de cuenta fechados en o antes de la marca de SU empresa; el saldo inicial de una cuenta de
> tesorería (409 `periodo_conciliado` apenas la empresa tiene alguna marca, porque el libro lo suma a todos los saldos; nombre y estado siguen
> editables); y los ajustes manuales de efectivo y de cuenta, que van fechados "ahora" y solo pueden chocar si la marca se confirma entre su
> reloj y su commit. Siguen editables porque no
> cambian lo que el libro lee: compras (edición y anulación), aplicaciones y desaplicaciones, redondeos, vincular compra, reasignar
> personal, cobros y ventas. El pago de administración fechado hoy (en la zona de la empresa) toma la hora actual como `fecha` y no la
> medianoche local: así queda siempre posterior a una marca puesta antes durante el día; las fechas pasadas conservan la medianoche local.
>
> **Locks.** `GuardaDeConciliacion`: lock advisory de transacción `pg_advisory_xact_lock(bigint)` sobre una clave de `(tenant, empresa)`
> (bit alto encendido, distinta del advisory de tesorería y del de familias). El escritor de la marca lo toma EXCLUSIVO y no toma ningún
> otro; todo escritor protegido lo toma COMPARTIDO como PRIMER lock de su transacción (antes de turno, compra, pago, proveedor y del
> advisory de tesorería), lee la marca y recién ahí decide. El orden "primero" es obligatorio porque PostgreSQL encola un pedido
> compartido detrás de un exclusivo que espera: quien pidiera la marca reteniendo otro lock podría cerrar un ciclo con el escritor de la
> marca. Los escritores cuya empresa no se conoce antes de los locks (pago de una compra, cierre de turno, edición de un pago) la
> pre-leen sin lock (la empresa de un pago y el punto de venta de un turno no cambian; la de una compra se verifica de nuevo bajo su lock). La edición de un medio de pago toma el compartido de TODAS las empresas del tenant, en id
> ascendente. La edición de una cuenta de tesorería y los dos ajustes manuales lo toman como primer lock (la edición pre-lee la empresa de la
> cuenta, que no cambia). La reconstrucción de cuentas (`POST /api/cuentas-tesoreria/reconstruccion` y su vista previa) omite los turnos con
> `fecha_cierre <=` la marca de la empresa (igual a la marca = congelado) en lugar de rechazar el pedido: la respuesta trae
> `conciliadoHasta` y `congeladas` (las acreditaciones pendientes omitidas, sin contarlas en `turnos` ni `filas`). La aplicación toma el
> compartido como PRIMER lock, antes del advisory de tesorería y de los locks de medios y cuentas, y lee la marca una sola vez bajo él.

### Conexiones con Mercado Pago

```sql
conexiones_mercadopago (      -- [operativa por empresa: id_tenant + id_empresa NOT NULL] una por vez activa
    id_conexion_mercadopago, id_tenant, id_empresa,
    id_cuenta_tesoreria,                     -- cuenta de la MISMA empresa que representa la cuenta de Mercado Pago
    id_usuario_mercadopago bigint NOT NULL,  -- user_id del vendedor, devuelto al canjear el código
    access_token_cifrado bytea NULL, access_token_nonce bytea NULL, access_token_tag bytea NULL,
    refresh_token_cifrado bytea NULL, refresh_token_nonce bytea NULL, refresh_token_tag bytea NULL,
    id_clave_maestra varchar(30) NOT NULL,   -- versión de la clave maestra de Ways:MercadoPago que los cifró
    access_token_expira_en timestamptz NOT NULL,
    estado text NOT NULL,                    -- 'activa' | 'desconectada' | 'requiere_reconexion' (texto con CHECK, sin enum nativo)
    id_usuario_conecto,                      -- usuario de Ways que inició la conexión
    fecha_conexion timestamptz NOT NULL, fecha_desconexion NULL, fecha_ultima_renovacion NULL,
    id_usuario_desconecto int NULL,          -- usuario de Ways que la desconectó (NULL mientras no se desconecta y en las anteriores a este campo)
    fecha_requiere_reconexion timestamptz NULL,  -- cuándo la renovación automática la marcó 'requiere_reconexion'
    fecha_ultimo_intento_renovacion timestamptz NULL,  -- último intento de renovar el token que falló (backoff de la renovación previa al uso)
    created_at, updated_at, deleted_at
);
```

> **Estado (conexión OAuth, etapa E1):** una empresa conecta su cuenta de Mercado Pago con OAuth; Ways guarda los dos tokens
> cifrados con AES-256-GCM (nonce de 12 bytes y tag de 16 por token) y nunca los devuelve ni los registra. La clave maestra sale de
> `Ways:MercadoPago:ClaveMaestraActual` y `Ways:MercadoPago:ClavesMaestras:<id>` (32 bytes en base64), propias y distintas de las
> fiscales. El AAD de cada blob es `v1|id_tenant|id_empresa|id_conexion|access` o `...|refresh`: un blob movido a otra conexión,
> empresa, tenant o al otro token falla la autenticación. `id_clave_maestra` queda fuera del AAD, como en `certificados_fiscales`.
> El id de la conexión se reserva de la secuencia (`nextval`) antes de cifrar, para que el AAD lo incluya.
>
> Constraints: FK `fk_conexiones_mercadopago_tenant` (simple), `fk_conexiones_mercadopago_empresa` (compuesta
> `(id_empresa, id_tenant)`), `fk_conexiones_mercadopago_cuenta_tesoreria` (compuesta `(id_cuenta_tesoreria, id_empresa,
> id_tenant)` contra la clave alterna nueva `ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant`: la base rechaza una
> cuenta de otra empresa) y `fk_conexiones_mercadopago_usuario_conecto` y `fk_conexiones_mercadopago_usuario_desconecto` (simples a `usuarios`, la segunda nullable); todas RESTRICT. CHECK
> `ck_conexiones_mercadopago_estado` (`activa`, `desconectada` o `requiere_reconexion`), `ck_conexiones_mercadopago_activa` (activa implica los seis
> campos de cifrado presentes con nonce de 12 bytes, tag de 16 y blob no vacío, y `fecha_desconexion` y `fecha_requiere_reconexion` nulas),
> `ck_conexiones_mercadopago_desconectada` (desconectada implica `fecha_desconexion` y los seis campos en `NULL`) y
> `ck_conexiones_mercadopago_requiere_reconexion` (implica `fecha_requiere_reconexion`, `fecha_desconexion` nula y los seis campos en `NULL`). Únicos
> parciales, ambos `WHERE estado = 'activa'`: `ux_conexiones_mercadopago_empresa_activa` `(id_tenant, id_empresa)` y
> `ux_conexiones_mercadopago_usuario_activa` `(id_usuario_mercadopago)`, SIN `id_tenant`: una cuenta de Mercado Pago está activa en
> una sola empresa de todo el sistema (los webhooks resuelven la empresa desde ese id). `ManejadorDeErrores` traduce sus 23505 a
> 409 `mercadopago_ya_conectada` y `mercadopago_cuenta_en_uso`; el endpoint autenticado `.../completar` los devuelve como tales. Índices de
> soporte: `ix_conexiones_mercadopago_tenant`, `_empresa`, `_cuenta`, `_usuario_conecto` y `_usuario_desconecto`. Se puede desconectar una conexión `activa` o una que `requiere_reconexion` (pasa a `desconectada`, conserva `fecha_requiere_reconexion` como historia y registra `id_usuario_desconecto`). El `Down()` de `RenovacionDeConexionesMercadoPago` convierte las `requiere_reconexion` en `desconectada` (con su fecha de marca como `fecha_desconexion`) antes de restaurar el CHECK de dos valores. RLS estándar.
>
> **Efecto sobre `cuentas_tesoreria`:** la baja lógica de una cuenta con cualquier conexión (activa o desconectada) falla con 409
> `cuenta_tesoreria_en_uso`, porque la guarda de referencias recorre las FK del modelo. El inventario de dependientes de las bajas de
> empresa, tenant y usuario también cuenta estas filas como uso (`inventario-de-dependientes.txt`).
>
> **Endpoints (solo Admin salvo el callback).** `POST /api/empresas/{idEmpresa}/mercadopago/conexion/inicio` con `{ idCuentaTesoreria }`
> devuelve `{ urlAutorizacion }` (PKCE S256, `state` protegido con vencimiento de 10 minutos que lleva tenant, empresa, cuenta,
> usuario, verificador y el origen del navegador). Orden: 404 de la empresa (inexistente, ajena o dada de baja), 503
> `mercadopago_no_configurado` si faltan `ClientId`, `ClientSecret`, `RedirectUri` o la clave maestra, 400 `referencia_invalida`
> (cuenta inexistente o de otro tenant), 409 `cuenta_tesoreria_de_otra_empresa`, 409 `cuenta_tesoreria_inactiva` y 409
> `mercadopago_ya_conectada`. `GET /api/integraciones/mercadopago/callback?code=&state=` es anónimo y **no canjea nada**: lee el
> `state` solo para saber el origen y la empresa, y redirige a `{origen}/organizacion/empresas#mercadopago=completar&empresa=<id>&code=<code>&state=<state>`
> (o `#mercadopago=cancelado&empresa=<id>` si el usuario denegó). Todo va en el **fragmento**, nunca en la consulta: el navegador no lo
> manda al servidor ni en el `Referer`, así que el `code` no llega a ningún log. Un `state` inválido o vencido, o un retorno sin `code` ni
> `error`, da 400 de texto plano. El canje lo hace `POST /api/empresas/{idEmpresa}/mercadopago/conexion/completar` con `{ code, state }`
> (Admin; ambos campos obligatorios y acotados a 512 y 2048 caracteres; cualquier otro campo se ignora por el binding, no se guarda):
> 404 de la empresa, 400 `solicitud_invalida` (cuerpo), 400 `mercadopago_estado_invalido` (alterado o vencido), 403
> `mercadopago_estado_ajeno` si la empresa o el usuario del `state` no son los de la ruta y la sesión (la empresa ya se resolvió dentro del tenant de la sesión, así que ata el tenant) (no se llama a Mercado Pago),
> 503 `mercadopago_no_configurado`, y recién entonces el canje: 200 con el estado de la conexión, 409 `mercadopago_ya_conectada`, 409
> `mercadopago_cuenta_en_uso` o 502 `mercadopago_intercambio_fallido`. Cierra el login-CSRF: un enlace de autorización reenviado a otra
> persona vuelve a una sesión que no es la que inició el flujo y se rechaza antes de canjear.
> `GET /api/empresas/{idEmpresa}/mercadopago/conexion` devuelve `{ conectada, estado, idCuentaTesoreria, nombreCuenta,
> idUsuarioMercadoPago, fechaConexion, accessTokenExpiraEn }` o `{ conectada: false }`. Sin activa, si la última conexión de la empresa quedó
> en `requiere_reconexion` responde `{ conectada: false, estado: "requiere_reconexion", idCuentaTesoreria, nombreCuenta,
> idUsuarioMercadoPago, fechaConexion }` (sin vencimiento) para que la pantalla ofrezca reconectar; una desconexión posterior vuelve al
> estado vacío. `DELETE` del mismo recurso pasa la conexión a
> `desconectada` y borra los seis campos de cifrado en un solo `UPDATE` guardado por estado (404 si no hay una activa); no llama a
> Mercado Pago porque la API de OAuth no documenta una revocación.
>
> **Renovación automática (etapa E1, T2).** `RenovacionDeConexionesMercadoPago` (BackgroundService, primera corrida al minuto del
> arranque y después cada `Ways:MercadoPago:IntervaloRenovacionHoras`, 6 por defecto) llama a
> `ServicioDeRenovacionDeConexionesMercadoPago`, que toma, en todos los tenants, las conexiones `activa` cuyo
> `access_token_expira_en` cae dentro de `Ways:MercadoPago:DiasDeAnticipacionRenovacion` (30 por defecto) y renueva cada una en su
> propia transacción con el contexto de su tenant: `FOR UPDATE`, una sola lectura después del lock (se omite si ya no está activa o ya
> no le toca), llamada al endpoint de tokens con la fila bloqueada y, con la respuesta, ambos tokens se vuelven a cifrar con nonces
> nuevos (AAD sin cambios) y se mueven el vencimiento y `fecha_ultima_renovacion`. Un 4xx de Mercado Pago, o un `user_id` distinto
> del guardado, pasa la conexión a `requiere_reconexion` y borra el material cifrado; un 5xx, un 408/429, un tiempo agotado o un fallo
> de red no toca nada y se reintenta en la próxima corrida. `requiere_reconexion` no es `activa`: no ocupa los índices únicos
> parciales y la empresa puede volver a conectar (el inicio solo bloquea ante una `activa`); la fila queda como historia. Sin
> `ClientId`/`ClientSecret`/`RedirectUri`, o con `Ways:MercadoPago:RenovacionAutomatica` en `false`, el job se registra una vez y no hace nada.
>
> **Renovación previa al uso (etapa E2, T1).** Antes de cada llamada a la API de Mercado Pago, un token al que le quedan
> `Ways:MercadoPago:HorasDeMargenDelToken` (24) horas o menos se renueva primero. Esa renovación **no espera el lock**: toma la fila con
> `FOR UPDATE SKIP LOCKED` y, si otra renovación la tiene, se usa el token guardado cuando todavía le queda más de un minuto. Un intento que
> falla (falla de Mercado Pago o de red, credenciales de la aplicación rechazadas, material ilegible o rotación perdida) anota
> `fecha_ultimo_intento_renovacion`; mientras el token guardado siga siendo utilizable, las llamadas siguientes no vuelven a intentarlo
> antes de `Ways:MercadoPago:MinutosDeBackoffDeRenovacion` (15; releído bajo el lock). Un token que ya no es utilizable ignora el backoff y
> reintenta, y si no logra renovarlo responde 503 `mercadopago_no_disponible`. La corrida en segundo plano no respeta el backoff, pero sí lo
> anota cuando falla. Una excepción inesperada de la renovación previa al uso se registra (sin tokens) y no corta la llamada si el token
> guardado todavía sirve.

### Locales de Mercado Pago (sucursales)

```sql
locales_mercadopago (         -- [operativa por empresa: id_tenant + id_empresa NOT NULL] una por sucursal y cuenta de Mercado Pago
    id_local_mercadopago, id_tenant, id_empresa,
    id_sucursal,                             -- FK compuesta (id_sucursal, id_empresa, id_tenant): la base rechaza una sucursal de otra empresa
    id_usuario_mercadopago bigint,           -- user_id del vendedor dueño del local; NO es FK (la conexión puede desconectarse y la fila queda de historia)
    id_store_mercadopago bigint,             -- id numérico que Mercado Pago asignó al local
    external_id varchar(60),                 -- 'WAYSS' + id_sucursal: alfanumérico, estable, único por vendedor
    fecha_registro, fecha_sincronizacion,    -- esta última es la de la LECTURA de la sucursal, no la del fin de la llamada
    created_at, updated_at, deleted_at
)
```

> Constraints: FK `fk_locales_mercadopago_tenant` (simple), `fk_locales_mercadopago_empresa` (compuesta `(id_empresa, id_tenant)`) y
> `fk_locales_mercadopago_sucursal` (compuesta `(id_sucursal, id_empresa, id_tenant)`, sobre la clave alterna de `sucursales`); todas RESTRICT.
> CHECK `ck_locales_mercadopago_external_id` (no vacío). Único `ux_locales_mercadopago_sucursal_usuario` `(id_sucursal, id_usuario_mercadopago)`:
> una fila por sucursal y cuenta; si la empresa se reconecta con otra cuenta, la sucursal se registra de nuevo y la fila anterior queda de
> historia. `ManejadorDeErrores` traduce su 23505 a 409 `mercadopago_local_duplicado` (solo lo alcanza una escritura que no pase por el
> servicio, que serializa con un lock por sucursal). Índices de soporte: `ix_locales_mercadopago_tenant`, `_empresa` y `_sucursal`. RLS
> estándar. Efecto: como toda FK del modelo, una sucursal con un local registrado cuenta como en uso y no se da de baja (409
> `sucursal_en_uso`), y lo mismo la empresa y el tenant (`inventario-de-dependientes.txt`).
>
> **Endpoints (solo Admin).** `GET /api/sucursales/{id}/mercadopago` devuelve `{ estado, idStoreMercadoPago, externalId, fechaRegistro,
> fechaSincronizacion, camposFaltantes }` con `estado` en `sin_conexion` (la empresa no tiene una conexión activa), `no_registrada`,
> `registrada` o `desactualizada` (la sucursal se editó después de `fecha_sincronizacion`), para la cuenta conectada hoy.
> `camposFaltantes` lista, en orden, lo que la sucursal todavía no tiene para registrarse (`nombre`, `calle`, `altura`, `ciudad`,
> `provincia`, `latitud`, `longitud`; el código postal y la referencia no son requisito). `POST` del mismo recurso la registra. Orden:
> 404 de la sucursal (inexistente, ajena o dada de baja), 400 `direccion_incompleta_para_mercadopago` (nombra lo que falta), 409
> `mercadopago_no_conectada` o `mercadopago_requiere_reconexion`. Bajo un `pg_advisory_xact_lock` por sucursal que se mantiene durante las
> llamadas a Mercado Pago: con vínculo para la cuenta actual, `PUT /users/{user_id}/stores/{id}` con los datos actuales; sin vínculo,
> primero `GET /users/{user_id}/stores/search?external_id=` (sin coincidencias Mercado Pago contesta 404 `store_not_found`, no una lista vacía: el 404 se trata como "no hay") y, si hay un local con ese `external_id` (un alta cuyo guardado se perdió), se
> adopta y se actualiza; si no, `POST /users/{user_id}/stores`. Toda llamada que no es GET lleva `X-Idempotency-Key`. Después se guarda o
> actualiza la fila y se registra `sucursal.mercadopago` (operación `alta`, `adopcion` o `actualizacion`, ids de Mercado Pago, nunca un
> token). El texto que viaja a Mercado Pago se limpia (sin acentos; solo letras ASCII, dígitos, espacio y `. , -`) y se corta a su largo; un
> campo que queda vacío cuenta como faltante. El rechazo de Mercado Pago sale como 502 `mercadopago_solicitud_rechazada` con su mensaje
> saneado, un 401 deja la conexión en `requiere_reconexion` (409) y la caída o el tiempo agotado son 503 `mercadopago_no_disponible`.
> Verificado contra la referencia (2026-11-01), no en vivo: la búsqueda `{paging, results}` y su 404 sin coincidencias, y que la actualización es `PUT`.
> **Sin verificar contra la API real:** qué caracteres acepta cada campo, el nombre de provincia que espera Mercado Pago para la Ciudad Autónoma de
> Buenos Aires y qué responde un `PUT` a un local borrado en Mercado Pago.

### Cajas de Mercado Pago (puntos de venta)

```sql
cajas_mercadopago (           -- [operativa por empresa: id_tenant + id_empresa NOT NULL] una por punto de venta y cuenta de Mercado Pago
    id_caja_mercadopago, id_tenant, id_empresa,
    id_punto_venta,                          -- FK compuesta (id_punto_venta, id_tenant) sobre la clave alterna de puntos_venta; la empresa la fija el servicio al leer el punto de venta
    id_local_mercadopago,                    -- FK compuesta (id_local_mercadopago, id_empresa, id_tenant) a locales_mercadopago: el local del que cuelga la caja
    id_store_mercadopago bigint,             -- id del local (store) de Mercado Pago bajo el que se registró la caja; si el local de la sucursal cambia de id la fila queda desactualizada
    id_usuario_mercadopago bigint,           -- user_id del vendedor dueño de la caja; NO es FK (la conexión puede desconectarse y la fila queda de historia)
    id_pos_mercadopago bigint,               -- id numérico que Mercado Pago asignó a la caja (POS)
    external_id varchar(40),                 -- 'WAYSPV' + id_punto_venta: letras y dígitos, estable, único por vendedor
    url_qr varchar(500) NULL,                -- imagen del QR (qr_response.image), si Mercado Pago la devolvió
    fecha_registro, fecha_sincronizacion,    -- esta última es la de la LECTURA del punto de venta, no la del fin de la llamada
    id_terminal_point varchar(80) NULL,      -- terminal Point elegida para la caja (TIPO__SERIE, p. ej. PAX_A910__8701); se vincula en la app de Mercado Pago
    fecha_terminal NULL,                     -- cuándo se asignó; nula si y solo si no hay terminal
    created_at, updated_at, deleted_at
)
```

> Constraints: FK `fk_cajas_mercadopago_tenant` (simple), `fk_cajas_mercadopago_empresa` (compuesta `(id_empresa, id_tenant)`),
> `fk_cajas_mercadopago_punto_venta` (compuesta `(id_punto_venta, id_tenant)`, sobre la clave alterna existente) y `fk_cajas_mercadopago_local`; todas RESTRICT. Para la última se agregó la clave alterna
> `ak_locales_mercadopago_id_local_id_empresa_id_tenant`
> (sobre `locales_mercadopago`; no se agregó ninguna a `puntos_venta`: una clave alterna con la empresa obliga a toda alta de punto de venta a conocerla antes del `SaveChanges`). CHECK `ck_cajas_mercadopago_external_id` (no vacío) y `ck_cajas_mercadopago_terminal` (`id_terminal_point` y `fecha_terminal` van juntos o ninguno, y el id no es blanco; el servicio valida antes, es solo respaldo). Único `ux_cajas_mercadopago_punto_venta_usuario`
> `(id_punto_venta, id_usuario_mercadopago)`: una fila por punto de venta y cuenta; si la empresa se reconecta con otra cuenta el punto de
> venta se registra de nuevo y la fila anterior queda de historia. `ManejadorDeErrores` traduce su 23505 a 409 `mercadopago_caja_duplicada`
> (solo lo alcanza una escritura que no pase por el servicio, que serializa con un lock por punto de venta). Índices de soporte:
> `ix_cajas_mercadopago_tenant`, `_empresa`, `_punto_venta` y `_local`. RLS estándar. Efecto: como toda FK del modelo, un punto de venta
> con una caja registrada cuenta como en uso y no se da de baja (409 `punto_venta_en_uso`), y lo mismo la empresa y el tenant
> (`inventario-de-dependientes.txt`). **Mover el punto de venta a otra sucursal no se bloquea** (la FK no pasa por la sucursal): lo deja
> `desactualizada` y registrarlo de nuevo lo reapunta. Por eso, al mover la última caja de una sucursal, el local de Mercado Pago de esa
> sucursal no cuenta como uso que impida la baja en cascada de la sucursal (queda como historia, igual que sus parámetros).
>
> **Endpoints (solo Admin).** `GET /api/puntos-venta/{id}/mercadopago` devuelve `{ estado, idPosMercadoPago, idStoreMercadoPago, externalId,
> urlQr, fechaRegistro, fechaSincronizacion }` con `estado` en `sin_conexion`, `local_no_registrado` (la sucursal actual del punto de venta
> todavía no es un local de la cuenta), `no_registrada`, `registrada` o `desactualizada` (el punto de venta se editó o cambió de sucursal
> después de `fecha_sincronizacion`, o el local de su sucursal es otro que el de la fila o se volvió a registrar con otro `id` de Mercado Pago). `POST` del mismo recurso la registra. Orden: 404 del
> punto de venta, 400 `nombre_invalido_para_mercadopago` (el nombre no tiene ningún carácter admitido), 409 `mercadopago_no_conectada` o
> `mercadopago_requiere_reconexion`, 409 `mercadopago_local_no_registrado`. Bajo dos `pg_advisory_xact_lock` que se mantienen
> durante las llamadas a Mercado Pago, en orden fijo sucursal → punto de venta (el de la sucursal es el mismo de la registración del local, que toma solo ese: así el local no cambia de `id` a mitad de la llamada; si el punto de venta cambió de sucursal entre la lectura que elige el lock y la de después, 409 `mercadopago_punto_venta_movido`): con vínculo para la cuenta actual, `PATCH /v2/pos/{id}` con el nombre actual y el `store_id` del local de la
> sucursal actual (así se reapunta una caja movida); si Mercado Pago contesta 404 (la caja se borró), se busca por `external_id` y se adopta o se
> crea de nuevo. Sin vínculo, primero `GET /v2/pos?external_id=` (`{paging, data}`) y, si hay una caja con ese `external_id` (un alta cuyo guardado se perdió), se
> adopta y se actualiza; si no, `POST /v2/pos` con `{name, external_id, store_id (cadena de dígitos), config: {qr: {operating_mode: "pdv"}}}` (sin `fixed_amount`, que la API no admite; si contesta 409 `pos_already_exists` se busca por `external_id` y se adopta). Toda llamada que no es GET lleva
> `X-Idempotency-Key` (≤ 64): la del alta lleva la huella del cuerpo; la de la actualización, la caja de destino, la huella del cuerpo y el
> `updated_at` del punto de venta, así que reintentar el mismo intento repite la clave y A → B → A no. Después se guarda o actualiza la fila y se
> registra `pv.mercadopago` (operación `alta`, `adopcion` o `actualizacion`, ids de Mercado Pago, nunca un token). El nombre se limpia a letras ASCII, dígitos, espacio, guion y guion bajo (el punto y la coma pasan a espacio) y se corta a 45. El rechazo de Mercado Pago sale como 502 `mercadopago_solicitud_rechazada`, un 401 deja la conexión en
> `requiere_reconexion` (409) y la caída o el tiempo agotado son 503 `mercadopago_no_disponible`.
> Verificado contra la referencia (2026-11-01), no en vivo: `POST /v2/pos`, `PATCH /v2/pos/{id}` (el `store_id` puede cambiar), `GET /v2/pos?external_id=` con la clave `data`,
> la imagen del QR en `qr_response.image` y el 409 `pos_already_exists`. **Sin verificar contra la API real:** qué responde un `PATCH` a una caja borrada (se asume 404 `pos_not_found`).
>
> **Terminal Point.** `GET /api/puntos-venta/{id}/mercadopago` también devuelve `idTerminalPoint`. `GET .../mercadopago/terminales` lista las
> terminales que Mercado Pago tiene vinculadas a la caja (`GET /terminals/v1/list?pos_id=`; solo Newland N950 y PAX A910) como `{ id, modoOperativo,
> asignada }`, y con la lista vacía trae `instrucciones`: la terminal se vincula **solo en la app móvil de Mercado Pago** (se enciende, se escanea el
> QR de su pantalla y se elige local y caja); una caja admite una terminal. `POST .../mercadopago/terminal` `{ idTerminal }` (obligatorio, `^[A-Z0-9]+__[A-Za-z0-9]+$`,
> ≤ 80; 400 `terminal_point_invalida`) asigna una terminal del listado de la caja (si no figura, 409 `mercadopago_terminal_no_vinculada`) bajo los mismos dos
> locks sucursal → punto de venta; si no está en `PDV`, `PATCH /terminals/v1/setup` `{terminals:[{id, operating_mode:"PDV"}]}` con
> `X-Idempotency-Key` (huella de terminal y modo; Mercado Pago pide reiniciar la terminal): 403 `terminal_not_allowed_action` es 409
> `mercadopago_terminal_modelo_no_admitido`, 403 `store_pos_not_found` es 409 `mercadopago_terminal_no_vinculada` y 412 (la caja ya tiene otra terminal
> en `PDV` o suspendida) es 409 `mercadopago_caja_con_terminal_en_pdv`. Con la terminal ya en `PDV` no se manda el `PATCH`. Registra
> `pv.terminalmercadopago` (anterior y nueva terminal, si cambió el modo; sin tokens). `DELETE .../mercadopago/terminal` borra la asignación en Ways y no toca Mercado Pago. Sin caja
> registrada, las tres rutas responden 409 `mercadopago_caja_no_registrada`. Verificado contra la referencia (2026-11-01), no en vivo.

### Cobros con Mercado Pago (dispositivo del POS)

```sql
cobros_mercadopago (          -- [operativa por empresa: id_tenant + id_empresa NOT NULL] una fila por orden pedida a Mercado Pago
    id_cobro_mercadopago, id_tenant, id_empresa,
    id_punto_venta,                          -- FK compuesta (id_punto_venta, id_tenant); el del dispositivo que cobra, nunca el del cuerpo
    id_dispositivo,                          -- FK compuesta (id_dispositivo, id_tenant) a dispositivos (clave alterna existente)
    id_medio_pago,                           -- FK compuesta (id_medio_pago, id_tenant) a medios_pago (proveedor 'mercadopago_qr')
    numero_preasignado bigint,               -- el número de venta que el dispositivo reservó (SolicitudDeVenta.NumeroPreasignado)
    intento integer,                         -- 1, y +1 por cada cobro nuevo del mismo número después de uno cancelado o vencido
    tipo varchar(10),                        -- 'qr' | 'point' (CHECK ck_cobros_mercadopago_tipo)
    importe numeric(18,2),                   -- > 0; lo que se pidió cobrar
    external_reference varchar(64),          -- 'W{tenant}P{punto de venta}N{número}I{intento}'; vuelve en los informes de Mercado Pago
    external_pos_id varchar(40),             -- external_id de la caja con el que se pidió la orden; un reintento reenvía la misma orden aunque la caja cambie
    clave_idempotencia varchar(64),          -- X-Idempotency-Key de POST /v1/orders: 'ways-cobro-' + huella SHA-256 del cuerpo completo de la orden
    id_orden_mercadopago varchar(64) NULL,   -- id de la orden (ORD…); nulo solo en 'creando' o 'expirado' sin orden (CHECK ck_cobros_mercadopago_orden)
    estado varchar(20),                      -- 'creando' | 'pendiente' | 'aprobado' | 'cancelado' | 'expirado' | 'devuelto'
    id_pago_mercadopago varchar(64) NULL,    -- id del pago dentro de la orden; obligatorio si el cobro está aprobado (CHECK ck_cobros_mercadopago_aprobado_con_pago)
    referencia_pago_mercadopago bigint NULL, -- reference_id numérico del pago (para /v1/payments y los informes)
    importe_pagado numeric(18,2) NULL,       -- lo que Mercado Pago dice que se pagó; puede diferir de importe
    qr_data text NULL,                       -- contenido EMVCo del QR dinámico; solo se devuelve mientras el cobro está pendiente
    expira_en,                               -- reserva de la fila + 10 minutos (expiration_time PT10M)
    fecha_ultima_consulta NULL,              -- última lectura de la orden; limita el refresco a una cada 2 segundos por cobro
    fecha_aprobacion NULL,
    fecha_procesada_mercadopago NULL,        -- la primera vez que Mercado Pago informó la orden processed sin un pago legible (importe o id); no se borra
    id_comprobante_venta NULL,               -- la venta que este cobro aprobado pagó (E3-T2); FK compuesta (id_comprobante_venta, id_tenant)
    created_at, updated_at, deleted_at
)
```

> Constraints: FK `fk_cobros_mercadopago_tenant` (simple), `_empresa` (`(id_empresa, id_tenant)`), `_punto_venta`, `_dispositivo` y `_medio_pago`
> (todas compuestas con `id_tenant`, RESTRICT, sobre claves alternas existentes). CHECK de respaldo `ck_cobros_mercadopago_tipo`, `_estado`,
> `_importe` (> 0), `_importe_pagado` (nulo o >= 0), `_intento` (>= 1), `_numero` (> 0), `_aprobacion` (un cobro aprobado tiene `fecha_aprobacion`), `_aprobado_con_pago` (un cobro aprobado tiene `id_pago_mercadopago`; 23514 -> 409 `cobro_mercadopago_sin_pago`) y `_orden` (solo un cobro `creando`, o `expirado` por abandono sin orden confirmada, puede no tener `id_orden_mercadopago`).
> Único parcial `ux_cobros_mercadopago_vivo_numero` `(id_punto_venta, numero_preasignado) WHERE estado IN ('creando', 'pendiente', 'aprobado')`: a lo sumo
> un cobro vivo por número de venta; y único `ux_cobros_mercadopago_intento` `(id_punto_venta, numero_preasignado, intento)`. Ambos 23505 se
> traducen a 409 `cobro_mercadopago_en_curso` en `ManejadorDeErrores` (el servicio serializa las creaciones con un lock por punto de venta, así que
> solo los alcanza una escritura que no pase por él). Índices de soporte `ix_cobros_mercadopago_tenant`, `_empresa`, `_dispositivo` y `_medio_pago`.
> **Vínculo con la venta (E3-T2).** `id_comprobante_venta` (FK compuesta `fk_cobros_mercadopago_comprobante_venta`, RESTRICT) lo escribe la emisión de la venta
> que usa el cobro; el único `ux_cobros_mercadopago_comprobante_venta` `(id_comprobante_venta, id_tenant)` hace que un cobro pague una sola venta y una venta
> ligue un solo cobro (los nulos no chocan; a lo sumo hay un cobro vivo por número). Su 23505 es 409 `cobro_mercadopago_ya_usado` en `ManejadorDeErrores` (la venta
> toma el cobro con `FOR UPDATE` y rechaza uno ya ligado, así que solo lo alcanza una escritura que se salte ese lock). La clave alterna `ak_cobros_mercadopago_id_id_tenant`
> sostiene la FK `fk_cobros_cobro_mercadopago` de `cobros.id_cobro_mercadopago` (§4). Un cobro ligado a una venta no se borra (RESTRICT) y anular la venta no lo desliga.
> RLS estándar. Efecto: como toda FK del modelo, un punto de venta, empresa o medio de pago con cobros cuenta como en uso y no se da de baja
> (`inventario-de-dependientes.txt`, clasificación `marcado`). La tabla no se audita: es en sí el registro de cada intento y sus estados.
>
> **Endpoints (sesión de dispositivo; `OperacionDePos` + `RequiereDispositivo`).** `POST /api/pos/mercadopago/cobros` `{ idMedioPago,
> numeroPreasignado, importe }`; `GET /api/pos/mercadopago/cobros/{id}`; `POST /api/pos/mercadopago/cobros/{id}/cancelar`. Responden `{ idCobro,
> estado, qrData, expiraEn, importe, importePagado, importeDistinto, verificandoPago, pagoInformadoSinDatos }`. Un cobro de otro punto de venta o de otro tenant es 404. Crear: el medio tiene que
> estar activo, ser de la empresa del punto de venta y tener `proveedor = 'mercadopago_qr'` (400 `cobro_mercadopago_medio_invalido`), importe > 0 con
> hasta 2 decimales (400 `cobro_mercadopago_importe_invalido`), número > 0 (400 `cobro_mercadopago_numero_invalido`); el punto de venta tiene que ser
> el del dispositivo y de modo escritorio (409 `punto_venta_modo_incompatible`). Bajo un lock por punto de venta (y, si hay un cobro vivo del número, el del cobro, que se vuelve a leer después del lock): un cobro pendiente con el mismo medio e
> importe se devuelve tal cual; con otro es 409 `cobro_mercadopago_en_curso`; uno aprobado es 409 `cobro_mercadopago_ya_aprobado`; un pendiente vencido se consulta antes
> de decidir. Después, conexión activa (409 `mercadopago_no_conectada` o `mercadopago_requiere_reconexion`) y caja del punto de venta para el vendedor actual (409
> `mercadopago_caja_no_registrada`). `POST /v1/orders` con `type: "qr"`, `external_reference`, `total_amount` y `unit_price` como cadena con 2 decimales,
> `expiration_time: "PT10M"`, `config.qr.external_pos_id` = `external_id` de la caja, `mode: "dynamic"` y un ítem "Venta {número}" (`quantity` 1, `unit_measure`
> `unit`); la `X-Idempotency-Key` es la huella del cuerpo completo (`clave_idempotencia`). **Reservar y después llamar:** la fila se inserta y se confirma primero en estado `creando` (con la `external_reference`, el importe, el `external_pos_id` de la caja y la clave), recién después se llama a `POST /v1/orders` y la fila pasa a `pendiente` con el id de la orden, el QR y el plazo. Si el guardado posterior falla, la fila queda `creando`: el siguiente crear, consultar o cancelar reenvía la orden original armada con los valores guardados y la misma clave (Mercado Pago devuelve la misma orden) y aplica la respuesta; recién después rigen las reglas de arriba (otro importe es 409 `cobro_mercadopago_en_curso`). Plazos de la fila `creando` desde su reserva (`created_at`): dentro de una vigencia (10 min) se reenvía y cualquier fallo (un 4xx incluido) la deja `creando`; desde una vigencia no se reenvía más y crear, consultar y cancelar responden 409 `cobro_mercadopago_en_curso`; desde dos vigencias más 5 min pasa a `expirado` sin llamar a Mercado Pago y libera el número. Al completarse, `expira_en` se renueva. Una `processed` sin pago legible (sin importe o sin id) queda `pendiente` y la primera vez guarda `fecha_procesada_mercadopago`: desde entonces la respuesta no trae `qrData`, trae `verificandoPago: true` y `pagoInformadoSinDatos: true` (el POS dice "Mercado Pago informa el pago, falta confirmarlo"), cancelarlo es 409 `cobro_mercadopago_pagado_sin_datos` sin llamar a Mercado Pago (el cliente ya pagó; hay que esperar la confirmación o usar la aprobación manual, E5) y consultar sigue refrescando para que un pago legible posterior lo apruebe. Si la `processed` se recupera al completar la creación sin QR, `verificandoPago` también es `true`. Una orden `created` sin `qr_data` es 502 `mercadopago_respuesta_invalida`. Consultar: un cobro pendiente se refresca con `GET /v1/orders/{id}` a lo sumo cada
> 2 segundos, bajo un lock por cobro: `processed` → `aprobado` solo si trae un pago con importe pagado válido (`paid_amount` o `amount`, > 0 y hasta 2 decimales) e id no vacío de hasta 64 caracteres, recortado (se guarda el pago, el `reference_id` y el importe pagado, y si difiere de `importe`, `importeDistinto`); sin pago legible el cobro sigue `pendiente`, queda marcado con `fecha_procesada_mercadopago` y se avisa en el log. `canceled` → `cancelado`,
> `expired` → `expirado`, `refunded` → `devuelto` (solo se observa mientras el cobro está pendiente; la devolución de un cobro ya aprobado es de la conciliación, E6); cualquier otro estado lo deja pendiente. Un 404 de la orden de un pendiente cuyo plazo pasó lo deja `expirado`; antes del plazo sigue siendo 502. Cancelar: un pendiente llama a `POST /v1/orders/{id}/cancel`; si Mercado Pago contesta
> 409 se lee la orden y se devuelve su estado real (puede ser `aprobado`); un cobro aprobado es 409 `cobro_mercadopago_ya_aprobado`. Verificado contra la referencia (2026-11-01), no en vivo.
>
> **Guarda del medio.** Cambiar el `proveedor` de un medio (a nulo o a otro), sacarlo de `Electronico` o desactivarlo es 409 `medio_con_cobros_mercadopago_vivos` mientras tenga cobros `creando` o `pendiente` (los aprobados quedan como historia). La edición toma `FOR UPDATE` del medio y la reserva de un cobro `FOR KEY SHARE`, así que no se cruzan.
