# 01 — Catálogo de features existentes

> Todo lo que el sistema hace hoy, con las reglas de negocio tal como están implementadas
> (incluidos los bugs, marcados con ⚠). Este documento es el contrato de "paridad funcional":
> la app nueva tiene que cubrir esta lista antes de agregar nada.

---

## A. Acceso

### A1. Login
- **Ruta:** `index.php?menu=login`
- Combo con todos los usuarios `tipoUser IN (2,3,4)` ordenados por `user` (no por nombre) + campo password.
- Valida contra `usuarios.pass` en texto plano.
- Mensajes: usuario inexistente / contraseña incorrecta / sin locales habilitados.
- Si el usuario tiene 1 punto de venta entra directo a Ventas; si tiene varios, va a A2.

### A2. Selección de punto de venta
- **Ruta:** `index.php?menu=elegirLocal`
- Combo con los locales asignados vía `usuario_rol_puntoventa`.
- El punto de venta elegido queda en sesión y **tiñe toda la UI** (clase CSS `color_1` naranja / `color_2` violeta).

### A3. Logout
- `logout.php` destruye la sesión.

---

## B. Facturación → Ventas / Devoluciones (el POS)

**Ruta:** `index.php?menu=facturacion&opc=ventas`
**Archivo:** `facturacion.php` líneas 1–760

Es la pantalla principal. Layout: barra de totales arriba, fila de carga, líneas del ticket
debajo (orden inverso, la última cargada primero) y un display gigante con el total.

### B1. Carga de artículos
Tres formas de agregar una línea:
1. **Escaneo / tipeo** en el campo `busqueda` + Enter (`accion=Cargar`).
2. **Búsqueda por nombre** (modal F1 `#insertarCodigo`) → link `&agregar=<barra>`.
3. **Teclas rápidas** con IDs hardcodeados (ver B7).

Reglas de resolución del código:
- `strlen(codigo) < 7` → busca por `articulos.ID` (código interno corto).
- `strlen(codigo) >= 7` → busca por `codigos_barra.codigo` (EAN).
- Siempre filtra `activo = 1`.
- Sintaxis `<cantidad>*<codigo>` en el campo de búsqueda: `3*7790001` carga 3 unidades.
- `**` se traduce al código especial `999999999` (artículo "descuento").
- Si el artículo ya está en el ticket, **suma cantidades** y recalcula el total de la línea.
- Cantidad vacía o `0` → `1.00`.

Estado en sesión por línea:
`{mostrarID, barra, cantidad, descripcion, precio, total, id_area, grupo}`

Además mantiene `$_SESSION['grupo'][id_grupo] = {cantidad, importe}` para las ofertas de grupo.

### B2. Motor de ofertas (`funciones.php`)

**`comprobarOferta($barra, $cantidad)`** — ofertas a nivel artículo. Se evalúan en cascada:

| Tipo | Campo activador | Condición | Descuento |
|---|---|---|---|
| Por horario | `OfertaHora=1` | hora actual entre `OfertaHoraDesde` y `OfertaHoraHasta` | actúa como *gate*: si está fuera de rango corta todas las demás (`$break=TRUE`) |
| Por fecha | `OfertaDia=1` | fecha actual entre `OfertaDiaDesde` y `OfertaDiaHasta` | `(precio − precioOferta) × cantidad`, en negativo |
| Por cantidad | `OfertaCant=1` | `cantidad >= OfertaCantN` | `(precio − precioCant) × OfertaCantN × floor(cantidad / OfertaCantN)` |

- La oferta por cantidad también se evalúa en negativo (`$cantidad2 = cantidad * -1`) para **devoluciones**.
- ⚠ Redondeo raro: los centavos del descuento se fuerzan a `.00` salvo que sean exactamente `.50`.
  Esto solo aplica al bloque de **Oferta por Cantidad** (`funciones.php:41-45,59-63`); la
  **Oferta por Fecha** usa `number_format` normal, sin este forzado.
- El descuento entra como una **línea propia** del ticket con barra `OF<codigo>`, cantidad `-`, precio `-`.
- Etiqueta: `articulos.nombreOferta`, o `"DESCUENTO POR CANTIDAD"` si está vacío.

**`comprobarOfertaGrupo($id_grupo, $acumulado)`** — ofertas a nivel grupo:

