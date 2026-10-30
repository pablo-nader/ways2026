> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 8. Cuenta corriente de clientes

## 8. Cuenta corriente de clientes

```sql
movimientos_cuenta_corriente (    -- [operativa]
    id_movimiento, id_cliente, fecha, id_punto_venta, id_empleado,
    tipo tipo_movimiento_cc,                 -- enum: consumo | pago | ajuste
                                             --     | actualizacion_precios | descuento_personal
    id_comprobante_venta NULL,               -- consumo/devolución que lo originó
    id_pago_comprobante NULL,                -- el pago con medio "cuenta corriente"
    importe numeric(14,2),                   -- + aumenta deuda, − la reduce
    saldo_resultante numeric(14,2),
    detalle text,
    id_movimiento_personal NULL              -- solo descuento_personal: el pago de personal (§8c)
);
```

El pago con medio `cuenta_corriente` en `pagos_comprobante` genera el movimiento
`consumo` en la misma transacción. El límite de crédito se valida contra
`saldo + nuevo consumo <= limite_credito` (o `credito_ilimitado`). La reliquidación a
precio del día (F4) recorre los consumos no actualizados, consulta `precios` vigentes y
graba **un** movimiento `actualizacion_precios` con el detalle — igual efecto de negocio
que hoy, pero auditable.

