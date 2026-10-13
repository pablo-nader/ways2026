> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 3. Artículos, listas de precio y ofertas

### Combos

Un combo es un conjunto de dos o más unidades que se vende a un precio fijo (ej. 1 pancho $3.000 + 1 coca $2.500 =
$5.000). Cada componente es un artículo o una familia, con su cantidad. La venta nunca lleva una línea "combo": sigue
guardando las líneas de los artículos reales y el combo es un descuento repartido entre ellas, así que stock, IVA,
anulación y reportes siguen funcionando por artículo.

```sql
combos (                      -- [catálogo]
    id_combo,
    id_empresa integer NULL,                 -- NULL = toda empresa del tenant
    codigo     integer NOT NULL,             -- lo que se tipea tras la "C" en el POS
    nombre     citext,                       -- lo que imprime el ticket (no único)
    precio     numeric(14,2) NOT NULL,       -- precio fijo del conjunto, no depende de la lista de precios
    activo,
    disponible_online boolean NOT NULL DEFAULT true  -- la tienda online lo ofrece (§3, "Tienda online")
);
CHECK (codigo > 0);                          -- ck_combos_codigo_positivo
CHECK (precio > 0);                          -- ck_combos_precio_positivo
UNIQUE (id_tenant, codigo) WHERE deleted_at IS NULL;   -- ux_combos_codigo

combos_componentes (          -- [catálogo] hijo de combos, sin auditoría (PK propia)
    id_combo_componente, id_tenant, id_combo,
    id_articulo integer NULL,                -- FK (id_articulo, id_tenant) -> articulos (Restrict)
    id_familia  integer NULL,                -- FK (id_familia, id_tenant)  -> familias  (Restrict)
    cantidad    numeric(12,3) NOT NULL
);
CHECK (num_nonnulls(id_articulo, id_familia) = 1);   -- ck_combos_componentes_alcance_exclusivo
CHECK (cantidad > 0);                                -- ck_combos_componentes_cantidad_positiva
UNIQUE (id_combo, id_articulo) WHERE id_articulo IS NOT NULL;  -- ux_combos_componentes_articulo
UNIQUE (id_combo, id_familia)  WHERE id_familia  IS NOT NULL;  -- ux_combos_componentes_familia
-- FK (id_combo, id_tenant) -> combos (Cascade)
```

Reglas que el esquema no puede expresar y que viven en `ReglaDeCombos`: el conjunto tiene al menos dos
componentes, o uno solo con cantidad dos o más (`combo_unidades_insuficientes`), las cantidades son mayores a cero, de
hasta 3 decimales y no superan 999.999 (`combo_cantidad_invalida`), el precio es mayor a cero, tiene hasta dos
decimales y no supera 999.999.999.999,99 (`combo_precio_invalido`), para que ningún valor llegue a la base como 22003 ni
la respuesta muestre un precio sin redondear, y el combo tiene hasta 50 componentes (`combo_componentes_excedidos`,
`ReglaDeCombos.ComponentesMaximos`; cada uno cuesta un `FOR KEY SHARE` bajo el lock por tenant). Que la cantidad
sea la que admite la unidad de venta del artículo (entera para `Unidad`, hasta 3 decimales para `Peso`) NO lo valida
`ReglaDeCombos`, que no recibe la unidad de venta: lo exige `ServicioDeCombos` (`combo_cantidad_no_admitida`; ver "API"
más abajo). Solo entran a combos los artículos por `Unidad` y por `Peso` (`ReglaDeCombos.ParticipaEnCombos`): uno que se
vende por `Importe` nunca es componente (`combo_componente_por_importe`), ni candidato de un componente, y sus líneas no
consumen un combo, aunque su cantidad sea 1. `ResolvedorDeCombos` (dominio puro) detecta los conjuntos completos en el
documento y reparte el ahorro entre las líneas consumidas:

- Un combo aplica tantas veces como conjuntos completos haya; cada unidad la consume un solo combo. El algoritmo es
  voraz y esta es su definición: en cada vuelta gana, entre los combos que caben, el de mayor ganancia real por
  aplicación (lo que cuestan esas unidades con sus ofertas menos el precio del combo, la misma cantidad que decide si
  el combo es aplicable), y a igual ganancia el de menor `id_combo`. Con la asignación de líneas de esa vuelta, el
  combo se aplica de una vez k veces, con k = mínimo entre las líneas asignadas de (unidades restantes / unidades
  tomadas por aplicación) redondeado hacia abajo, y recién entonces se reevalúa. El reparto por línea se calcula para
  una aplicación y se multiplica por k. No busca el óptimo global cuando varios combos se pisan.
- Dentro de un combo se cubren primero los componentes artículo y después los familia; entre líneas que pueden
  cubrir un mismo componente se elige por orden de documento (el orden de entrada). Por eso un combo puede
  descartarse aunque otro emparejamiento de las líneas del documento lo completaría.
- Las unidades consumidas se cobran a precio de lista menos su parte del combo; la oferta no se apila sobre ellas, y
  el combo solo aplica si queda estrictamente más barato que esas mismas unidades con su oferta. Las unidades de la
  misma línea que no entran al combo conservan su oferta.