| Tipo | Campo | Regla |
|---|---|---|
| Restricción horaria | `grupos.horas=1` + `hDesde`/`hHasta` | gate |
| Restricción de fechas | `grupos.dias=1` + `dDesde`/`dHasta` | gate |
| Por cantidad | `ofertaCantidad=1` | si el acumulado del grupo `>= grupos.cantidad`, descuenta `(unitario × cantidad − precio) × floor(acumulado/cantidad)`, redondeado con `ceil()` |
| Directa | `ofertaDirecta=1` | `importe_acumulado × descuento% `, redondeado con `ceil()` |

- Línea de descuento con barra `OF<id_grupo>`, `id_area` forzada a `1`.
- ⚠ `ofertaCantidad` y `ofertaDirecta` son mutuamente excluyentes (`elseif`).

### B3. Eliminar línea
`accion=Eliminar` + `barra`. Tres casos:
- Línea normal: descuenta del total, resta del acumulado del grupo, borra la línea de oferta
  asociada `OF<barra>` si existe, y **recalcula** la oferta de grupo.
- Línea `OF...`: solo descuenta del acumulado de descuentos.
- Línea `COMBO...`: idem (feature muerto).

### B4. Cliente y domicilio
- Por defecto el cliente es el ID `1` = **Consumidor Final** (acuerdo y saldo en `0.00`).
- Modal F1 `#buscarCliente` → `&cambiarUser=<id>` carga en sesión: id, `NNNN - Nombre Apellido`,
  domicilio, teléfono (prefiere celular), acuerdo y saldo.
- Con cliente distinto de CF aparece una barra superior con sus datos y se **habilita el pago en cuenta corriente**.
- Modal F3 `#direccion` → `&cargarDireccion=` guarda un domicilio de entrega en mayúsculas,
  que se imprime en el ticket y dispara la **comanda** (ver B8).

### B5. Tickets en espera
- Botón 💾 guarda el ticket completo (líneas, cliente, total, descuento, tipo, vuelto, grupo, dirección)
  en `$_SESSION['guardado'][1..3]` y limpia la venta en curso.
- Máximo **3 slots**. Con los 3 ocupados el botón se deshabilita.
- Botones flotantes a la derecha para recuperar cada slot.
- Si hay un ticket en curso al recuperar, hace **swap** (el actual pasa al slot); si no, recupera y libera el slot.
- ⚠ Todo esto está triplicado literalmente en el código (≈250 líneas duplicadas).
- ⚠ Se pierde al vencer la sesión PHP.
- ⚠ **Asimetría del slot 3:** en `facturacion.php:97`, al guardar en el slot 3 el código hace
  `$_SESSION['guardado'][3]['direccion']=$_SESSION['direccion']="";` — borra el domicilio de
  entrega **antes** de copiarlo al slot, a diferencia de los slots 1 y 2 (que preservan el valor
  con `?? ""`). El domicilio siempre se pierde al usar el slot 3.

### B6. Cierre de la venta (F9 → F9)

**Paso 1 — `accion=Siguiente (F9)`:** muestra los cuatro campos de pago.
- Efectivo (editable, con foco).
- Tarjetas (readonly; clic simple = autocompletar el resto, doble clic = editar a mano).
- Cuenta corriente (solo habilitado si el cliente ≠ CF; clic = autocompletar el resto).
- Vuelto (calculado por JS: `efectivo + tarjetas + c_corriente − total`).
- Si el total es `<= 0` (devolución pura) los campos van readonly en `-`.

**Paso 2 — `accion=Finalizar (F9)`:** validaciones, en orden:

| # | Condición de rechazo | Mensaje |
|---|---|---|
| 1 | Todos los medios en 0 y total > 0 | "No se ingreso el pago!!" |
| 2 | `efectivo+tarjetas+c_corriente+10 < total` | "…tolerancia máxima es de $10.00…" (⚠ tolerancia fija hardcodeada) |
| 3 | `saldo > 20` | "El vuelto no puede ser mayor a 20.00" (⚠ límite hardcodeado) |
| 4 | `tarjetas > 0 && vuelto > 0` | no se da vuelto si pagó con tarjeta |
| 5 | `c_corriente > 0 && vuelto > 0` | no se da vuelto si pagó con cuenta corriente |
| 6 | `c_corriente + saldo_cliente > acuerdo_cliente` (y `acuerdo != -1`) | excede el crédito. `acuerdo = -1` = crédito ilimitado |