> **Estado (Etapa 7, stage-7-cuenta-corriente): implementada.** `movimientos_cuenta_corriente`
> gana `id_movimiento_actualizacion integer NULL` — un **self-FK** (`ON DELETE RESTRICT`,
> `RETURNING`-friendly) sobre la propia tabla, no un booleano: cada consumo apunta al movimiento
> `actualizacion_precios` que lo cubrió, en vez de solo marcar "ya reliquidado" — trazabilidad
> completa desde cualquier consumo hacia la corrida que lo re-precificó. Un índice parcial
> (`WHERE tipo = 'consumo' AND id_movimiento_actualizacion IS NULL`) acota el escaneo de
> elegibles a los consumos todavía sin cubrir. El pago a cuenta (`tipo = pago`) se emite como un
> comprobante `RC` de cero ítems (doc 10 §1) — el pago con medio `cuenta_corriente` en un
> comprobante de venta normal sigue generando el movimiento `consumo` como antes, sin cambios.
>
> **Estado (pago de personal): `descuento_personal`.** El valor nuevo del enum `tipo_movimiento_cc` es
> la parte de un pago al personal que se cancela contra la deuda del cliente vinculado al empleado (§8c).
> No mueve dinero: no tiene turno, comprobante ni pago. Siempre lleva `id_movimiento_personal` (FK
> compuesta `(id_movimiento_personal, id_tenant)` a `movimientos_personal`, RESTRICT), no lleva
> `id_comprobante_venta` ni `id_pago_comprobante` y su `importe` es negativo; el índice único parcial
> `ux_movimientos_cuenta_corriente_movimiento_personal` admite a lo sumo uno por pago. Dos CHECK lo
> garantizan: `ck_movimientos_cuenta_corriente_descuento_personal` (la forma) y
> `ck_movimientos_cuenta_corriente_vinculo_personal` (solo ese tipo lleva el vínculo). El valor del enum
> y los CHECK que lo nombran están en dos migraciones (`PagoDePersonal` y `ChecksDelDescuentoDePersonal`):
> Postgres no deja usar un valor de enum en la transacción que lo agregó. `EscriturasDeCuentaCorriente`
> lo escribe con su propio método (`InsertarDescuentoDePersonalAsync`); el estado de cuenta del cliente lo
> devuelve como un movimiento más (`tipo = DescuentoPersonal`, `etiqueta` nula). El contrato sí cambia: `tipo`
> gana ese valor, así que el web lo declara en `TipoMovimientoCc` y lo etiqueta "Descuento de personal"
> (`etiquetaDeMovimiento`); un cliente que no conozca el valor mostraría el tipo vacío.
>
> **Deviación declarada — fracción financiada:** la reliquidación re-precifica solo la porción
> del comprobante que quedó fiada (`factor = min(1, importeFinanciado / totalComprobante)`), no
> el ticket completo — el legacy re-precificaba el ticket entero incluso cuando ya estaba
> parcialmente pagado. Con financiamiento total (`factor = 1`, el caso normal) la fórmula
> colapsa exacto a la del legacy; el dueño del negocio confirmó la semántica de financiamiento
> parcial como aceptable para esta etapa.
>
> **Ajuste manual de precio (sin cambio de esquema):** la reliquidación conserva el
> `ajuste_manual_porcentaje` de cada item. El total del día de una línea es `neto nuevo +
> ajuste` con `ajuste = round(neto nuevo × porcentaje / 100)`, la misma fórmula y el mismo
> redondeo que el checkout (`CalculadorDeTotales.AjusteManualSobre`), en vez del neto solo: un
> −10% manual sobre una línea de 100 que hoy cuesta 120 se re-precifica a 108 (delta 18, no 30)
> y un +20% con el precio sin cambios da delta 0 (no un crédito de 20). El descuento de oferta
> sigue anulándose como antes; solo el porcentaje manual sobrevive, y las líneas sin porcentaje
> dan exactamente el mismo resultado de siempre. El movimiento de diferencia se compara contra
> `items.total` (que ya incluye el ajuste original).
>
> **Irreversibilidad:** ningún endpoint revierte ni edita un movimiento `actualizacion_precios`
> — la única corrección posible es un `Ajuste` manual nuevo, distinto y auditable por su propio
> `detalle`. La reliquidación tampoco tiene turno de caja: no mueve plata física, así que no
> aporta ningún término al arqueo — mismo criterio que el ajuste manual. Ambos llevan
> `id_punto_venta` como *provenance* (de dónde salió el pedido), nunca como autoridad (ninguno
> de los dos deriva nada de un turno).
>
> **Pagar una compra (sin cambio de esquema):** `POST /api/compras/{id}/pagos`
> (`ServicioDePagos.PagarCompraAsync`, `GestionDeCatalogo`) es el camino directo desde el detalle
> de la compra y desde la fila `compra` de la cuenta corriente del proveedor. Cuerpo: `fecha`
> (fecha de negocio, puede ser retroactiva, nunca futura en la zona de la empresa), `importe`,
> `idMedioPago` (nunca un medio de cuenta corriente) y `concepto` opcional (sin él queda
> `Pago <tipo> <número>`). Empresa, punto de venta, proveedor y categoría salen de la compra, no del
> cliente. En UNA transacción crea el pago (`categoria = proveedor`, `origen_fondos = tesoreria`,
> sin turno, `id_comprobante_compra` = la compra, `fecha` = la fecha de negocio), el movimiento
> `pago` imputado a esa compra (con `now`, como todo el ledger) y el egreso de tesorería: reusa el
> mismo escritor que el alta administrativa de un pago ligado a una compra, no duplica ninguna
> escritura de ledger. Reglas: la compra tiene que estar `confirmada` (`409 compra_no_confirmada`
> / `compra_anulada`); `importe > 0` (`400 gasto_importe_invalido`); `importe` ≤ saldo pendiente de
> ESA compra (`409 pago_excede_saldo_pendiente`, o `409 compra_sin_saldo_pendiente` si ya está
> saldada). Saldo pendiente = `total − pagado` (piso en cero), con `pagado` por la fórmula OD7
> (`LectorDePagadoPorCompra`, la única fuente: el estado de pago, el detalle, el listado y la cuenta
> corriente leen de ahí). Concurrencia: la compra se toma `FOR UPDATE` como primer lock (los demás
> escritores que la tocan la toman `FOR SHARE` o exclusiva, la confirmación después del lock de
> membresía de familias compartido, que el pago no pide; sin ciclo) y el pagado se lee después del
> lock, así que dos pagos que juntos superan el saldo no pueden entrar los dos; sin reintento
> automático (un commit ambiguo sale como `503 resultado_incierto`). Un pago editado hacia
> arriba o dado de baja después de imputarse no toma el lock de la compra: puede dejarla sobrepagada
> o con saldo distinto, que queda en cero y nunca negativo. Lecturas: `GET /api/compras/{id}` suma
> `pagado` y `saldoPendiente` (cero fuera de una compra confirmada), `GET /api/compras` suma
> `saldoPendiente` (lo usa el selector de vincular un pago a una compra) y las filas `compra` de
> `GET /api/proveedores/{id}/cuenta-corriente` suman `saldoPendienteDeLaCompra`. Son datos de
> encabezado, los ve también el vendedor; el costo de las líneas sigue oculto.