- El ahorro de una aplicación (lista de las unidades consumidas − precio del combo) se reparte entre las líneas en
  proporción a su importe de lista, redondeado a centavos (`AwayFromZero`). La parte de cada línea queda entre cero y
  el importe de lista de las unidades que aporta: el resto de redondeo se coloca en la última línea que puede
  absorberlo y, si no cabe, retrocede a las anteriores. Las unidades consumidas de cada conjunto suman exactamente el
  precio del combo. Es un importe de línea, no unitario.
- Las líneas con cantidad no positiva, o cuya cantidad no admite su unidad de venta (fracción de un artículo por
  unidad), se ignoran en la detección, igual que las líneas de un artículo por importe. Los artículos pesables se detectan: el combo consume exactamente la cantidad del
  componente (hasta 3 decimales) y el resto de la línea queda a precio de lista. Qué cantidades valen lo decide la
  unidad de venta de la línea, nunca la forma del número.
- El cobro de una aplicación es el precio del combo redondeado al peso entero, exacto por aplicación. El importe de
  lista de lo que aporta cada línea se redondea a dos decimales en cada aplicación; como esa cuenta se repite k veces,
  el ahorro acumulado de una línea se acota por el importe de lista, redondeado a dos decimales, de todo lo que
  consumió, y la línea nunca queda cobrada en negativo.

#### Combos en la venta online