Si pasa todo:
1. Recorre las líneas y arma el string `articulos` (`barra/cant/desc/precio/total*…`).
2. Acumula el importe por área en `c1..c6` (1=N/A, 2=Almacén, 3=Verdulería, 4=Cigarrillos, 5=Carga Virtual, 6=Rotisería).
3. **Descuenta stock**: un `UPDATE articulos SET existencia = existencia − cantidad WHERE barra = ...` por línea.
   - ⚠ Sin transacción, sin verificar disponibilidad, y matcheando por `barra` (no por ID).
4. `INSERT INTO ventas` con `tipo = 1` si total ≥ 0, `tipo = 2` si es devolución.
5. Si hubo cuenta corriente: `UPDATE usuarios SET saldo = saldo + c_corriente`.
6. Numera el ticket como `NNNN - NNNNNNNN` (punto de venta + id de venta).
7. Abre el popup de impresión (`ticket()`).

**Cálculo del saldo/diferencia:** si el vuelto declarado por el cajero no coincide con el
calculado, la diferencia se guarda en `ventas.saldo` (positiva = sobrante, negativa = faltante).

### B7. Atajos de teclado (definidos en `index.php`)

| Tecla | Acción |
|---|---|
| `F1` | modal Buscar Cliente |
| `F2` | modal Insertar Código (buscar artículo por nombre) |
| `F3` | modal Dirección de entrega |
| `F4`–`F8` | `alert("Pulsaste Fn")` — ⚠ sin implementar |
| `F9` | submit del formulario "siguiente" |
| `F10` | descartar ticket (con confirmación) / volver |
| `F11` | `alert` — ⚠ sin implementar |
| `F12` | modal Abrir Caja (pide motivo, mínimo 5 caracteres, imprime y recarga) |
| `+` (107) | foco en Cantidad |
| `−` (109) | foco en Búsqueda |
| `PgUp`/`PgDn`/`End`/`Home`/`Ins`/`Del` | cargan los artículos 711, 688, 1337, 710, 709, 697 — ⚠ IDs hardcodeados |

### B8. Impresión

| Archivo | Qué imprime |
|---|---|
| `ticket.php` | Ticket de la venta recién cerrada. Cabecera con local, dirección y horario; líneas; total; pie con redes; **vuelto** en overlay (se oculta al imprimir). Al terminar navega a `ticketOk.php` y cierra la ventana. |
| `reTicket.php` | Reimpresión de un ticket ya guardado, por ID. |
| `ticketCC.php` | Comprobante de cierre de caja. |
| `ticketRetiro.php` | Comprobante de retiro de efectivo. |
| `imprimirArticulos.php` | Lista de reposición (sin stock / bajo mínimo) por proveedor. ⚠ Abre su propia conexión hardcodeada `mysqli_connect('127.0.0.1','root','','ways')` en vez de usar `conexion.php` — mismo patrón que `combos.php` — por lo que probablemente falla en el hosting de producción. |

- **Comanda de rotisería:** si alguna línea tiene `id_area == 8`, se imprime un bloque extra
  "COMANDA" con ticket, domicilio, hora y los ítems. ⚠ `areas` solo llega hasta el id 6 —
  esta rama está muerta con los datos actuales.
- Formato del precio en la línea: si `barra < 10000` imprime `precio x cantidad`, si no `cantidad x $precio`.
- Ventana popup 300×400, dispara `window.print()` al perder el foco o con F9/Esc.

---

## C. Facturación → Compras / Pagos (Gastos)

**Ruta:** `index.php?menu=facturacion&opc=gastos`

### C1. Alta rápida de gasto (la que se usa)
Formulario con: fecha, número de factura (`9999-99999999`), concepto, detalle, importe, área.

Concepto es un `<select>` con dos grupos:
- **Otros:** `99` Otros, `98` Sueldos, `97` Viáticos, `96` Impuestos
- **Proveedores:** un ítem por fila de `proveedores` (el `tipo` guardado es el `id` del proveedor)

Reglas:
- Importe `0` → rechazado.
- Área vacía → `99` (⚠ ese id no existe en `areas`, la FK lo rechazaría).
- El número de factura se guarda sin guión, como `bigint`.
- Se graba con `cerrada = 0` y el punto de venta de la sesión.

Panel derecho: gastos de la caja abierta con totales separados de **Gastos** (`tipo <> 95`) y
**Retiros** (`tipo = 95`), y borrado por fila.