### 8b. Cuenta corriente de proveedores

> **Edición de una compra confirmada (E6):** el tipo `compra` deja de ser «uno por compra». Editar una compra confirmada asienta, por la
> diferencia, un movimiento `compra` por proveedor afectado (importe con signo; si cambia el proveedor, la reversa de lo suyo al anterior
> y el total al nuevo). La suma de los asientos `compra` de una compra sigue siendo su total. El estado de cuenta muestra
> `saldoPendienteDeLaCompra` solo en el asiento `compra` más nuevo de cada compra.

```sql
movimientos_cuenta_corriente_proveedor (   -- [operativa — id_punto_venta NULL en el asiento de apertura]
    id_movimiento         integer     GENERATED BY DEFAULT AS IDENTITY,
    id_tenant             integer     NOT NULL,
    id_proveedor          integer     NOT NULL,
    fecha                 timestamptz NOT NULL,   -- IRelojDelSistema, sin DEFAULT now()
    id_punto_venta        integer     NULL,       -- NULL solo en tipo = 'apertura'
    id_empleado           integer     NULL,       -- NULL solo en tipo = 'apertura'
    tipo                  tipo_movimiento_cc_proveedor NOT NULL,  -- enum: apertura | compra | pago | ajuste | redondeo
    id_comprobante_compra integer     NULL,       -- 'compra': la que originó la deuda
                                                  -- 'pago'/'ajuste': la compra imputada
                                                  -- 'redondeo': la compra cuyo saldo se perdonó (o este NULL y va id_pago)
    id_pago              integer     NULL,       -- el pago registrado que materializó el movimiento
    importe               numeric(14,2) NOT NULL, -- + aumenta deuda, − la reduce
    saldo_resultante      numeric(14,2) NOT NULL,
    detalle               text        NULL        -- obligatorio en el ajuste manual (regla de servicio)
);
```

