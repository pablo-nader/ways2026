# 00 — Inventario del sistema legacy (Ways)

> Relevamiento del código en `alsina/` y del dump `alsina/localhost.sql`.
> Fecha de relevamiento: 2026-07-27.

## 1. Identidad del sistema

- **Nombre comercial:** Ways (marca propia, fuente `assets/fonts/ways.ttf`).
- **Dominio:** ERP/POS de autoservicio minorista con dos puntos de venta reales:
  - `1` — Ways Autoservicio, García Lorca 2438, 08:00–00:00
  - `2` — Ways Store, Gascón 1503, 07:00–00:00
- **Rubros operados (tabla `areas`):** N/A, Almacén, Verdulería, Cigarrillos, Carga Virtual, Rotisería.

## 2. Stack actual

| Capa | Tecnología |
|---|---|
| Runtime | PHP (procedural, sin framework), `mysqli` sin prepared statements |
| Base de datos | MySQL / MariaDB, InnoDB, charset `latin1` |
| Front | HTML generado por concatenación de strings en PHP |
| CSS | Bootstrap 5.2.0-beta1 (CDN) + `Bootstrap-Admin-Template` v2.4.2 (metisAdmin) + CSS propio |
| JS | jQuery 3.6.0 (CDN), metisMenu, Font Awesome |
| Sesión | `session_start()` nativo de PHP, estado del ticket vive **en la sesión del servidor** |
| Hosting | Compartido tipo cPanel (usuario `c1890978_alsina`) |

## 3. Estructura de archivos

```
alsina/
├── index.php               Front controller + <head> + todo el JS global (424 líneas)
├── body.php                Layout: navbar, footer, switch de módulos, modales globales
├── conexion.php            Credenciales hardcodeadas (define HOST/USER/PASSWORD/DATABASE)
├── funciones.php           comprobarOferta() y comprobarOfertaGrupo()
├── login.php               Login por <select> de usuario + password en texto plano
├── elegirLocal.php         Selección de punto de venta cuando el usuario tiene >1
├── logout.php
├── default.php             Placeholder vacío
│
├── facturacion.php         2306 líneas — POS, gastos y caja. El corazón del sistema.
├── articulos.php           Router del módulo Artículos
├── usuarios.php            Router del módulo Usuarios + modales de cuenta corriente
├── estadisticas.php        1254 líneas — Cajas, Caja Z (general), Caja V (virtual), dashboard
├── actualizar.php          Sincronización servidor remoto → local (script roto, ver §6)
│
├── modulos/
│   ├── articulos/{index,ver-todos,nuevo,editar,marcas,grupos,proveedores,stock,cambiarCodigo}.php
│   └── usuarios/{index,nuevo,editar,cuenta-corriente}.php
│
├── ticket.php              Impresión del ticket de venta (ventana popup 300x400)
├── reTicket.php            Reimpresión de un ticket existente
├── ticketCC.php            Comprobante de cierre de caja
├── ticketRetiro.php        Comprobante de retiro de efectivo
├── ticketOk.php            Limpia la sesión del ticket tras imprimir
├── reTicketOk.php
├── imprimirArticulos.php   Lista de reposición imprimible
│
├── buscar.php              AJAX: busca artículo por ID o código de barras → "nombre,precio"
├── mostrarArticulos.php    AJAX: tabla de artículos por nombre
├── mostrarClientes.php     AJAX: tabla de clientes
├── mostrarClientesCC.php   AJAX: idem, apuntando a cuenta corriente
├── combos.php              AJAX de combos — ROTO (schema viejo, credenciales root)
├── cargarCodigo.php        AJAX: alta de código de barras adicional
├── filtrarArticulo.php     Filtro de artículos
├── filtrarUsuario.php      Filtro de usuarios
│
├── assets/
│   ├── css/{main.css, theme.css, ticket.css, ways.css}
│   ├── fonts/ways.ttf      Fuente de la marca
│   ├── img/{favicon.png, pattern/}
│   └── lib/{font-awesome, metismenu}
│
├── errores/{403,404,405,500,503,countdown,offline}.php
├── sql/{1.0.sql, 2.0.sql}  Migraciones históricas aplicadas a mano
└── localhost.sql           Dump completo: 97 MB, 383.566 líneas
```

## 4. Modelo de request

No hay routing real. Todo pasa por `index.php` con query string:

```
index.php?menu=<modulo>&opc=<seccion>&<accion>=<valor>
```

- `index.php` abre sesión, conecta a MySQL, imprime `<head>` y decide:
  - `$_SESSION['login']['status'] == 'ready'` → `body.php`
  - `== 'logged'` → `elegirLocal.php` (falta elegir punto de venta)
  - sin sesión → `login.php`