### C2. Borrado de gasto
`&eliminar=<id>` — solo si `cerrada = 0`. Si la caja ya cerró, rechaza.

### C3. Alta de compra con detalle (`&accion=nuevo`)
Pantalla más ambiciosa: proveedor, factura, fecha, observaciones y carga línea por línea
(código, unidades, bultos, descripción, precio, descuento, total), con cálculo
`cantidad = unidades + bultos × uBulto`.
⚠ **Nunca persiste.** Acumula en `$_SESSION['compra']` y no hay INSERT. Además la query de
artículos usa la columna `proveedor` que ya no existe. Feature incompleto.

### C4. Tipos de gasto

| `tipo` | Significado |
|---|---|
| `< 90` | id del proveedor |
| `95` | Retiro de efectivo |
| `96` | Impuestos |
| `97` | Viáticos |
| `98` | Sueldos |
| `99` | Otros |

---

## D. Facturación → Tickets / Caja

**Ruta:** `index.php?menu=facturacion&opc=caja`

### D1. Listado de tickets sin cerrar
Tickets de la caja abierta (`cerrada = 0`, `tipo IN (1,2,3)`) del punto de venta actual.
Columnas: ticket, fecha, cliente, total, efectivo, tarjetas, cuenta, vuelto, saldo (coloreado)
y acciones: reimprimir, editar, reasignar cliente, anular/restaurar.
Los anulados se muestran con fondo naranja.

### D2. Anular ticket (`&eliminar=`)
- Bloqueado si `cerrada = 1`.
- Parsea `ventas.articulos`, y por cada línea que **no** empiece con `OF`,
  devuelve la cantidad al stock (`existencia = existencia + cantidad`).
- Marca `eliminado = 1`.
- Si el cliente ≠ CF, resta `c_corriente` de su saldo.

### D3. Restaurar ticket (`&restaurar=`)
- ⚠ **Bug:** vuelve a **sumar** stock en lugar de restarlo. Restaurar un ticket infla el inventario.
- Marca `eliminado = 0` y suma `c_corriente` al saldo del cliente.

### D4. Reasignar cliente (`&cambiarUsuario=`)
- Bloqueado si la caja está cerrada o si es un pago a cuenta (`tipo = 3`).
- Muestra el detalle del ticket y un combo de usuarios.
- Previsualiza el nuevo saldo del destino; si supera el acuerdo, lo pinta en rojo y **no deja confirmar**.
- Al confirmar: cambia `ventas.cliente`, suma el importe al destino y lo resta del origen.

### D5. Retiro de efectivo
Modal ⬆ que pide monto y muestra el efectivo disponible (`efectivo en caja − retiros`).
Inserta un `gasto` de `tipo = 95`, área `1`, e imprime comprobante.

### D6. Ver Parcial
Resumen en vivo de la caja abierta, dos bloques:
- **Ingresos:** por área (Almacén, Verdulería, Cigarrillos, Carga Virtual, Rotisería) + total,
  y por medio de pago (efectivo neto de vuelto, tarjetas, cuenta corriente, saldo,
  cantidad de tickets, primer y último ticket).
- **Egresos:** por área y por tipo (proveedores, otros, sueldos, viáticos, impuestos, retiros).

### D7. Cierre de caja (3 pasos)
1. **"Cerrar Caja"** — muestra los valores con la advertencia de irreversibilidad.
2. **"Continuar"** — repite la confirmación (⚠ el segundo título dice "Total Egresos" dos veces).
3. **"Finalizar"** — ejecuta:
   - `INSERT INTO cajas` con todos los totales (ingresos `c2..c6`, egresos `g_c1..g_c6`,
     `gProveedores/gOtros/gSueldos/gViaticos/gImpuestos`, `gTotal` neto de retiros, `retiros`).
   - `INSERT INTO cajaz` (caja general/fondo): `inicio` = `final` del último cierre,
     `ingreso` = retiros, `egreso` = `gTotal`, `final` = inicio + ingreso − egreso, `tipo = 1`.
   - `UPDATE ventas SET cerrada = 1, id_caja = <nueva caja>` para todo lo abierto del local.
   - `UPDATE gastos SET cerrada = 1, id_caja = <nueva caja>` idem.
   - Ofrece imprimir el comprobante.
- ⚠ Los tres pasos viajan por POST con **todos los importes en inputs readonly**: manipulables desde el navegador.
- ⚠ Sin transacción: si falla el update de gastos, la caja queda cerrada a medias.

