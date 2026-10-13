> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 3. Artículos, listas de precio y ofertas

### Familias de artículos

Una **familia** agrupa artículos que son **siempre idénticos** en sus campos compartidos. La
familia no guarda ningún valor: sus miembros son la fuente de verdad, así que los lectores
(listados, POS, ventas, reportes) siguen leyendo cada artículo tal cual. La carga es de los
**escritores**: todo cambio de un campo compartido o de un precio debe replicarse a todos los
miembros en la misma transacción. El esquema no fuerza la igualdad; la sostienen los escritores.

> **Estado (familias de artículos):** implementados el modelo (tabla `familias`, columna
> `articulos.id_familia`, regla pura `ValoresCompartidosDeFamilia`) y cuatro escritores que respetan el
> protocolo de locks y replican a la familia en su propia transacción: el de **precios** ("Precios con
> alcance de familia", abajo; también lo usa aplicar el precio sugerido de una compra), la **edición de
> artículos** ("Edición con alcance de familia"), el **alta de un artículo dentro de una familia** ("Alta
> dentro de una familia", y "Alta con una familia nueva", que crea la familia con el artículo como único miembro) y la **confirmación de una compra**, que replica `costo_nominal` ("Compras y
> familias"). La **baja lógica** de un artículo también cambia quién es miembro (`deleted_at`) y no sigue ese
> protocolo: no toma el lock de membresía sino el `FOR UPDATE` de la fila del artículo, y los escritores que
> bloquean miembros reevalúan `deleted_at IS NULL` sobre cada fila bajo su propio lock de fila, así que un
> miembro dado de baja a mitad de camino queda fuera de lo que escriben. Tres operaciones de gestión cambian la
> pertenencia y siguen el protocolo con el lock de membresía exclusivo ("Gestión de familias"): **agrupar
> artículos** —crear una familia con un artículo de referencia y agregar artículos que ya existen a una
> familia—, que los ALINEA con la referencia en los quince campos compartidos y en los precios ("Alinear"), y las dos
> que solo SACAN miembros, **sacar un artículo de su familia** y **disolver la familia**, que no cambian ningún
> campo compartido ni ningún precio. El alta de un artículo con `idFamilia` agrega un miembro NUEVO; agrupar suma
> artículos que ya existen.

| | Campos |
|---|---|
| **Compartidos** (quince columnas de `articulos`) | `id_area`, `id_categoria`, `id_grupo`, `id_proveedor_habitual`, `id_alicuota_iva`, `unidad_venta`, `unidades_por_bulto`, `es_producto`, `controla_lote`, `acumula_en_venta`, `costo_lista`, `costo_lista_por`, `costo_lista_incluye_iva`, `descuento_proveedor`, `costo_nominal` — más el estado de precios de **cada lista fija** (precio vigente y precio pendiente o programado) |
| **Propios** de cada artículo | `nombre`, `descripcion`, `codigo_interno`, códigos de barra, `id_marca`, `activo`, `disponible_para_todas` (con `articulos_empresas`) |

- **"Solo este"** significa que el artículo **sale de la familia** (`id_familia = NULL`) con el
  valor nuevo. No hay excepciones dentro de una familia: o es idéntico a sus miembros, o no es
  miembro.
- **Miembro** es todo artículo con `id_familia = F` y `deleted_at IS NULL`. Un artículo dado de
  baja no cuenta como miembro para ningún efecto.
- La regla pura de qué es compartido y qué es propio vive en `ValoresCompartidosDeFamilia`
  (Domain): una columna nueva en `articulos` obliga a clasificarla.

**Precios con alcance de familia.** `POST /api/articulos/{id}/precios` y
`POST /api/articulos/{id}/precios/programados` aceptan un `alcance` opcional (`Familia` o `SoloEste`):

| `alcance` | Artículo sin familia | Artículo miembro |
|---|---|---|
| ausente | se escribe solo | `409 alcance_requerido` (el mensaje nombra la familia y cuántos artículos vivos tiene); no se escribe nada |
| `Familia` | `409 familia_cambio` | el precio se aplica a **todos** los miembros vivos |
| `SoloEste` | `409 familia_cambio` | el artículo **sale de la familia** y se escribe solo él |

Un **ordinal** que no es el de ninguno de los dos (el JSON acepta el ordinal además del nombre; los
valores son `Familia = 1` y `SoloEste = 2`, así que el `0` tampoco vale) es `400 alcance_invalido`. Un
**texto** que no se lee ni como el nombre de un valor ni como un ordinal no llega al servicio: lo rechaza
el binding JSON del framework, como a cualquier otro enum de la API, con `400 cuerpo_invalido` en vez de
`alcance_invalido`.
La escritura es **todo o nada**: una sola transacción, y un conflicto de cualquier miembro
(`precio_pendiente_existe` sin `confirmarReemplazo`, `vigente_desde_invalido`) la aborta entera, incluida
la salida de la familia de "solo este". Todos los miembros comparten el mismo "ahora" y cada uno
registra su propia auditoría `precio.cambio`. Los llamadores internos que no tienen a quién preguntar
(aplicar el precio sugerido de una compra) piden "familia si corresponde": un miembro se escribe con
su familia y quien no lo es, solo.

**Edición con alcance de familia.** `PUT /api/articulos/{id}` acepta el mismo `alcance` opcional. Lo que
decide qué se escribe es si el pedido **cambia algún campo compartido**: se comparan los quince valores
compartidos del pedido con los **actuales** del artículo, leídos bajo los locks (nunca una lectura previa).
El pedido puede omitir `acumula_en_venta` (`null` conserva el valor guardado): su valor en el pedido es
entonces el guardado en el artículo editado, leído bajo esos mismos locks, así que no cuenta como cambio y,
con `Familia`, es el que se aplica a todos los miembros vivos.
`costo_lista_por` y `costo_lista_incluye_iva` siguen la misma regla (`null` conserva el guardado). `costo_nominal` no viaja
en el pedido, ni en el alta ni en la edición. En la edición el servidor lo recalcula (costo de lista, su base, el
descuento y la alícuota del pedido) solo si cambia alguna entrada del cálculo respecto de lo guardado: `costo_lista`,
`costo_lista_por`, `costo_lista_incluye_iva`, `descuento_proveedor`, `unidades_por_bulto` (mientras la base es bulto,
antes o después) o el porcentaje de la alícuota (mientras el costo se guarda sin IVA, antes o después). Si ninguna
cambia conserva el guardado, que puede ser el costo efectivo de una compra, y ese valor es el que se compara con la
familia y el que se aplica a sus miembros. Un alta común siempre lo calcula; un alta dentro de una familia hereda el
de la referencia y `costo_nominal` nunca es motivo de `familia_valores_distintos` por sí solo.

**Sugerencia de precio** (`GET /api/articulos/{id}/sugerencia-precio?idEmpresa=`, nunca se aplica sola). El precio
sugerido es `costo base × (1 + margen / 100)`, con el margen del grupo del artículo o, si no tiene, el de su
proveedor habitual; sin costo base o sin margen no hay sugerencia (`precioSugerido: null`). El costo base es el
`costo_nominal` guardado o, si el artículo no lo tiene, el que sale de normalizar `costo_lista` (por unidad, con IVA
y neto del descuento, con la misma regla de `costo_nominal`); sin unidades por bulto en un costo por bulto, o sin la
alícuota, tampoco hay costo base. El resultado se redondea al múltiplo más cercano del parámetro
`redondeo_sugerencia_precio` (decimal, default `100`, a nivel empresa): 2896 se sugiere como 2900 y el empate se
redondea lejos de cero (2950 → 3000). Si el múltiplo más cercano es cero (el precio no llega a la mitad del múltiplo)
o el parámetro es menor o igual que cero, se redondea a 2 decimales. `idEmpresa` es opcional: sin él se usa el default
del parámetro; con una empresa inexistente o de otro tenant responde `404`. El parámetro no valida rango al guardarse
(solo que sea un decimal).

| `alcance` | Artículo sin familia | Miembro, sin cambio de campos compartidos | Miembro, con cambio de campos compartidos |
|---|---|---|---|
| ausente | se edita solo | se escriben solo los campos propios del artículo | `409 alcance_requerido` (el mensaje nombra la familia y cuántos artículos vivos tiene); no se escribe nada |
| `Familia` | `409 familia_cambio` | se escriben solo los campos propios (no hay nada que replicar) | los quince campos compartidos del pedido se aplican a **todos** los miembros vivos y los campos propios solo al artículo editado |
| `SoloEste` | `409 familia_cambio` | el artículo **sale de la familia** y se escriben sus campos propios | el artículo **sale de la familia** y se escribe todo, solo en él |

Los campos propios (`nombre`, `descripcion`, `id_marca`, `activo`, `disponible_para_todas` con su
`articulos_empresas`) son siempre del artículo editado: ningún otro miembro los recibe. El `alcance` inválido
(un ordinal que no es el de ninguno de los dos valores) es `400 alcance_invalido`, y el 404 de un artículo
inexistente sigue precediendo a cualquier validación del pedido. La edición es **todo o nada**: una sola
transacción sin reintento, y todo rechazo (`alcance_requerido`, `familia_cambio`, `referencia_invalida` y las
demás validaciones) ocurre antes de mutar ninguna entidad. Cuando `controla_lote` pasa de `false` a `true` en
varios miembros, la reconciliación de lotes corre **por cada miembro** que cambió, después del commit y en
orden ascendente de id; mantiene el contrato de fallo parcial de siempre —el cambio ya está comiteado—, así que
si una falla, los miembros siguientes quedan con el valor nuevo y sin reconciliar (se recupera con
`POST /api/stock/lotes/reconciliacion`). Las lecturas (`GET /api/articulos`, `GET /api/articulos/{id}` y la
respuesta de la edición) exponen `idFamilia` tal como está guardado, sin tratarlo como `NULL` cuando la fila
de la familia está dada de baja: los escritores definen la pertenencia por `id_familia` y la baja del propio
artículo, y un lector que lo anulara discreparía con ellos sobre quién es miembro.

**Alta dentro de una familia.** `POST /api/articulos` acepta un `idFamilia` opcional: el artículo nace como
miembro de esa familia. Entrar es un cambio de pertenencia: el alta toma el lock de membresía **exclusivo**
como primera sentencia de su transacción —antes que los locks de los catálogos del pedido—, y bajo él, después de
los chequeos de catálogo del pedido (una referencia inexistente es `400 referencia_invalida` aunque el pedido además
difiera de la familia):

1. la familia tiene que existir y estar viva (si no, `404`; también la de otro tenant) y estar **activa**
   (`409 familia_inactiva`). Se lee y se bloquea `FOR SHARE`, que serializa el alta con cualquier `UPDATE` de la
   fila de la familia, sea una baja lógica o un cambio de `activo`;
2. tiene que tener al menos un artículo vivo (`409 familia_sin_articulos`); la **referencia** es el miembro vivo
   de menor `id_articulo`, leído una sola vez bajo esos locks;
3. los quince campos compartidos del pedido tienen que ser idénticos a los de la referencia: si no,
   `409 familia_valores_distintos`, que nombra las columnas que difieren. El pedido que no trae
   `acumula_en_venta` lleva su valor por defecto (`true`, o `false` en un artículo por importe) y se compara como cualquier otro. Los rechazos se evalúan en ese orden
   (inactiva, sin artículos, valores distintos) y siempre antes de insertar nada;
4. se inserta el artículo con `id_familia = F` y, en la misma transacción y con el mismo "ahora", se copia el
   estado de precios de la referencia en cada lista fija: el precio vigente a ese instante (la fila nueva
   arranca en el "ahora" del alta) y, si la referencia tiene un precio pendiente, el vigente se cierra donde
   empieza el pendiente y se abre una fila pendiente con su misma fecha. La historia cerrada de la referencia no
   se copia. Cada fila insertada lleva su fila de auditoría `precio.cambio`, sin valor anterior.

`ServicioDePrecios` sigue siendo el único escritor de `precios`: la copia es
`CopiarEstadoDePreciosAlNuevoMiembroAsync`, que corre dentro de la transacción del alta, toma los locks de par
del artículo nuevo en orden ascendente de su clave después de las filas y solo inserta. Los campos propios del
artículo son los del pedido. Un fallo al guardar —también el de las filas de precio— revierte el artículo entero,
y el alta no se reintenta. Sin `idFamilia` el alta no cambia: no toma el lock de membresía y no copia nada.

**Alta con una familia nueva.** `POST /api/articulos` acepta también un `nombreFamiliaNueva` opcional: el artículo y la
familia con ese nombre se crean en UNA transacción y el artículo es su único miembro y su referencia. No hay valores
compartidos que comparar ni precios que copiar, y ni el alta de artículos ni la creación de una familia escriben auditoría
propia, así que no se agrega ninguna fila de auditoría. Es **excluyente** con `idFamilia`: los dos a la vez son
`400 familia_existente_y_nueva`, sin elegir ninguno en silencio. Con el campo ausente o en `null` el alta no cambia. Presente,
el nombre se normaliza como el de cualquier familia (`NombreDeFamilia.Normalizar`): una cadena vacía o de solo espacios es
`400 nombre_requerido` y una de más de 150 caracteres, `400 nombre_muy_largo`; un campo aceptado nunca se descarta. Un
nombre en uso entre las familias vivas del tenant (sin distinguir mayúsculas) es `409 familia_nombre_duplicado`: el
pre-chequeo corre antes de abrir la transacción y es solo de mejor esfuerzo; el respaldo es `ux_familias_nombre`, cuyo
`23505` se produce al insertar la familia cuando la carrera se pierde y la API traduce al mismo `409`. Un fallo en cualquier
punto revierte la transacción entera y no deja ni el artículo ni la familia. La respuesta es la del alta
(`ArticuloListado`), con el `idFamilia` de la familia creada. Orden de locks: el de membresía **exclusivo** es la primera
sentencia, igual que con `idFamilia`; después los chequeos de catálogo; la familia nueva se inserta (con su propio
`SaveChangesAsync`, que da el id) justo antes que el artículo. Una fila nueva no la puede bloquear nadie, así que no hay
fila de familia que bloquear ni filas de miembros ni locks de par.

**Compras y familias.** La confirmación de una compra (`ServicioDeCompras.ConfirmarAsync`) toma el lock de
membresía **compartido** como primera sentencia de su transacción, antes que el `UPDATE` del encabezado:
ningún alta ni salida de una familia puede intercalarse hasta el commit. El costo que escribe
(`costo_nominal`, solo con las líneas `actualiza_costo` con `costo_unitario > 0` y costo efectivo > 0: una
bonificación total, cuyo descuento iguala al importe bruto, no pone en cero el costo del artículo ni el de su
familia) se replica a **todos los miembros vivos** de la familia de cada línea: de las líneas de una misma
familia gana la de mayor `orden` —no la de mayor costo— y su costo efectivo es el de toda la familia; los
artículos sin familia conservan el dedupe por artículo. Las filas de los artículos a los que se escribe el
costo (los miembros vivos de cada familia ganadora y los artículos sueltos ganadores) se bloquean
`FOR NO KEY UPDATE` en un solo statement ordenado por id —paso (2) del protocolo—, después del stock y antes del
lock del proveedor, que sigue siendo el último lock de fila de la transacción, y recién entonces el costo se
escribe con un solo `UPDATE` para todas ellas. Si ninguna línea actualiza el costo, la confirmación no lee la
pertenencia ni bloquea ni escribe nada de este paso. La baja lógica de un artículo no toma el lock de
membresía sino el de su fila, así que puede comitear entre la lectura de la pertenencia y el bloqueo de las
filas: el bloqueo reevalúa `deleted_at IS NULL` cuando obtiene el lock de cada fila, de modo que el artículo
dado de baja no se devuelve en ese bloqueo ni recibe el costo de la familia, y los miembros vivos quedan
idénticos entre sí; uno dado de baja antes de confirmar, aunque conserve su `id_familia`, es un artículo
suelto y su línea solo lo escribe a él. Anular una compra nunca revierte el costo, tampoco el de una familia.

Aplicar el precio sugerido (`POST /api/compras/{id}/precios`) pide "familia si corresponde" para cada línea y,
como aplicar una línea de una familia llega a todos sus miembros, intenta **solo la de mayor `orden`** de cada
familia. Las demás líneas de esa familia salen en el resultado como no aplicadas (`aplicado = false`, sin
precio): no se intentan, y su motivo, en `error`, nombra la línea y el artículo que las superan y remite al
resultado de esa línea, que es el que dice si el precio se aplicó (si se rechaza, ni ella ni las superadas
escriben nada). Las líneas de artículos sueltos se intentan todas, en orden. El plan se arma con la
pertenencia leída antes de escribir, sin lock; cada línea se aplica en su propia transacción, que resuelve la
pertenencia de nuevo bajo el lock de membresía. Si la pertenencia cambia entre esa lectura y la escritura, el
plan puede no coincidir con ella: una línea superada nunca se intenta, así que si la que la supera se rechaza,
o su artículo salió de la familia, el precio de la compra no llega al artículo de la superada; y una línea que
se intenta se aplica con el alcance que su artículo tenga al escribirla —a toda su familia si ya es miembro,
aunque fuera suelto al armar el plan, o solo a él si dejó de serlo—. Cada escritura deja a la familia idéntica
en cualquier caso.

**Gestión de familias.** Las rutas de `/api/familias` son solo de admin (`GestionDeCatalogo`, la puerta del
alta y la edición de artículos), también las lecturas: el detalle trae los costos de la referencia. Las lecturas
no toman locks.

- `GET /api/familias`: las familias vivas del tenant, por nombre, cada una con `id`, `nombre`, `activo` y
  `cantidadArticulos`, que cuenta solo los miembros vivos.
- `GET /api/familias/{id}`: la familia, sus miembros vivos ascendentes por id (`id`, `codigoInterno`, `nombre`,
  `idMarca`, `activo`), los quince valores compartidos de la **referencia** (el miembro vivo de menor id) y su
  estado de precios —el precio vigente y, si lo hay, el pendiente con su fecha— en cada lista fija del tenant,
  también las que no tiene precios. Es lo que prellena el alta de un artículo dentro de la familia. Una familia sin
  miembros vivos no tiene referencia: `valores` es `null` y `precios` viene vacío. `404` si la familia no existe,
  está dada de baja o es de otro tenant. Un id de catálogo que apunta a una fila dada de baja viaja como `null`, igual
  que en la grilla de artículos: la `idMarca` de un miembro y el área, la categoría, el grupo y el proveedor
  habitual de la referencia.
- `POST /api/familias/previsualizacion` `{ idArticuloReferencia, idsArticulos }`: dice qué cambiaría si se agruparan
  esos artículos con la referencia como modelo, sin escribir nada, sin locks y sin transacción: es una foto. Los
  **destinos** son los ids pedidos sin repetir y sin la propia referencia, como máximo 100 (`400
  demasiados_articulos`); `400 id_articulo_referencia_requerido` si la referencia no es un id posible. Entre todos los
  destinos y las listas fijas del tenant hay además un tope de 1000 **pares** destino-lista fija: cada par es un lock
  advisory que el pedido real sostiene hasta el commit, la tabla de locks de Postgres es compartida por todas las
  conexiones, y los cien destinos solos no acotan los pares cuando el tenant tiene muchas listas fijas (con diez, cien
  destinos son mil pares; con cincuenta, caben veinte). Pasarse del tope es `400 demasiados_articulos` en el pedido real,
  con otro mensaje que nombra los dos factores, y en la previsualización es un problema más. Si la referencia ya es
  miembro de una familia, agrupar sería sumar a esa familia (`idFamilia`); si no, crear una nueva (`idFamilia: null`).
  Por cada destino que se puede alinear —no los inexistentes, ni los dados de baja, ni los que están en otra
  familia— trae las columnas compartidas que difieren, con los quince valores actuales y los de la referencia como los lee
  el detalle de una familia —un id de catálogo que apunta a una fila dada de baja viaja como `null`: una columna puede
  figurar entre las que difieren con `null` de los dos lados, porque el destino tiene un id colgante y la referencia
  ninguno, y la alineación se lo borra—, y por cada lista fija en la que su estado de precios cambia, el estado actual
  y el de la referencia; un destino ya idéntico trae `campos` y `precios` vacíos. Los `problemas` llevan el código de
  error de la API, en este orden: los de la familia de la referencia (`no_encontrado` si está dada de baja,
  `familia_inactiva`), `referencia_invalida` por cada artículo inexistente o dado de baja, `articulo_en_otra_familia`,
  `referencia_invalida` por cada catálogo de la referencia (la alícuota de IVA, el área, la categoría, el grupo y el
  proveedor habitual, en ese orden) que no existe o está dado de baja, `demasiados_articulos` si los pares pasan el
  tope y `familia_precio_inalineable` por cada par artículo-lista que no se puede alinear. Si la referencia no existe no
  hay con qué comparar: solo informa los artículos inexistentes.
- `POST /api/familias` `{ nombre, idArticuloReferencia, idsArticulos }`: **crea** la familia y la agrupa: la
  referencia y los destinos (los ids pedidos sin repetir y sin la propia referencia, como máximo 100) quedan como sus
  miembros, y los destinos, alineados con la referencia ("Alinear"). `201` con `Location: /api/familias/{id}` y
  `{ idFamilia, nombre, idArticuloReferencia, articulos }`, donde `articulos` trae, por cada destino ascendente por
  id, lo mismo que la previsualización del mismo pedido —`campos`, los valores `actual` y `nuevo` y los `precios` que
  cambiaron—, con `campos` y `precios` vacíos para el que ya estaba alineado. `idsArticulos` puede faltar o venir vacío: la
  familia nace con la referencia como único miembro. Los rechazos no escriben nada y se evalúan en este orden:
  `400 nombre_requerido`, `400 nombre_muy_largo`, `400 id_articulo_referencia_requerido` y `400
  demasiados_articulos`; `409 familia_nombre_duplicado`, que el chequeo previo adelanta —corre antes de abrir la
  transacción y no espera ningún lock— y que `ux_familias_nombre` sostiene en una carrera: dos pedidos al mismo nombre
  se serializan en el lock de membresía y el segundo recibe el 409 del respaldo; y, ya bajo los locks, `400
  referencia_invalida` (el menor id que no existe o está dado de baja entre la referencia y los destinos; también los de
  otro tenant), `409 articulo_en_otra_familia` (el menor id que ya es miembro de una familia, la referencia incluida:
  agrupar no mueve a nadie; el mensaje nombra la familia también si está dada de baja), `400 referencia_invalida` por
  el primer catálogo de la referencia que no existe o está dado de baja —la alícuota de IVA, el área, la categoría,
  el grupo y el proveedor habitual, en ese orden y con el código y el mensaje de la edición de artículos: los quince
  valores de la referencia son los que se copian a los demás—, `400 demasiados_articulos` si los pares destino-lista
  fija pasan los 1000 y `422 familia_precio_inalineable` (el mensaje nombra el artículo y la lista del primer par que no
  se puede alinear; lo que se había alineado antes de él no queda escrito).
- `POST /api/familias/{id}/articulos` `{ idsArticulos }`: **agrega** artículos que ya existen a la familia, alineados
  con su referencia: el miembro vivo de menor id, que no figura en `articulos`. `200` con el mismo cuerpo. Un artículo
  que ya es miembro de la familia se alinea igual, y si ya estaba alineado no se escribe nada: repetir un pedido no
  cambia nada. Rechazos, sin escribir nada: `400 articulos_requeridos` (lista ausente o vacía) y `400
  demasiados_articulos` si trae más de 101 ids distintos, antes de buscar la familia; `404` si la familia no existe,
  está dada de baja o es de otro tenant; `409 familia_inactiva`; `409 familia_sin_articulos` (sin un miembro vivo no hay
  referencia); y, como al crear, `400 referencia_invalida` (también por los catálogos de la referencia de la familia),
  `409 articulo_en_otra_familia`, `400 demasiados_articulos` por los pares y `422 familia_precio_inalineable`. La
  familia se resuelve antes que los artículos: la inactiva da su 409 aunque uno de los ids no exista. El tope de 100
  es sobre los destinos y la referencia de la familia —que puede figurar en la lista— no es uno: por eso la lista
  admite uno más, y el tope exacto se exige bajo el lock, cuando se la conoce y sale de la lista (101 ids que no la
  incluyen dan `400 demasiados_articulos` después de resolver la familia).
- `PUT /api/familias/{id}`: el nombre y el estado `activo`, los dos obligatorios (`400 nombre_requerido`,
  `400 nombre_muy_largo` —hasta 150 caracteres, sin espacios en los extremos— y `400 activo_requerido`: un `activo`
  ausente se rechaza, no se lee como `false`). El nombre es único entre las familias vivas del tenant sin distinguir
  mayúsculas (`ux_familias_nombre`): `409 familia_nombre_duplicado`, que sostiene la restricción también en una
  carrera y que el chequeo previo adelanta con un mensaje que nombra el nombre pedido; el nombre de una familia dada de
  baja se puede reutilizar. Una familia inactiva no admite miembros nuevos, ni por el alta de un artículo con
  `idFamilia` ni por agregarle artículos (`409 familia_inactiva`). Responde la familia como el listado. `404` si la
  familia no existe, está dada de baja o es de otro tenant: el servicio lo da antes de los tres `400` de arriba y del
  chequeo previo del nombre (`409 familia_nombre_duplicado`). Es una
  transacción sin reintento que lee la fila UNA vez, después de su `FOR UPDATE`; una familia dada de baja mientras
  esperaba ese lock da `404` y no se escribe nada. No toma el lock de membresía ni bloquea filas de artículos. Escribe
  la fila de la familia, que el alta de un artículo con `idFamilia` y agregar artículos leen `FOR SHARE`: un cambio de
  `activo` en curso hace esperar a esa operación, que lo ve.
- `DELETE /api/familias/{id}/articulos/{idArticulo}`: **saca** al artículo de la familia: queda sin familia
  (`id_familia = NULL`) con todos sus valores —los compartidos, los propios y sus precios— y los demás miembros no
  cambian. `204`. `404` si la familia no existe o está dada de baja, o si el artículo no existe o está dado de
  baja (también los de otro tenant); `409 familia_cambio` si el artículo existe pero no es miembro de esa familia.
  Es una transacción sin reintento: el lock de membresía exclusivo como primera sentencia y, bajo él, la fila del
  artículo `FOR NO KEY UPDATE` guardada por la familia pedida y por la baja lógica. Una familia que se queda sin
  miembros vivos sigue existiendo, vacía.
- `DELETE /api/familias/{id}`: **disuelve** la familia: sus miembros vivos quedan sin familia con todos sus
  valores y la familia se da de baja, en una sola transacción sin reintento y con un solo "ahora". Los artículos
  dados de baja conservan su `id_familia`. `204`; `404` si la familia no existe o ya está dada de baja (también la
  de otro tenant). Toma, en este orden, el lock de membresía exclusivo, la fila de la familia `FOR UPDATE` y las
  filas de los miembros vivos, ascendentes y `FOR NO KEY UPDATE`; el nombre de la familia disuelta se puede
  reutilizar. `409 familia_en_uso` si alguna oferta, viva o dada de baja, apunta a la familia (la misma política
  que la baja de un grupo): la guarda corre bajo los dos locks, después de leer la familia, y no cuenta a los
  artículos, que se sueltan en la misma transacción. El pre-chequeo de existencia de las
  ofertas no toma lock: queda el residual descrito en la sección de ofertas (§3).

**Alinear.** Alinear un artículo con el de referencia es dejarlo idéntico a él en los quince campos compartidos (los
propios no se tocan) y en el **estado de precios** de cada lista fija —el precio vigente y, si lo hay, el pendiente con su
fecha—. Dos estados se comparan por valor: vigente, y monto y fecha del pendiente. Un precio nunca se quita, así que hay
estados que no se pueden alinear:

| Referencia | Destino | Resolución |
|---|---|---|
| estado igual al del destino | — | sin cambios |
| vigente (con o sin pendiente) | cualquier otro estado | alinear |
| solo un pendiente | sin precio vigente | alinear (el pendiente propio, si lo hay, se reemplaza) |
| solo un pendiente | con precio vigente | rechazo: `familia_precio_inalineable` |
| sin ningún precio | con algún precio | rechazo: `familia_precio_inalineable` |

Un tercer rechazo del mismo código es un dato que la API no produce: un pendiente del destino que reemplaza a una fila
vigente que empieza en o después de "ahora". La regla es pura (`ReglaDeAlineacionDePrecios`).

**Escribir la alineación.** Agrupar es todo o nada: UNA transacción sin reintento (ni la familia ni las filas de precio
ni su auditoría tienen clave de idempotencia). Con el lock de membresía exclusivo como primera sentencia toma, en el
orden del protocolo, la fila de la familia `FOR SHARE` (al agregar), las filas de los artículos y los locks de par;
recién después lee "ahora", UNA vez, y planifica sin escribir —de ahí salen los rechazos, así que ninguno deja nada
escrito—; solo entonces escribe, con ese mismo "ahora":

- la familia, al crear, con `created_at` y `updated_at` en "ahora"; la referencia de una familia nueva recibe solo la
  pertenencia (`id_familia` y `updated_at`);
- cada destino recibe los quince campos de la referencia y, si no era miembro, `id_familia`; su `updated_at` pasa a
  "ahora" si entró o si cambió algún campo, y no se escribe si ya era miembro y estaba alineado;
- por cada par destino-lista que se alinea, `ServicioDePrecios` cierra la fila abierta del destino con las reglas de un
  cambio de precio —un pendiente se reemplaza cerrándolo en su propio inicio, y se vuelve a cerrar su predecesor en
  "ahora"— e inserta el estado de la referencia: el precio vigente desde "ahora", cerrado donde empieza el pendiente, y
  el pendiente con su fecha. Cada fila insertada lleva su fila de auditoría `precio.cambio`, con el estado inmediato
  anterior: la fila abierta que tenía el destino y, para la segunda fila, la primera. Un par que ya es igual no recibe
  nada.

Los locks de par se toman para TODOS los pares de los destinos y las listas fijas, antes de leer ningún precio: es el
superconjunto de lo que se puede escribir. La referencia no los toma, porque no se le escribe ningún precio, y la lista
derivada tampoco, porque no guarda filas. La familia se inserta en la fase de escrituras, después de "ahora" y de todos
los locks. Un fallo en cualquier paso revierte todo y suelta del contexto lo que la operación dejó rastreado. Cuando
`controla_lote` pasa de `false` a `true` en un destino, la reconciliación de lotes corre por cada uno, después del
commit y en orden ascendente de id; mantiene el contrato de fallo parcial de la edición de artículos —el cambio ya está
comiteado— y se recupera con `POST /api/stock/lotes/reconciliacion`.

**Protocolo de locks.** Toda transacción que escribe campos compartidos o precios, o que cambia la
pertenencia, toma, en este orden global: (1) el **lock de membresía** del tenant (`pg_advisory_xact_lock` de una
clave `bigint`, compartido para quien no cambia la pertenencia y exclusivo para quien la cambia: "solo este",
el alta de un artículo con `idFamilia`, sacar un artículo, disolver la familia y agrupar artículos),
como primera sentencia; (2) las **filas de `articulos`** de los miembros —y, al agrupar, de los artículos pedidos— en
orden ascendente de id (`SELECT … ORDER BY id_articulo FOR NO KEY UPDATE` en un solo statement, nunca
`FOR UPDATE` sobre varias filas: choca con el `FOR KEY SHARE` que toman las ventas por sus FK) o, en "solo este", la
del propio artículo; (3) los
**locks de par artículo-lista** en orden ascendente de su **clave de lock**
(`ServicioDePrecios.ClaveDeLockDePar`), no de `id_articulo`: la clave de pares de listas distintas
puede coincidir, y por id dos escrituras de familias distintas, cada una en su lista, podrían tomar las
mismas dos claves en orden opuesto y esperarse en ciclo; quien toma varios artículos en varias listas los toma todos
juntos en ese orden, sin agrupar por artículo ni por lista. La confirmación de una compra agrega dos tramos que
ningún otro escritor de familias toma: entre (1) y (2), las filas de la compra y de lo que mueve —encabezado,
orden de compra, lotes y stock—, y al final la fila del proveedor, el último lock de fila de su transacción.
Dos escritores de familias no se esperan en ciclo porque ninguno toma un lock de un tramo anterior después de
uno de un tramo posterior y, dentro de un mismo tramo, todos los que lo toman lo hacen en el mismo orden. Esa
garantía depende de que esos dos tramos sean solo de la confirmación —ningún otro escritor de familias toma
filas de compra, de stock ni de lotes, ni escribe la fila del proveedor— y de que el lock de membresía siga
siendo la primera sentencia de cada transacción; no cubre a las transacciones que no toman ese lock. Los
chequeos de catálogo (`FOR KEY SHARE`, incluido el del proveedor habitual de la edición, del alta y de agrupar, que
no choca con el `UPDATE` del proveedor de la confirmación) no chocan con ningún lock que tomen estos escritores y
no entran en el orden. La fila de la familia sí entra: el alta de un artículo la lee `FOR SHARE` después de los
chequeos de catálogo y antes de los locks de par; agregar artículos a una familia, la misma lectura, justo después
del lock de membresía y antes de las filas de los artículos; y la disolución la toma `FOR UPDATE` justo después de
ese lock y antes de las filas de los miembros. Las tres toman antes el lock de membresía en modo exclusivo, así que
no pueden esperarse entre sí por esa fila. La edición de la familia no toma el lock de membresía: toma solo esa fila
(`FOR UPDATE`), sin ningún otro lock de este protocolo, y por eso queda fuera del orden. Dos escrituras de la misma familia
no llegan a competir por los pares: se esperan antes, en (1) o en (2). La pertenencia que se lee después de (1) es
estable hasta el commit; un artículo sin familia no toma (2) en los precios, y en la edición de artículos
conserva su `FOR UPDATE` de siempre sobre la fila propia, después de la membresía. Lo implementan el escritor de
precios (`ServicioDePrecios.AbrirNuevoPrecioAsync`) y la edición de artículos
(`ServicioDeArticulos.ActualizarAsync`: exclusivo solo con `SoloEste`, compartido en cualquier otro caso; en
"solo este" bloquea solo la fila propia y en los demás casos todos los miembros, los 5 chequeos de catálogo
`FOR KEY SHARE` van después de las filas) y el alta con `idFamilia` o `nombreFamiliaNueva` (`ServicioDeArticulos.CrearAsync`:
exclusivo, antes de los catálogos; la familia `FOR SHARE`; sin filas de miembros que bloquear, porque el
artículo todavía no existe, y los locks de par del artículo nuevo al final) y la confirmación de una compra
(`ServicioDeCompras.ConfirmarAsync`: compartido, antes del encabezado; las filas de los artículos con costo
ascendentes después del stock; no toma locks de par, porque no escribe precios) y las dos operaciones que solo
sacan miembros (`ServicioDeFamilias.SacarArticuloAsync`: exclusivo y la fila del propio artículo, como "solo
este"; `ServicioDeFamilias.DisolverAsync`: exclusivo, después la fila de la familia `FOR UPDATE`, que ningún
otro escritor de este protocolo toma después de las filas de los artículos, y después las filas de los miembros;
ninguna toma locks de par, porque no escribe precios) y las dos operaciones que agrupan
(`ServicioDeAgrupacionDeFamilias.CrearAsync` y `AgregarArticulosAsync`, que comparten un solo camino: exclusivo; al
agregar, la fila de la familia `FOR SHARE` antes que las filas de los artículos; las filas de los artículos pedidos
y, al agregar, de todos los miembros, ascendentes y en un solo statement; los chequeos de catálogo de la referencia
—el área, la categoría, el grupo y el proveedor habitual `FOR KEY SHARE` y la alícuota de IVA, que es global y no se
bloquea— después de las filas, como en la edición de artículos; y los locks de par de todos los destinos en todas las
listas fijas, por clave ascendente, antes de leer ningún precio y con los pares ya acotados a 1000); todo escritor de
campos compartidos o de pertenencia tiene que respetarlo.