`ServicioDeVentas.EmitirAsync` aplica combos en la fase de decisión, después de resolver las ofertas y antes de los
totales, el FEFO y la validación de pagos. Solo en la venta online de signo positivo: quedan afuera la conversión de un
presupuesto (precios congelados), la venta con `NumeroPreasignado` (precios del dispositivo: el servidor detecta combos
solo para auditar, ver "Combos en el POS offline") y las notas de crédito, que
nunca detectan combos (la asociada a una venta con combo devuelve lo cobrado: ver "Nota de crédito de una venta con
combo" más abajo).
El combo nunca es una línea de la venta: la venta conserva las líneas de los artículos reales y el combo es un descuento
repartido entre ellas, así que stock (un movimiento por artículo), IVA (la alícuota de cada artículo sobre el total de
línea ya descontado) y anulación siguen funcionando por artículo.

- `items_comprobante_venta.id_combo` (FK compuesta `(id_combo, id_tenant)` → `combos`, `Restrict`, índice
  `ix_items_comprobante_venta_combo`, sin backfill) guarda el combo de la línea; si varios combos consumieron unidades
  de la misma línea se guarda el de menor `id_combo`. Es un snapshot: la venta emitida se lee igual aunque el combo se
  dé de baja después (la baja es lógica y la FK nunca queda colgada).
- El descuento de la línea es `round(descuento_de_oferta_unitario × unidades no consumidas) + parte del combo`
  (`CalculadorDeTotales`, campos `UnidadesConsumidasPorCombo` y `AhorroDeCombo` de `LineaParaCalcular`). Una línea cuyas
  unidades se consumieron todas no lleva `id_oferta` (no recibió ninguna oferta); si le quedan unidades fuera del combo,
  conserva su oferta y su `id_oferta`.
- El ajuste manual (`ajuste_manual_porcentaje`) se aplica sobre el neto de la línea que YA incluye la parte del combo:
  el precio del combo rige antes de los ajustes manuales. Con una coca de 2.500 que recibe 227,27 de un combo, un −10 %
  se calcula sobre 2.272,73 (−227,27), no sobre los 2.500 de lista; el neto, el ajuste y el total de la línea salen de la
  misma fórmula de `CalculadorDeTotales` con o sin combo, y el total de la venta deja de coincidir con el precio del
  combo en la medida del ajuste.
- Los candidatos se cargan con UNA consulta (combos vivos y activos de la empresa del punto de venta o de todo el tenant,
  unidos con sus componentes), salvo que todas las líneas sean de unidad `Unidad` y el documento sume menos de 2 (ahí no puede completarse
  ningún combo). La consulta trae solo los combos que tienen al menos un componente que el carrito puede satisfacer
  (un artículo del carrito, o su familia), pero de esos trae TODOS los componentes: el motor necesita el conjunto completo. Todo se decide antes de la
  transacción y viaja en el plan: un reintento de la escritura no vuelve a detectar combos.

##### Nota de crédito de una venta con combo

Una nota de crédito (NCX) nunca DETECTA combos, pero la que está asociada (`id_comprobante_asociado`) a una venta cuyos
items consumieron un combo devuelve lo que se cobró por esas unidades y no lo que cuestan hoy (decisión del dueño,
2026-10-11): devolver un pancho y una coca de un combo de 5.000 acredita 5.000, no 5.500. Se decide en la fase de decisión
de `EmitirAsync` (`DevolucionDeCombos`, pura); nada ocurre dentro de la lambda reintentable.

- **Compuerta.** La NCX es online (sin `NumeroPreasignado`), tiene comprobante asociado y ese comprobante tiene items con
  `id_combo` y cantidad positiva de alguno de los artículos devueltos. Si no, la NCX se precia como siempre, con el
  mismo plan y las mismas filas: la devolución de una venta sin combos sigue usando la lista y las ofertas de HOY y no lo
  cobrado (decisión aparte, no tomada). Una línea cuyo artículo no figura en un item de combo de la venta asociada
  también se precia como siempre. Un asociado que es a su vez una NCX no aporta items (sus cantidades son negativas).
- **Importe.** Para las unidades que toma de un item de combo, el crédito es lo cobrado por ese item en proporción a las
  unidades: devolver TODAS las unidades del item acredita exactamente su `total`; una devolución parcial acredita
  `round(total × devueltas / cantidad, 2)` (`AwayFromZero`). El `total` del item ya incluye la parte del combo, la oferta
  de las unidades que no entraron al combo y el ajuste manual de esa línea, así que el crédito devuelve el total
  cobrado, ajuste incluido. El consumo se acumula entre las líneas del mismo documento y el crédito de cada toma es
  `round(total × unidades acumuladas / cantidad, 2)` menos lo ya acreditado de ese item: como el redondeo es monótono,
  ninguna toma resulta negativa y repartir el item en varias líneas no pierde ni suma centavos del crédito del item (la
  suma de las tomas es exactamente su `total`). Esto vale para el crédito del item; el `ajuste_manual_porcentaje` que
  traiga la línea de la NCX se redondea aparte, por tramo (ver más abajo).
- **Ajuste manual positivo (recargo) en la línea original.** Si la línea vendida llevaba un recargo, su `total` supera al
  bruto y el crédito también: se devuelve lo que se pagó. El `descuento` persistido del item de la NCX tiene entonces el
  signo opuesto al de un descuento normal de NCX (positivo), porque `bruto − descuento = −crédito` con crédito mayor que
  el bruto (coca de 2.500 con +20 % sobre su parte del combo: total 2.727,28, `descuento` +227,28).
- **Línea resultante.** Cada tramo es un item de la NCX con el `precio_unitario` del item original, el `id_combo`, la
  `id_lista_precio` y el `id_oferta` del original, y la alícuota (`id_alicuota_iva` y `porcentaje_iva`) del original:
  el artículo pudo cambiar de alícuota desde la venta y el IVA de lo devuelto es el de lo cobrado. La diferencia entre
  el bruto (`−unidades × precio`) y el crédito es el `descuento` de la línea, con el signo de la cantidad (negativo,
  como todo descuento de una NCX): `bruto − descuento = −crédito`. Es el mismo mecanismo de importe de línea que la
  parte del combo de una venta (`UnidadesConsumidasPorCombo` y `AhorroDeCombo` de `LineaParaCalcular`). Una línea
  devuelta que cruza varios items de combo da un item de la NCX por tramo; stock y lote se reponen por item.
- **Un `ajuste_manual_porcentaje` en la línea de la NCX** sigue siendo válido y se aplica ENCIMA del neto devuelto, por
  tramo, con la misma fórmula de siempre (`−10 %` sobre un neto de −2.272,73 resta 227,27 de lo devuelto). No es lo
  mismo que el ajuste de la línea original, que ya está dentro del `total` que se devuelve.
- **Orden cuando el artículo está en varios items.** Las unidades de una línea devuelta se toman SOLO de items de combo,
  en orden creciente de `orden` del item (empate por menor `id_item`), y las líneas del mismo documento consumen en el
  orden en que vienen. Los items del mismo artículo fuera de un combo no se toman: si se devuelven más unidades que las
  vendidas en combo, el excedente se precia como siempre (lista y ofertas de hoy, sin detectar combos). Así una NCX nunca
  acredita por una unidad de combo más que su `total` y, mientras el precio de hoy no supere lo cobrado, tampoco más que
  el total de la venta.
- **Tope por documento, no por venta.** El sistema no lleva cuenta de lo devuelto por NCX anteriores asociadas a la misma
  venta ni valida las cantidades contra las de la venta: una segunda NCX de la misma venta vuelve a partir de los items
  originales. El tope es el de un solo documento (nunca acredita más unidades de combo que las del item). Queda como
  decisión de producto abierta.
- **Costo de consultas.** UNA consulta más (los items de combo de la venta asociada, de los artículos devueltos), solo en la
  NCX asociada: una sin comprobante asociado no la paga, y una asociada a una venta sin combos la paga y recibe el
  resultado vacío. La nota de crédito no tiene un techo de consultas fijado en `VentasCheckoutTests`.

##### Vista previa del carrito (`POST /api/ofertas/resolver-con-combos`)

El POS en línea muestra el mismo total que el servidor cobrará. Recibe el mismo cuerpo que `POST /api/ofertas/resolver`
(`SolicitudDeResolucion`) pero responde un objeto y no un arreglo, así que vive en su propia ruta: cambiar la forma de
`/resolver` rompería a los POS ya instalados, que siguen recibiendo el arreglo de siempre sin ninguna clave nueva
(`combo` se omite cuando es `null`). Misma policy que `/resolver` (`OperacionDePos`); no escribe nada.

```jsonc
{
  "lineas": [   // en el orden de la solicitud; cada una es un ResultadoDeResolucion de siempre
    { "idArticulo": 1, "idListaPrecio": 1, "precioOriginal": 3000, "precioFinal": 3000, "descuentoUnitario": 0,
      "aplicadas": [],
      "combo": { "unidadesConsumidas": 1, "ahorro": 272.73, "idCombo": 7 } }  // solo si un combo consumió unidades
  ],
  "combos": [   // combos aplicados al documento, ascendentes por idCombo; [] si ninguno
    { "idCombo": 7, "codigo": 1, "nombre": "Pancho + coca", "veces": 1, "ahorro": 500 }
  ]
}
```

- `combo.unidadesConsumidas` son las unidades de la línea que entraron a un combo; `combo.ahorro` es la parte del combo como
  IMPORTE DE LÍNEA (no unitario) y `combo.idCombo` el combo que la venta guardará en `id_combo` (el de menor id entre los
  que consumieron la línea). El neto de la línea es `round(cantidad × precioOriginal) − (round(descuentoUnitario ×
  (cantidad − unidadesConsumidas)) + ahorro)`, la fórmula del checkout, y el del documento la suma de las líneas.
  `combos[].ahorro` es el ahorro de una aplicación (importe de lista de las unidades consumidas menos el precio del combo)
  por `veces`; coincide con la suma de lo repartido a las líneas por ese combo porque `ResolvedorDeCombos` reparte el ahorro
  de cada aplicación entre las líneas para que sume exacto (cada conjunto completo cobra el precio del combo) y multiplica
  cada parte por las veces. `combo.ahorro` de una línea suma las partes de todos los combos que la consumieron. `veces` es
  cuántos conjuntos completó.
- Es el mismo código que la venta online: ambos llaman a `CombosEnVenta.DetectarAsync`, que arma las líneas a partir de la
  resolución de ofertas y carga los candidatos con `ServicioDeCombos.CargarCandidatosParaVentaAsync`. Mismas exclusiones
  (líneas sin precio vigente, descuento de oferta negativo), mismo mínimo (dos componentes, o uno solo con cantidad
  de 2 o más), mismo combo de menor id por línea. La paridad la fija `CombosPrevisualizacionTests`: el neto por línea y el total
  que calcula la vista previa son los que `POST /api/ventas` persiste para varios carritos.
- `agregacion: PorLinea` no es un documento y nunca devuelve combos (`lineas` como en `/resolver`, `combos: []`); un valor de
  agregación inválido da `400 agregacion_invalida`.
- Empresa y lista (REQUISITO del contrato): la vista previa toma la empresa y la lista de lo que el cliente ENVÍA en cada
  línea, mientras que la venta usa la empresa del punto de venta y la lista del cliente. Para que la vista previa sea el
  total de la venta, el cliente envía en TODAS las líneas la `idEmpresa` del punto de venta y la `idListaPrecio` del cliente.
  Si omite la `idEmpresa`, solo participan los combos de todo el tenant y un combo propio de la empresa no aparece en la
  vista previa aunque la venta lo aplique (lo fija `CombosPrevisualizacionTests.SiElClienteOmiteLaEmpresaLaVistaPreviaNoMuestraUnComboPropioQueLaVentaSiAplica`).
  El servidor solo rechaza lo que es incoherente dentro del documento: líneas con empresas distintas (incluida la mezcla
  de con y sin empresa) dan `400 empresa_inconsistente` y líneas con listas distintas dan `400 lista_inconsistente`;
  `/resolver` no rechaza ninguna mezcla porque filtra las ofertas línea por línea, y los combos se detectan sobre el
  documento entero. Que los valores enviados sean los del punto de venta y el cliente no lo verifica el servidor.
- Costo: las 5 consultas de la resolución más UNA de candidatos de combos, que se omite solo si todas las líneas que entran a combos (las de un artículo por importe no cuentan) son de unidad `Unidad` y
  suman menos de 2 (6 y 5 respectivamente, fijado en `CombosConsultasTests`). Los combos aplican en un documento de signo positivo; la vista previa no sabe de notas de crédito.

##### Combos en el POS offline

**Instantánea (`GET /api/pos/instantanea?version=2`).** Trae `combos[]`: los combos vivos y activos del punto de venta (de su
empresa o de todo el tenant) que el dispositivo puede completar con los artículos de esa misma instantánea, ascendentes por
`idCombo`. Cada uno es `{ idCombo, codigo, nombre, precio, componentes[] }` y cada componente `{ idArticulo | idFamilia,
cantidad }` (viaja exactamente uno de los dos ids, el otro se omite), con los artículos primero y las familias después, cada
grupo ascendente por id. El componente familia se empareja con `articulos[].idFamilia`, que la instantánea ya trae. La clave
`combos` se omite (no se manda `[]`) si no hay ninguno: una instantánea sin combos y su ETag son los de antes de la
funcionalidad. El ETag es el hash del contenido serializado, así que cambia cuando cambia un combo, su precio o un componente
(o un artículo que deja un combo sin poder completarse), y no cambia por nada que no figure en la respuesta (tocar
`updated_at`, por ejemplo). El formato original (sin `version=2`) no lleva combos. Cuesta UNA consulta más, solo en `version=2`
(`CombosConsultasTests` cuenta las consultas a `combos`: 1 en `version=2`, 0 en el formato original).

Un combo viaja ENTERO o no viaja (`ArmadorDeInstantanea.ArmarCombos`): uno parcial haría creer al dispositivo que puede
armarlo. Se descarta si algún componente no se puede vender con esta instantánea: un componente artículo tiene que figurar
en ella (activo y con precio en alguna lista; un artículo dado de baja ya no figura) y su unidad de venta tiene que admitir la
cantidad del componente (entera para `Unidad`, hasta 3 decimales para `Peso`, como en la venta online; un artículo por
`Importe`, que figura en la instantánea sin precios, nunca es un componente vendible); un componente
familia necesita al menos un artículo vendible de esa familia en la instantánea cuya unidad admita la cantidad (la misma
regla de emparejamiento que la venta online, que compara la familia del artículo; una familia dada de baja deja de contar
cuando sus artículos dejan de figurar). Es la
lectura de la regla de `dangling-fk-read-models` para este caso: una referencia que apunta a algo no vendible se trata igual
que una ausente, y como el motor del dispositivo necesita el conjunto completo, la ausencia descarta el combo y no solo el
componente.

**Venta offline (`POST /api/ventas` con `NumeroPreasignado`).** `LineaDeVenta` suma tres campos opcionales, válidos solo
junto al precio offline de la línea: `unidadesConsumidasPorCombo` (entero por unidad, hasta 3 decimales por peso, nunca en un artículo por importe), `ahorroDeCombo` (importe de línea) e `idCombo`. El
servidor los graba tal cual: el descuento de la línea es `round(descuentoUnitario × (cantidad − unidadesConsumidasPorCombo)) +
ahorroDeCombo` (`CalculadorDeTotales`) y el combo queda en `items_comprobante_venta.id_combo`; una línea con todas sus
unidades en el combo no lleva `id_oferta`, una con unidades fuera del combo conserva la oferta. Validación
(`ExigirPreciosOfflineValidos`): sin `precioUnitario` en la línea `400 combo_offline_no_admitido`; los tres o ninguno
(`400 combo_offline_incompleto`); unidades mayores a cero, hasta la cantidad de la línea y admitidas por la unidad de venta del artículo (ninguna si se vende por importe), ahorro entre 0 y el bruto de las unidades
consumidas con a lo sumo dos decimales, e id positivo (`400 combo_offline_invalido`); una nota de crédito no admite combos
(`400 combo_offline_no_admitido`). El camino online
nunca los admite: sin precio offline son siempre un rechazo.

- **Id de combo inexistente.** La venta ya ocurrió y el ticket está en manos del cliente: nunca se la deja varada. Si el id no
  existe en el tenant (la FK lo rechazaría), se conservan los importes, `id_combo` queda en `null` y la línea se audita con
  `combo_cobrado.inexistente = true`. Un combo dado de baja después de que el dispositivo lo sincronizó SÍ existe (la baja es
  lógica): la venta conserva su `id_combo`. Cuesta UNA consulta, solo si alguna línea trae `idCombo`.
- **Auditoría.** El servidor detecta combos sobre el carrito offline con su propio motor, SOLO para comparar (los importes del
  dispositivo siguen siendo los que se graban): `NetoDeLinea` ahora calcula el neto esperado con `CalculadorDeTotales` incluyendo
  la parte del combo, de modo que un combo bien calculado no es una discrepancia y uno mal calculado, o ausente, sí (el
  dispositivo no lo detectó, la parte no coincide, o el combo ya no existe o está inactivo para el servidor). Cada item auditado
  en `venta.discrepancia` suma `combo_cobrado` y `combo_esperado` (`{ unidades, ahorro, id_combo, inexistente }`, `null` si no hay).
  Se compara el neto, no el id: dos combos que dan el mismo neto no son una discrepancia. Cuesta UNA consulta de candidatos más
  en la venta offline, salvo que todas las líneas que entran a combos sean de unidad `Unidad` y sumen menos de 2 (la misma de la venta online). `CombosConsultasTests` fija el costo de la venta
  offline: sin datos de combo, 19 consultas con una unidad y 20 con dos (las 16 de la venta online de una línea más 3 propias
  del camino de dispositivo con número pre-asignado, y los candidatos); con datos de combo en un carrito de dos unidades, 21
  (los candidatos y la lectura de ids).
- **Despliegue.** Un dispositivo que no aplica combos (una versión vieja del POS o una instantánea vieja) y vende un
  carrito que coincide con un combo activo queda registrado por lo que cobró, sin combo, y se audita como discrepante
  (`PrecioDiscrepante = true`, `combo_cobrado = null`). Es lo esperado hasta que el dispositivo se actualice; no es un error
  de la venta ni la bloquea.
- **Dispositivo (POS web y de escritorio).** El dispositivo porta el mismo motor a TypeScript (`pos/motorDeCombos.ts`, con
  los casos de `ResolvedorDeCombosTests` espejados) y lo aplica sobre `combos[]` de la instantánea DESPUÉS de resolver el
  precio vigente de cada línea, con las exclusiones del servidor: cualquier artículo por unidad o por peso, con una cantidad que admita su unidad de venta (una unidad ausente en una
  instantánea vieja cuenta como pesable), sin las líneas por importe y sin las líneas sin precio. La vista previa, el payload
  encolado y el ticket sintético salen de una única función (`resolverDocumentoOffline`); el payload se arma al encolar y se
  reenvía literal, nunca se recalcula al drenar. Una instantánea guardada sin `combos` sigue siendo válida; una con `combos`
  malformado también lo es: la instantánea no se descarta, se sanea al adoptarla o leerla (cada combo malformado se descarta,
  un `combos` que no es una lista cuenta como ausente, y `codigo`, un rótulo que el motor no lee, puede faltar). Si la parte de
  combo de alguna línea tuviera más de dos decimales (solo con precios de fracción de centavo), el servidor la rechazaría con
  `combo_offline_invalido`: el dispositivo cotiza, encola e imprime ese documento sin combos.
- **Reenvío idempotente.** Los tres campos NO forman parte del "mismo contenido" de `ExigirMismoContenido`, igual que el precio y
  el descuento de la línea: son dinero que calculó el dispositivo, no la identidad de lo vendido. Un reenvío con otra parte de
  combo bajo el mismo número devuelve el comprobante ya emitido sin pisarlo, y no duplica la auditoría. Todo se decide antes de
  la transacción; nada se calcula dentro de la lambda reintentable.

Un artículo o una familia que un combo referencia no se puede borrar (`Restrict`); borrar físicamente el combo borra
sus componentes (`Cascade`). La baja de negocio del combo es lógica (`deleted_at`) y borra físicamente sus componentes
en la misma transacción: la guarda de referencias cuenta cualquier fila que exista, así que un combo dado de baja no
bloquea la baja de las familias que usaba.

#### API (`/api/combos`)

Lecturas con la policy de `/api/ofertas` (`OperacionDePos`); escrituras con `GestionDeCatalogo` (Admin).

- `GET /api/combos` lista los combos vivos ordenados por `codigo`; `GET /api/combos/{id}` devuelve uno. Cada combo trae
  `{ id, codigo, nombre, precio, idEmpresa, activo, componentes[] }` y cada componente
  `{ idArticulo, idFamilia, nombre, cantidad, disponible }`. Si el artículo o la familia de un componente fue dado de baja
  después, el componente sigue en el combo con `nombre = null` y `disponible = false` (el id se conserva para poder
  quitarlo); un PUT que lo repita se rechaza con `referencia_invalida`.
- `POST` y `PUT /api/combos/{id}` reciben `{ codigo?, nombre, precio, idEmpresa?, activo, componentes[] }` con
  componentes `{ idArticulo?, idFamilia?, cantidad }`. El PUT reemplaza todo, incluido el conjunto de componentes, en una
  transacción. `activo` omitido en el alta vale `true`; en el PUT es obligatorio (`400 activo_requerido`), para que
  omitirlo no reactive un combo inactivo en silencio. `codigo` omitido: en el alta el servidor asigna el máximo `codigo` vivo del
  tenant + 1 (los combos dados de baja no cuentan, así que su código se reutiliza); en el PUT se conserva el actual.
  `codigo` informado tiene que estar entre 1 y 999.999 (`ReglaDeCombos.CodigoMaximo`). `DELETE` da la baja lógica.
- Un PUT o DELETE sobre un id inexistente, dado de baja o de otro tenant responde `404` antes de validar el cuerpo.
- Errores `400`: los de `ReglaDeCombos` (`combo_precio_invalido`, `combo_componente_invalido`, `combo_cantidad_invalida`,
  `combo_componente_duplicado`, `combo_unidades_insuficientes`, `combo_componentes_excedidos`), `activo_requerido`
  (solo PUT), `combo_codigo_invalido`, `nombre_requerido`,
  `nombre_muy_largo` (150), `referencia_invalida` (artículo, familia o empresa que no existe, está dada de baja o es de
  otro tenant), `combo_componente_por_importe` (un componente es un artículo que se vende por importe, o una familia con algún
  miembro vivo que se vende por importe; va antes que el siguiente y no depende de la cantidad) y `combo_cantidad_no_admitida` (la cantidad de un componente no la admite la unidad de venta del artículo, o la de
  algún miembro vivo de la familia; los servicios, `es_producto = false`, sí se aceptan). `409`: `combo_codigo_duplicado` (código en
  uso por otro combo vivo) y `combo_codigo_agotado` (alta sin código con el 999.999 ya ocupado).
- Toda escritura toma un lock advisory por tenant como primera sentencia de su transacción: serializa la asignación del
  código, el pre-chequeo de código y el reemplazo del conjunto. Cada artículo o familia referenciado se toma `FOR KEY
  SHARE` sobre su fila viva en esa transacción, de modo que una baja que ya tiene la fila hace fallar el alta o la
  edición con `referencia_invalida` en vez de dejar un componente colgante. Que la cantidad sea admitida por la unidad de venta se comprueba
  en ese momento y solo en ese momento: cambiar después la unidad de un artículo componente, o sumar a una familia que ya
  usa un combo un artículo que no admite su cantidad, no se bloquea (en la venta ese miembro deja de ser candidato). La ruta de venta decide si una línea es candidata por la unidad de venta
  del artículo, no por la forma de la cantidad.
- Respaldos de esquema (`ManejadorDeErrores`): `ux_combos_codigo` → `409 combo_codigo_duplicado`;
  `ux_combos_componentes_articulo` y `ux_combos_componentes_familia` → `409 combo_componente_duplicado`. Solo una
  escritura fuera de banda las alcanza, porque el lock por tenant serializa las del servicio.
- Disolver una familia que es componente de un combo vivo responde `409 familia_en_uso` ("tiene combos"). La baja de un
  artículo que es componente de un combo vivo responde `409 articulo_en_uso` ("tiene combos"); la de un combo dado de
  baja no bloquea.

El back office tiene su pantalla en `/combos` (solo Admin, entrada "Combos" del grupo Catálogo): lista los combos vivos,
también los inactivos, con el código como `C<n>` (lo que se tipea en la caja), el precio y los componentes, y marca el que
tiene un componente con `disponible = false`. El formulario valida en el cliente, para que los errores comunes no viajen:
nombre obligatorio (150), precio mayor a cero con hasta dos decimales, código entero entre 1 y 999.999 si se informa,
cantidades de 1 a 999.999 (enteras para un artículo por unidad, hasta 3 decimales para uno pesable), al menos dos componentes (o uno solo con cantidad de 2 o más), hasta 50 componentes, ningún artículo ni familia repetidos
y ningún componente `disponible = false` (hay que quitarlo o reemplazarlo antes de guardar). El buscador de artículos
ofrece los artículos activos y a la venta que se venden por unidad o por peso, los pesables rotulados (los que se venden por importe no se ofrecen); una cantidad que la unidad de venta de un artículo, o de algún
miembro de una familia, no admite solo la rechaza el servidor (`combo_cantidad_no_admitida`), que sigue siendo la autoridad: cada código de error de arriba se traduce a un mensaje junto
al campo o a la sección de componentes que corresponde.

#### Cargar un combo en el POS

Un combo nunca es una línea de la venta: cargarlo en el carrito agrega las MISMAS líneas de artículo que se agregarían
escaneándolas por separado, por el camino del escaneo (`acumulaEnVenta`, reglas de cantidad y resolución de precios
idénticas), y es el carrito el que después detecta el combo por sí mismo. Hay dos entradas:

- El botón "Buscar combo" (atajo F4, junto a "Buscar artículo" F2): un modal con los combos activos del punto de venta
  (`C<codigo>`, nombre, precio, resumen de componentes), filtro por nombre o código, flechas y Enter. Un combo con un
  componente sin artículos vendibles se muestra deshabilitado con el motivo. El atajo respeta las mismas compuertas que el
  botón (no abre con un escaneo o una lectura de combo en vuelo, con un cobro en curso ni sobre otro modal) y es la única
  tecla cuyo comportamiento por defecto del navegador se evita. Por qué F4 y no F3: el legacy usa F1 (buscar cliente), F2
  (insertar código), F3 (dirección de entrega), F9, F10 y F12 (abrir caja), y `docs/05` obliga a conservarlos; F4-F8 y F11
  eran un `alert` sin implementar. F3 queda para la dirección de entrega y F4 es la primera tecla libre tanto en el legacy
  como en esta pantalla.
- Escribir `C` + dígitos en el input de código. Solo el texto canónico `^[cC]([1-9]\d{0,5})$` (de 1 a 999999, sin ceros a la
  izquierda) toma este camino; cualquier otra (`C0`, `C012`, `C001`, `C1000000`, `C12X`, `3*C7`) va directo a la búsqueda de
  artículos y no hace ninguna lectura de combos: un artículo codificado con relleno de ceros no queda tapado por un combo.
  Se busca primero un combo activo con ese número (D7); si no hay, la búsqueda de artículos sigue con el texto ORIGINAL tal
  como se tipeó, porque el `codigo_interno` de un artículo es libre y puede ser `C12`. La caja es local-first: un escaneo
  nunca espera a la red si la instantánea puede responder. Con una instantánea utilizable y conexión el orden es (1) un
  combo con ese número en la instantánea: se carga, sin ninguna llamada; (2) si no, un ARTÍCULO que la instantánea resuelve
  con el texto tipeado (la misma búsqueda local de un escaneo común): se agrega, sin ninguna consulta de combos; (3) si la
  instantánea no conoce ni uno ni otro, se le pregunta a la API por un combo con ese número y después sigue la búsqueda de
  artículos de siempre. Consecuencia del paso (2): si un combo con ese número existe solo en el servidor (la instantánea es
  anterior a su alta) y la instantánea tiene un artículo con ese mismo código, gana el artículo hasta la próxima
  sincronización. Ese sondeo está acotado: vence a los 5 s (`TIEMPO_LIMITE_DE_RED_MS`) y cuenta como lectura fallida, y
  tras no encontrar un combo con un código no se vuelve a preguntar por ese mismo código durante 60 s (se olvida al abrir
  "Buscar combo"). Sin conexión solo cuenta la instantánea y no hay ninguna llamada. Sin instantánea (web) el orden es la
  lista de combos de la API (guardada en memoria) y después la búsqueda de artículos. Un combo guardado que figura como no
  cargable se vuelve a leer una vez antes de rechazarlo, y un detalle que responde 404 (el combo se dio de baja) se olvida y
  sigue como «no hay combo con ese número». Solo la búsqueda por código está protegida así: si falla (red, 500, tiempo
  agotado) la búsqueda de artículos también sigue, pero si el artículo tampoco aparece el aviso agrega «No se pudieron leer
  los combos.». Un combo que existe y no se puede leer o cargar (motivo propio, detalle que falla, cobro que empezó mientras
  tanto) avisa el motivo y NO cae a artículos; cada camino en que el combo se descarta deja un aviso, nunca un descarte
  silencioso. El atajo F4 se ignora con Alt, Ctrl o Meta apretadas (Alt+F4 cierra la ventana).

Todo o nada: el combo entra entero o no entra. Una familia con un solo candidato se agrega directo; con varios, un único
selector reparte la cantidad de cada componente de familia entre sus miembros y "Agregar combo" queda deshabilitado hasta
que cada total sea exactamente la cantidad del componente. Cancelar (botón, Escape o ×) no agrega ni una línea, tampoco las
de los componentes que no necesitaban elegir.

Fuente de datos: con una instantánea utilizable del punto de venta (POS de escritorio) la lista, la búsqueda por código y
los candidatos salen de la instantánea, también sin conexión: un candidato es un artículo cuya unidad de venta admite la cantidad del componente (una unidad ausente es
`Peso`; un artículo por `Importe` nunca lo es) con precio en la lista del cliente, y los miembros de una familia se buscan por el `idFamilia` de cada artículo.
Sin instantánea (web) salen de la API: `GET /api/combos` para la lista y el endpoint de abajo para los candidatos.

La instantánea no es la última palabra cuando hay conexión. Un combo creado después de la última sincronización no está en
ella, así que con conexión una búsqueda por código `C<n>` que la instantánea no resuelve (o una instantánea sin la clave
`combos`) prueba la API para ese código antes de caer a la búsqueda de artículos. Sin conexión el comportamiento no cambia:
solo la instantánea, cero pedidos. El modal no mezcla fuentes: lista los combos de la instantánea cuando esta trae la
clave `combos` (aunque sea vacía) y los de la API cuando con conexión la instantánea no la trae. La fuente de la API
guarda su lista en memoria mientras la pantalla esté montada: abrir el buscador siempre la vuelve a pedir y una búsqueda
por código que no la encuentra en lo guardado la refresca una sola vez antes de decidir; un escaneo común (que no tiene
forma de código de combo) no pide nada.

Las dos fuentes pueden rotular distinto una misma familia: sin API (instantánea) la etiqueta del componente se arma con los
nombres de sus primeros miembros, y con la API es el nombre de la familia. Tampoco coincide el filtro de precio: con la
instantánea un candidato necesita precio en la lista del cliente (la misma regla con que el carrito offline cotiza), y con
la API el servidor no mira el precio y lo resuelve el carrito, igual que al escanear el artículo suelto.

`GET /api/combos/{id}/cargable?idEmpresa=` (`OperacionDePos`, como las demás lecturas; es un GET, no entra en la lista de
excepciones de `SuperficieDeAutorizacionTests`): devuelve `{ id, codigo, nombre, precio, componentes[] }` con cada
componente `{ idArticulo, idFamilia, nombre, cantidad, candidatos[] }` en el orden en que se armó el combo. Un candidato
tiene la forma del escaneo (`ArticuloEscaneado` con `cantidad = 1` y sin código de barra): para un componente de artículo,
ese artículo; para uno de familia, sus miembros vivos, activos y disponibles a la venta cuya unidad de venta admite la cantidad del componente, ordenados por nombre. Es lo que el
escaneo resuelve (artículo vivo, activo y disponible a la venta) más esa unidad, que además tiene que entrar a combos (un artículo que pasó a venderse por importe deja de ser candidato); no mira el precio, que se resuelve aparte como en
cualquier línea. Un componente colgante (artículo o familia dada de baja, artículo inactivo o cuya unidad no admite la cantidad, familia sin miembros
vendibles) vuelve con `candidatos = []` y el cliente muestra el combo como no cargable. `404 no_encontrado` para un id
inexistente, dado de baja, inactivo, de otro tenant o de una empresa distinta de `idEmpresa` (sin `idEmpresa` solo vale un
combo de todo el tenant, igual que en la venta). Tres consultas sin importar la cantidad de componentes o miembros.

---