---

## E. Artículos

**Ruta:** `index.php?menu=articulos` — barra con 7 accesos.

### E1. Ver artículos (`opc=ver-todos`)
Tabla con filtro por proveedor. Columnas: ID (4 dígitos), nombre, unidades por bulto,
**costo sin IVA** (`lista / 1.21`), costo final, bulto sin IVA, bulto final, precio de venta.
⚠ El 21% de IVA está hardcodeado. Los inactivos se muestran en naranja.
Acciones: editar, eliminar (soft `activo=0`) / restaurar (`activo=1`).
⚠ Los inputs ocultos apuntando a `opc=editarMasivo` no tienen ningún handler en `articulos.php`
(el router no tiene `case 'editarMasivo'`) — vestigio no funcional.

### E2. Crear artículo (`opc=nuevo`)
Solo pide **código de barras + nombre**. Si el código ya existe redirige a la edición.
Valida que el código sea numérico. Tras crear, redirige al formulario completo de edición.

### E3. Editar artículo (`opc=editar&id=`)
Formulario grande con navegación por `tabindex` (1→30). Campos:

- **Identificación:** ID (readonly), lista de códigos de barra (+ botón para agregar), código interno, nombre, nombre de oferta.
- **Costos:** precio costo lista, descuento %, precio costo nominal, precio costo oferta.
- **Ventas:** precio lista, precio oferta, precio por cantidad, precio empleado.
- **Stock:** existencias, existencia mínima, reposición, unidades por bulto.
- **Clasificación:** área, proveedor, marca, grupo.
- **Ofertas:** por días (desde/hasta), por horas (desde/hasta), por cantidad (N).
- **Flags:** producto/servicio, activo.

⚠ Bug de precedencia en 5 checkboxes: `$_POST['activo'] ?? "" == "on" ? 1 : 0`.
En PHP `==` liga antes que `??`, así que la expresión evalúa `$_POST['activo'] ?? ("" == "on" ? 1 : 0)`.
Resultado: cuando el checkbox está tildado guarda el string `"on"` (→ `1` al castear), y cuando
está destildado guarda `0`. Funciona por accidente, pero es frágil.

### E4. Códigos de barra (`cargarCodigo.php`)
Modal que agrega un EAN adicional al artículo. Un artículo puede tener **N códigos de barra**
(tabla `codigos_barra`).
⚠ El backend solo valida **unicidad** del código. La validación de largo (7–13 dígitos) es
solo del lado del cliente (`articulos.php`, función JS `validateCodigo`) y se puede saltear
haciendo un POST directo a `cargarCodigo.php`.

### E5. Cambiar código (`cambiarCodigo.php`, 510 líneas)
⚠ **Código muerto / inalcanzable.** El router `articulos.php` no tiene ningún `case 'cambiarCodigo'`
(cae al `default`) y nada del frontend enlaza a esta pantalla. Además consulta columnas viejas
(`caja`, `proveedor`, `marca`, `grupo`) que ya no existen en `articulos` (el schema actual usa
`id_area`, `id_proveedor`, `id_marca`, `id_grupo`). No es una herramienta operativa hoy.

### E6. Marcas (`opc=marcas`)
ABM simple: id, nombre, grupo, proveedor.

### E7. Grupos (`opc=grupos`)
ABM con nombre y **margen %**, más la configuración de oferta de grupo
(por cantidad / directa, con restricción de días y horas).

Dos acciones adicionales, no documentadas en el menú pero presentes en el código:
- `&eliminarGrupo=<id>` — `DELETE FROM grupos` y luego `UPDATE articulos SET grupo='0' WHERE grupo=<id>`.
  ⚠ La columna `grupo` ya no existe en `articulos` (el schema actual usa `id_grupo`), así que ese
  `UPDATE` falla silenciosamente y los artículos del grupo eliminado quedan con un `id_grupo` huérfano.
- `&eliminarOfertaGrupo=<id>` — resetea a `0` todos los campos de oferta del grupo
  (`ofertaCantidad`, `ofertaDirecta`, `dias`, `horas`, `cantidad`, `precio`, `descuento`, etc.).

### E8. Proveedores (`opc=proveedores`)
ABM: el formulario real solo lee/escribe **nombre, razón social y CUIT**. El resto de las
columnas de la tabla (`domicilio`, `tel`, `vendedor`, `cel`, `supervisor`, `celSupervisor`,
`margen`) existen en el schema pero no están expuestas en ningún formulario.