> **Estado (Etapa 15, stage-15-cc-proveedores-ledger): implementada — etapa completa (PRs #134-#139).**
> Los 4 caminos de escritura activos (apertura por backfill, `compra` en confirmación, `pago` vía
> pago, `ajuste` por contramovimiento de anulación y por el endpoint manual bajo
> `SupervisionDeCuentaDeProveedor`); estado de cuenta paginado y `ServicioDeSaldoDeProveedor`
> re-sourceado sobre el ledger (fórmula OD7: pagos ligados + ajustes imputados; los `pago` no se
> cuentan).
> Mismo criterio que `movimientos_cuenta_corriente` (§8): ledger append-only, sin `EntidadBase`
> (sin `updated_at`, sin soft delete), `id_tenant` escrito explícito (nunca `EstamparTenant()`).
> **Sin clave alterna ni self-FK** — a diferencia de la tabla de clientes, esta etapa no incluye
> reliquidación, así que ninguna tabla referencia este ledger. 6 FKs (`tenant`, `proveedor`
> compuesta, `punto_venta` compuesta MATCH SIMPLE nullable, `empleado` simple, `comprobante_compra`
> compuesta nullable, `pago` compuesta nullable — requiere la clave alterna nueva de `pagos`,
> abajo), todas RESTRICT; CHECK `ck_movimientos_cuenta_corriente_proveedor_apertura` (la
> nullability de `id_punto_venta`/`id_empleado` es exactamente el asiento de apertura); RLS
> estándar. `proveedores.saldo` (arriba, §2) es la cache mantenida por
> `EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync` (raw `UPDATE ... RETURNING`,
> nunca un `proveedor.Saldo +=` trackeado) — el mismo criterio de `Cliente.Saldo`. `pagos` gana
> `ak_pagos_id_pago_id_tenant UNIQUE (id_pago, id_tenant)`, estructuralmente inviolable
> (`id_pago` ya único vía `pk_pagos`), requerida por la FK compuesta `fk_..._pago`.
>
> **Backfill idempotente, en la misma migración:** un asiento `apertura` por proveedor cuyo saldo
> derivado (`Σ compras confirmadas no eliminadas − Σ pagos categoria=proveedor con id_proveedor
> no eliminados`) es distinto de cero, reproduciendo EXACTAMENTE la fórmula que esta etapa retira
> (ver la nota retirada más abajo). Un proveedor con saldo derivado `0` no recibe fila. Reejecutar
> la migración no escribe filas adicionales ni cambia ningún saldo.
>
> **Los cuatro escritores, uno por valor del enum:** `apertura` ← la migración (arriba, una sola
> vez); `compra` ← `ServicioDeCompras.ConfirmarAsync` (Slice 2); `pago` ← imputado dentro de
> `ServicioDePagos.InsertarPagoAsync` cuando el pago es `categoria = proveedor` con
> `id_proveedor` (Slice 3); `ajuste` ← el contramovimiento de `ServicioDeCompras.AnularAsync`
> (Slice 2) y el ajuste manual bajo `Politicas.SupervisionDeCuentaDeProveedor` (Slice 5). El
> estado de cuenta paginado (`GET /api/proveedores/{id}/cuenta-corriente`) y el estado de pago
> por compra re-sourced desde el ledger (Slice 4). Todos mergeados.
>
> **`redondeo` (E4 de pagos y aplicaciones).** Quinto valor del enum (`ALTER TYPE ... ADD VALUE` en su propia migración,
> `RedondeoEnCuentaCorrienteDeProveedor`, porque Postgres no deja usar el valor en la transacción que lo agrega) y CHECK
> `ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino` (`tipo <> 'redondeo' OR num_nonnulls(id_comprobante_compra,
> id_pago) = 1`, migración `RedondeoUnDestino`): un redondeo perdona o absorbe la diferencia de UNA compra o de UN pago, nunca de
> las dos ni de ninguna. Como las demás filas lleva punto de venta y empleado (la CHECK de apertura). Signos, siempre el de la
> deuda que deja de existir, de modo que el saldo del proveedor queda neto en cero:
> - **Lado compra** (faltó pagar): `importe` NEGATIVO con `id_comprobante_compra`. Pagar 1500 una compra de 1503,50 escribe −3,50.
> - **Lado pago** (sobró): `importe` POSITIVO con `id_pago`. Pagar 1500 una compra de 1430,25 escribe +69,75 sobre el pago.
> - **Reversa**: otro `redondeo` del mismo destino con el signo opuesto (el ledger no se reescribe). Los lectores suman todos, así
>   que un redondeo revertido pesa 0. La reversa lleva el punto de venta del movimiento original (pago) o el de la compra.
>
> Lo escriben el pago de una compra con `cerrarEnCero`, `POST /api/compras/{id}/ajuste-de-redondeo` y `POST
> /api/pagos/{id}/ajuste-de-redondeo` (ver §5, E4) y lo revierten la anulación de la compra, la baja y la edición de importe de un
> pago, y desaplicar o recortar una aplicación de una compra con redondeo. El estado de cuenta lo rotula «Redondeo».

### 8c. Cuenta corriente del personal

```sql
movimientos_personal (                    -- [operativa — libro append-only]
    id_movimiento     integer       GENERATED BY DEFAULT AS IDENTITY,
    id_tenant         integer       NOT NULL,
    id_personal       integer       NOT NULL,   -- FK compuesta (id_personal, id_tenant)
    id_empleado       integer       NOT NULL,   -- FK simple a usuarios: el usuario que registró el asiento
    fecha             date          NOT NULL,   -- carga de horas: el día trabajado; resto: la fecha del asiento
    tipo              tipo_movimiento_personal NOT NULL,  -- enum: horas | pago | ajuste
    horas             numeric(6,2)  NULL,       -- solo 'horas'
    valor_hora        numeric(14,2) NULL,       -- solo 'horas'; congelado, no sigue a personal.valor_hora
    importe           numeric(14,2) NOT NULL,   -- + aumenta lo que se le debe, − lo reduce
    saldo_resultante  numeric(14,2) NOT NULL,   -- foto de personal.saldo al insertar
    detalle           text          NULL,       -- obligatorio en el ajuste (regla de servicio)
    created_at        timestamptz   NOT NULL    -- IRelojDelSistema, sin DEFAULT now()
);
```

> Ledger append-only, sin `EntidadBase` (sin `updated_at` ni soft delete) y con `id_tenant` escrito
> explícito; una carga mal hecha se corrige con un `ajuste`, nunca editando. `personal.saldo` solo lo
> mueven el `UPDATE ... RETURNING` y el `INSERT` del asiento de `EscriturasDePersonal`, en una misma
> transacción sin reintento. Los tres valores del enum están declarados desde el inicio; escriben `horas`
> (`POST /api/personal/{id}/horas`), `ajuste` (`POST /api/personal/{id}/ajustes`) y `pago`
> (`POST /api/personal/{id}/pagos`, más abajo). La `fecha` es obligatoria en los tres (400
> `fecha_requerida`): la manda el cliente con su día local y el servidor no deduce el día de negocio.
> El estado de cuenta (`GET /api/personal/{id}/cuenta`) sin `historico` ni rango abarca desde un mes
> atrás (contado desde la fecha UTC), sin tope superior; un `hasta` explícito sí lo aplica.
>
> **Sin `id_punto_venta`, a propósito** (desvío respecto de §8b): las cargas de horas, los ajustes y los
> pagos son de back office y no pasan por un turno; un pago llega a un punto de venta únicamente a
> través de los pagos y del descuento de cuenta corriente que genere.
>
> CHECK, uno por tipo: `ck_movimientos_personal_horas` (`horas` en [−24, 24] y distinto de cero, `valor_hora >= 0` e
> `importe = round(horas × valor_hora, 2)`; el signo del importe sigue al de las horas), `ck_movimientos_personal_ajuste`
> (`horas` y `valor_hora` nulos, `importe <> 0`) y `ck_movimientos_personal_pago` (`horas` y
> `valor_hora` nulos, `importe < 0`). FKs `fk_movimientos_personal_{tenant,personal,empleado}`, todas
> RESTRICT. Índices: `ix_movimientos_personal_tenant`, `ix_movimientos_personal_personal_fecha`
> `(id_personal, id_tenant, fecha)` y `ix_movimientos_personal_empleado`. RLS estándar. Un personal
> con movimientos no se puede dar de baja (409 `personal_en_uso`). El asiento tiene la clave alterna
> `ak_movimientos_personal_id_movimiento_id_tenant`, destino de las FKs compuestas que siguen.
>
> **Horas con signo.** Una carga individual (`POST /api/personal/{id}/horas`) sigue exigiendo `horas > 0`. Las
> horas negativas son correcciones que escribe solo la carga semanal (abajo): bajar el total de un día agrega
> asientos negativos que consumen las capas positivas del día de la última a la primera (LIFO), cada uno al
> `valor_hora` congelado de su capa, de modo que el importe revierte lo que esa capa acreditó (con el redondeo
> propio de cada asiento: al revertir una capa en varias partes la suma puede diferir unos centavos de lo
> acreditado). El ledger sigue siendo append-only.
>
> **Tope de 24 h por personal y por día**, sobre la suma de las horas del día (con signo). Lo aplican la carga
> individual (400 `horas_del_dia_excedidas`) y la semanal (`horas_invalidas` si el total pedido supera 24); un día
> heredado con más de 24 h se puede reducir a 24 con la carga semanal. Entre personales no hay tope.

```sql
jornadas_personal (                       -- [operativa — mutable, sin baja lógica]
    id_jornada     integer       GENERATED BY DEFAULT AS IDENTITY,
    id_tenant      integer       NOT NULL,
    id_personal    integer       NOT NULL,   -- FK compuesta (id_personal, id_tenant)
    id_empleado    integer       NOT NULL,   -- FK simple a usuarios: quien confirmó el día por última vez
    fecha          date          NOT NULL,
    horas          numeric(4,2)  NOT NULL,   -- total confirmado del día, 0 a 24
    created_at     timestamptz   NOT NULL,
    updated_at     timestamptz   NOT NULL
);
```

> Marca un día de un personal como **confirmado** y guarda su total, para distinguir «confirmado en 0 h» (la fila
> existe con `horas = 0`) de «sin cargar» (no hay fila: la grilla semanal lo precarga con el horario habitual).
> **Invariante:** después de cada escritura `jornadas_personal.horas` es igual a la suma de `movimientos_personal.horas`
> de tipo `horas` de ese personal y día; la verdad del dinero sigue siendo el libro. La escriben la carga semanal
> (todos los días de la solicitud, también los que no cambian) y la carga individual (con el total nuevo del día),
> siempre con SQL crudo dentro de la transacción que ya tiene la fila de `personal` bloqueada (`EscriturasDePersonal`),
> como un upsert `ON CONFLICT (id_personal, id_tenant, fecha) DO UPDATE`: la unicidad no se puede violar desde la
> aplicación. Sin `EntidadBase` (no hay baja lógica), `id_tenant` escrito explícito y filtro de tenant a mano.
> `ck_jornadas_personal_horas` (`horas >= 0 AND horas <= 24`); FKs `fk_jornadas_personal_{tenant,personal,empleado}`,
> todas RESTRICT; índice único `ux_jornadas_personal_personal_fecha` `(id_personal, id_tenant, fecha)` (cubre por
> prefijo la FK compuesta), `ix_jornadas_personal_tenant` e `ix_jornadas_personal_empleado`. RLS estándar. Un personal
> con jornadas no se puede dar de baja (409 `personal_en_uso`), igual que con movimientos.
>
> **Carga semanal.** `GET /api/personal/horas/semana?desde=yyyy-MM-dd` (`LecturaDePersonal`; `desde` debe ser lunes,
> 400 `semana_invalida`; sin `desde`, el lunes de la semana actual) devuelve lunes a domingo de cada personal vivo
> del tenant, ordenado por nombre: por día `horasCargadas` (suma del libro), `confirmado` (existe la jornada o el
> libro tiene horas ese día), `horas` (lo cargado si está confirmado; si no, el horario habitual del día, salvo días
> futuros o sin horario, que van en `null`), `precargado` y `futuro`. `POST /api/personal/horas/semana`
> (`GestionDeCuentaDePersonal`) recibe `{ dias: [{ idPersonal, fecha, horas }] }` donde `horas` es el TOTAL deseado del
> día (0 a 24, hasta dos decimales; 0 confirma el día sin horas), de 1 a 1000 días sin repetir `(idPersonal, fecha)` y
> ninguno futuro (400 `fecha_futura`; el día es el de la zona horaria de la empresa del personal, o la zona por
> defecto si no tiene empresa). Un personal inexistente, dado de baja o de otro tenant responde 404 antes de validar
> el resto. En UNA transacción sin reintento bloquea los personales en orden ascendente de id, lee el libro del día
> recién después del lock, escribe solo las diferencias (asientos con detalle «Carga semanal», saldo atómico) y
> confirma todos los días. Responde `{ movimientosCreados, jornadasConfirmadas }`.
>
> **Pago de personal** (`POST /api/personal/{id}/pagos`, `GestionDeCuentaDePersonal`: Admin y Supervisor).
> **Pagar de la tesorería es solo de Admin**, como el alta administrativa de pagos (`/api/gastos/administracion`,
> `GestionDeCatalogo`): un Supervisor que manda `origenFondos = Tesoreria` recibe 403 `prohibido`, decidido
> después del 404 del personal y antes de cualquier otro 400 del cuerpo y de cualquier escritura. El Supervisor
> sí paga del cajón (`CajaTurno`) y descuenta de la cuenta corriente. El asiento `pago` es la cabecera: no hay tabla de cabecera aparte. En UNA transacción sin reintento
> (`ServicioDePagoDePersonal`) escribe: el asiento `pago` con `importe = −(descuento + suma de líneas)`
> (el saldo puede quedar negativo: es un adelanto); a lo sumo un movimiento `descuento_personal` en la cuenta
> corriente del cliente vinculado al personal (§8), que baja su deuda y se rechaza si el personal no tiene
> un cliente vivo vinculado (409 `personal_sin_cliente_vinculado`) o si el saldo que devuelve el `UPDATE`
> atómico del cliente queda negativo (409 `descuento_supera_la_deuda`). Ese `UPDATE` exige además
> `deleted_at IS NULL`: la baja de un cliente (`ServicioDeClientes.EliminarAsync`) es una baja lógica común, sin
> lock explícito ni chequeo de referencias, así que no está serializada con el pago; si el cliente se da de baja
> mientras el pago espera su fila, el `UPDATE` reevalúa y no devuelve fila, y el pago responde 409
> `personal_sin_cliente_vinculado` y se revierte entero. Además del descuento, el movimiento `pago` del personal escribe un pago de categoría `sueldos`
> por cada línea de dinero (`{ idMedioPago, importe, referencia? }`, la referencia queda en `pagos.detalle`).
> El origen de los fondos se elige en cada pago (`origenFondos`, obligatorio con líneas y rechazado sin
> ellas): `Tesoreria` no usa turno, admite fecha retroactiva (nunca futura, en la zona de la empresa), pide
> `idEmpresa` y escribe un egreso encadenado de tesorería por pago; `CajaTurno` exige `idPuntoVenta` con
> turno abierto (409 `turno_no_abierto`), el pago lleva el instante real como fecha y entra al arqueo del
> turno. El descuento exige `idPuntoVenta` porque el asiento de cuenta corriente lo lleva. Las demás reglas y
> códigos son los de los pagos que reemplaza (medio de cuenta corriente 400, fecha futura 400,
> empresa/punto de venta/medio 404).
>
> `pagos` y `movimientos_cuenta_corriente` ganan `id_movimiento_personal integer NULL` con FK compuesta
> `(id_movimiento_personal, id_tenant)` a `movimientos_personal` (`fk_pagos_movimiento_personal`,
> `fk_movimientos_cuenta_corriente_movimiento_personal`, RESTRICT) e índice de soporte
> (`ix_pagos_movimiento_personal`; en cuenta corriente lo cubre el índice único parcial). El vínculo se
> escribe solo al insertar. **Un pago con `id_movimiento_personal` es inmutable:** la edición del POS,
> la edición y la baja administrativas y la vinculación posterior a una compra lo rechazan con 409
> `gasto_de_pago_de_personal`, después del 404, porque tocarlo desalinearía los cuatro libros del pago.
> Para que un cliente sepa de antemano qué pagos responderán así, los listados del POS (`GET /api/gastos`, el
> detalle del turno) y de administración (`GET /api/gastos/administracion` y su detalle) traen
> `idMovimientoPersonal` (nulo en un pago común) y el web deshabilita en esos pagos la edición, la baja y la
> vinculación a una compra. Anular o revertir un pago todavía no existe. El estado de cuenta (`GET /api/personal/{id}/cuenta`) trae
> en cada movimiento `pago` su `desglose`: el descuento y una línea por pago (medio, importe, origen,
> referencia).
>
> **Orden de locks del pago**, el mismo orden relativo de los demás escritores (turno, filas de negocio,
> advisory de tesorería al final): 1) turno `FOR SHARE` (solo `CajaTurno`); 2) fila de `personal`
> (`UPDATE ... RETURNING saldo, id_cliente`, que lee el cliente vinculado bajo el lock); 3) fila de
> `clientes` (`UPDATE ... RETURNING saldo`, solo con descuento); 4) inserts sin lock explícito (asiento,
> movimiento de cuenta corriente, pagos); 5) advisory de tesorería de la empresa, último (solo
> `Tesoreria`). Dos pagos al mismo personal se serializan en el paso 2; ningún escritor toma la fila del
> cliente y después la del personal, y el alta, la edición y la baja de pagos, el pago de una compra y el
> cierre de turno toman sus locks en un orden compatible con éste. El detalle del análisis de ciclos está en
> el comentario de clase de `ServicioDePagoDePersonal`.