- `body.php` hace un `switch($_GET['menu'])` que incluye el módulo correspondiente.
- Cada módulo acumula HTML en `$contenido` / `$menu` y el layout lo imprime al final.
- La navegación y las mutaciones se mezclan: casi todas las acciones destructivas viajan por **GET**
  (`&eliminar=`, `&restaurar=`, `&retiroEfectivo=`, `&pago=Cargar`).
- Las redirecciones se hacen con `echo '<script>window.location=...</script>'`.

## 5. Autenticación y autorización

- Login: `<select>` con **todos los usuarios operativos** (`tipoUser IN (2,3,4)`) + contraseña.
- Comparación de contraseña: `$user['pass'] == $pass` — **texto plano, sin hash**.
- Tras autenticar se resuelven los puntos de venta vía `usuario_rol_puntoventa`.
  - 0 locales → error; 1 local → entra directo; >1 → pantalla de selección.
- La sesión guarda: `status`, `user`, `id`, `tipoUser`, `punto_venta{id,nombre,domicilio,horario}`.
- **No hay chequeo de permisos por pantalla.** `tipoUser` y `roles` existen en la base pero
  ningún módulo los consulta para autorizar. Cualquier usuario logueado accede a todo.
- Convención observada de `tipoUser`: `1` = cliente (cuenta corriente), `2/3/4` = operativos.
- Existe la tabla `roles` (Administrador / Encargado / Vendedor) pero **no se usa en el código**.

## 6. Código muerto o roto detectado

| Archivo | Problema |
|---|---|
| `actualizar.php` | Sincroniza contra `152.171.159.179` con `root2/ePn35376189w`. Usa columnas del schema pre-2.0 (`caja`, `proveedor`, `marca`, `grupo`) que ya no existen. Falla siempre. |
| `combos.php` | Conecta a `127.0.0.1 root` sin password, base `ways`. Consulta columnas viejas. Falla siempre. |
| `mostrarArticulos2.php` | Referenciado desde `index.php`, **el archivo no existe**. |
| `sorteo.php` | Referenciado desde `index.php`, **no existe**. |
| `sistema.php` | Referenciado en el `switch` de `body.php`, **no existe**. |
| `facturacion.php:876` | Rama `if(isset($_GET['proveedor']))` del alta de compras: consulta `WHERE proveedor='...'` (columna renombrada a `id_proveedor`). Rota. |
| `combos` (tabla) | 0 filas. Feature abandonado. |
| `articulos_oferta`, `precios`, `listas_precio`, `stock` | Tablas creadas para una refactorización de precios/stock multi-local que **nunca se cableó al código**. Ver `03-modelo-destino-postgres.md`. |
| `index.php:86-120` | Atajos de teclado con IDs de artículo hardcodeados (711, 688, 1337, 710, 709, 697). |
| `facturacion.php:333-334` | `if($producto['id_area']==1)` duplicado en el `elseif` — rama inalcanzable. |

## 7. Deuda técnica crítica (para no repetirla)

1. **SQL injection en todas partes.** Cada query es interpolación de string con `$_GET`/`$_POST`.
2. **Credenciales de producción versionadas** en `conexion.php` y `actualizar.php`.
3. **Contraseñas en texto plano** en la tabla `usuarios`.
4. **Dinero en `float(10,2)`.** Todos los importes. Errores de redondeo acumulativos garantizados.
5. **El detalle de venta es un string.** `ventas.articulos` guarda
   `barra/cantidad/descripcion/precio/total*barra/...`. No es consultable ni agregable por artículo.
6. **El carrito vive en `$_SESSION`.** Si se cae la sesión se pierde la venta en curso.
   Los "tickets guardados" son 3 slots fijos (`$_SESSION['guardado'][1..3]`) con código triplicado.
7. **Sin transacciones.** Venta + descuento de stock + actualización de saldo son queries sueltas.
   Si falla la segunda, la base queda inconsistente.
8. **Sin control de concurrencia.** Dos cajas vendiendo el mismo artículo pisan el stock.
9. **Stock global, no por local.** `articulos.existencia` es una sola columna pese a haber 2 locales.
10. **Sin logs ni auditoría.** No hay forma de saber quién cambió un precio.
11. **Sin tests.**

## 8. Volumetría actual (dump `localhost.sql`)

| Tabla | Filas |
|---|---:|
| `ventas` | 345.665 |
| `gastos` | 13.492 |
| `cajaz` | 6.194 |
| `articulos` | 5.992 |
| `codigos_barra` | 3.954 |
| `cajas` | 2.793 |
| `cajav` | 1.763 |
| `marcas` | 539 |
| `grupos` | 136 |
| `proveedores` | 62 |
| `usuarios` | 30 |
| `areas` | 6 |
| `usuario_rol_puntoventa` | 4 |
| `listas_precio` | 3 |
| `roles` | 3 |
| `puntos_venta` | 2 |
| `articulos_oferta`, `cajagral`, `combos`, `precios`, `stock` | 0 |

El peso del dump (97 MB) es casi todo `ventas.articulos`.