### E9. Stock (`opc=stock`)
Tablero de inventario:
- Totales: existencias, valorizado a costo nominal, a precio oferta, a precio lista, a precio de venta, a precio por cantidad.
- **Productos sin stock** (`existencia <= 0`), filtrable por proveedor, límite 150.
- **Productos bajo mínimo** (`0 < existencia < existenciaMinima`), filtrable por proveedor, límite 150.
- Ambas listas imprimibles.
- Solo considera `producto = 1 AND activo = 1`.

---

## F. Usuarios / Clientes

> El sistema **no distingue clientes de operadores**: todos viven en la tabla `usuarios`.
> `tipoUser = 1` son clientes de cuenta corriente; `2/3/4` son operadores.

### F1. Listado (`menu=usuarios`)
Todos menos el ID 1 (Consumidor Final). Columnas: número de cliente (4 dígitos), nombre,
domicilio, teléfono (prefiere celular), saldo, acuerdo. Marca con ícono los que tienen privilegios.

### F2. Crear (`opc=nuevo`)
Nombre, apellido, DNI, fecha de nacimiento, domicilio, teléfono, celular, e-mail, acuerdo.
Se crea con `tipoUser = 1` y `lista = 1`. El campo `user` se arma como `"Nombre Apellido"`.

### F3. Editar (`opc=editar&usuario=`)
Todo lo anterior más `tipoUser`, `user`, `pass` (⚠ en claro), `lista` y **saldo editable a mano**.

El combo `lista` tiene **4 opciones**, no 2: `1` Normal, `2` Descuento Especial, `3` "5% Descuento",
`4` "10% Descuento".

`tipoUser` tiene además una 4ª variante, **"Super Administrador"** (`tipoUser=4`), que en este
formulario aparece bloqueada (input oculto de solo lectura): no es reasignable desde la UI de edición.

### F4. Cuenta corriente (`opc=cc&usuario=`)
Cabecera con datos del cliente, saldo, acuerdo y **disponibilidad** (`acuerdo − saldo`).
Movimientos del último mes por defecto, con filtro desde/hasta o "Ver Histórico".
El saldo se muestra corriendo hacia atrás desde el saldo actual.
⚠ `echo $listaCliente;` de depuración (`cuenta-corriente.php:51`) imprime un valor crudo
directo en el HTML de la página.

Tipos de movimiento (`ventas.tipo`):

| `tipo` | Etiqueta en pantalla |
|---|---|
| `1` | venta |
| `2` | devolución |
| `3` | PAGO A CUENTA |
| `4` | ACTUALIZACION DE PRECIOS |
| `5` | ajuste manual (muestra `obs`) |

**Acciones:**
- **Ingresar pago** — modal con efectivo y tarjetas. Crea una `venta` de `tipo = 3` con
  `c_corriente` negativo y descuenta el saldo.
- **Ajuste personalizado** — modal con detalle e importe. Crea `tipo = 5` con el detalle en `obs`.
- **Actualizar precios** — la feature más particular del sistema:
  - Recorre todas las ventas del cliente con `actualizada = 0`.
  - Por cada línea, busca el **precio actual** del artículo (`precioEmp` si `lista = 2`, si no `precio`).
  - Calcula la diferencia contra el precio al que se vendió.
  - Las líneas de oferta se revierten (el descuento se anula).
  - Marca las ventas como `actualizada = 1`.
  - Graba una `venta` de `tipo = 4` con el detalle y ajusta el saldo.
  - **Efecto de negocio:** el fiado se indexa al precio del día de pago, no al de la compra.
    Es un mecanismo anti-inflación.
  - ⚠ **Alcance de `usuarios.lista`:** esta distinción `precio`/`precioEmp` según `lista` **solo
    existe acá**, dentro de "Actualizar precios" (`cuenta-corriente.php:64-87`). El motor de venta
    del POS (`facturacion.php:560-620`) siempre usa `articulos.precio` y nunca consulta
    `usuarios.lista` — un cliente "empleado" paga el precio de mostrador normal al cobrar. No hay
    pricing diferenciado en el checkout hoy.

---

## G. Estadísticas

**Ruta:** `index.php?menu=estadisticas` — 4 accesos.

### G1. Inicio (dashboard)
Ventas y gastos de los últimos 7 días agrupados por día.

### G2. Ver Cajas (`opc=cajas`)
- Histórico de cierres del punto de venta, ordenado por fecha.
- Por fila: fecha, total de ingresos, total de egresos, neto acumulado, operador,
  con tooltips que desglosan por área y por medio de pago.
- **Detalle (`&ver=<id>`):** cabecera del cierre + todos los tickets de esa caja + todos los gastos,
  con totales separados de gastos y retiros. Imprimible.

### G3. Caja General / Caja Z (`opc=cajaZ`)
Libro del fondo de caja: últimos 20 movimientos con `inicio`, `ingreso`, `egreso`, `final`, operador y concepto.
Modal "Ingresar FC" para cargar el fondo de caja actual y registrar la diferencia.

### G4. Caja Virtual (`opc=cajaV`)
Control del negocio de recargas y servicios. Tres canales:

| Canal | Campos |
|---|---|
| **Virtual** (`v*`) | inicial, ventas, cantidad, adicionales, depósitos, comisiones, final |
| **Claro** (`c*`) | ídem |
| **SUBE** (`s*`) | inicial, ventas, cantidad, depósitos, final |
| **Efectivo** (`e*`) | inicial, ajuste, final |

- **Adicionales:** `cantidad_operaciones × 5` (⚠ importe fijo hardcodeado).
- Carga de nueva jornada arrastrando los saldos finales de la anterior como iniciales.
- Calcula `final`, `diferencia` y permite cargar el fondo de caja real para conciliar.
- Últimos 20 movimientos.

---

## H. Endpoints AJAX

| Endpoint | Entrada | Salida |
|---|---|---|
| `buscar.php` | `valorBusqueda` | `"nombre,precio"` en texto plano |
| `mostrarArticulos.php` | `valorBusqueda` | tabla HTML de artículos con link `&agregar=` |
| `mostrarClientes.php` | `valorBusqueda` | tabla HTML con link `&cambiarUser=` |
| `mostrarClientesCC.php` | `valorBusqueda` | ídem apuntando a cuenta corriente |
| `cargarCodigo.php` | `barcode`, `id` | `"EXITO:mensaje"` o `"ERROR:mensaje"` |
| `filtrarArticulo.php` / `filtrarUsuario.php` | — | ⚠ código muerto (ver abajo) |
| `combos.php` | `valorBusqueda` | ⚠ roto |

⚠ Ninguno valida sesión. `buscar.php` y `cargarCodigo.php` son accesibles sin login.

⚠ **`filtrarArticulo.php` / `filtrarUsuario.php` son código muerto.** Ningún `$.post`/`$.get` del
frontend los invoca. Abren su propia conexión hardcodeada (`c1890978_ways` / `naGOfi35me`),
distinta de la de `conexion.php`, y consultan columnas viejas de `articulos` (`caja`, `proveedor`,
`marca`, `grupo`) que ya no existen. `filtrarUsuario.php` ni siquiera toca la tabla `usuarios`: es
un clon mal copiado que consulta `articulos` igual que `filtrarArticulo.php`.

---

## I. Resumen de reglas de negocio a preservar

1. Un artículo tiene **N códigos de barra** + un código interno corto.
2. Códigos de menos de 7 dígitos son ID interno; de 7 o más, EAN.
3. **5 precios por artículo:** lista, oferta, por cantidad, empleado, y costo (lista/nominal/oferta).
4. ⚠ La lista de precios del cliente (`usuarios.lista`) decide entre `precio` y `precioEmp`
   **solo** dentro de "Actualizar precios" de cuenta corriente (`cuenta-corriente.php:64-87`).
   El motor de venta del POS (`facturacion.php:560-620`) siempre cobra `articulos.precio` y no
   consulta `usuarios.lista` — no hay pricing diferenciado por cliente en el checkout.
5. Ofertas por horario, por rango de fechas y por cantidad, a nivel artículo y a nivel grupo.
6. La caja se cierra manualmente y congela ventas y gastos del período (`cerrada = 1`, `id_caja`).
7. La caja Z arrastra el saldo del fondo entre cierres.
8. La cuenta corriente tiene límite de crédito (`acuerdo`), y `-1` significa ilimitado.
9. Las ventas fiadas se **reindexan a precio del día** al momento de pagar.
10. Toda operación está scopeada por `id_punto_venta`.
11. El stock se descuenta al cerrar la venta y se devuelve al anular.
12. Tolerancia de pago: $10. Vuelto máximo: $20.
