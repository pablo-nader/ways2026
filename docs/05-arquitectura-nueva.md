# 05 — Arquitectura de la app nueva

## Stack

| Capa | Elección | Versión local verificada |
|---|---|---|
| API | ASP.NET Core Minimal API / Controllers | .NET SDK 10.0.300 |
| ORM | EF Core + Npgsql | |
| Base | PostgreSQL 17 | |
| Front | React + Vite + TypeScript | Node 24.15.0 |
| Estilos | Bootstrap 5 + el CSS del template actual (`main.css`, `ways.css`, `ticket.css`) | |
| Contenedor | Docker (un solo contenedor, ver abajo) | Docker 29.5.3 |

## Estructura de repositorio

```
ways2026/
├── alsina/                 legacy PHP (solo referencia, no se toca)
├── docs/                   esta documentación
├── migracion/              scripts de migración de datos
├── src/
│   ├── Ways.Api/           ASP.NET Core: endpoints, DI, auth, middleware
│   ├── Ways.Domain/        entidades, value objects, reglas de negocio puras
│   ├── Ways.Application/   casos de uso (VenderArticulo, CerrarCaja, ...)
│   ├── Ways.Infrastructure/EF Core, repositorios, migrations
│   └── Ways.Web/           React + Vite + TS
├── tests/
│   ├── Ways.Domain.Tests/
│   └── Ways.Application.Tests/
├── docker/
│   ├── Dockerfile          multi-stage: build front + build api + runtime con Postgres
│   ├── entrypoint.sh       arranca Postgres, corre migrations, arranca la API
│   └── supervisord.conf
├── compose.yml
└── Ways.sln
```

Arquitectura hexagonal liviana: el dominio no conoce EF, la API no conoce SQL.
No hace falta más ceremonia que eso para este tamaño.

---

## Decisión: un solo contenedor

Pediste todo en un contenedor. Es viable y para arrancar está bien, pero conviene
tener claro el trade-off:

| | Un contenedor | Compose (3 servicios) |
|---|---|---|
| Simplicidad de deploy | ✅ un `docker run` | 2 comandos |
| Backup de la base | ⚠ hay que montar un volumen y no olvidarse | ✅ volumen explícito |
| Reiniciar la API sin tirar la base | ❌ | ✅ |
| Actualizar Postgres | ⚠ rebuild de todo | ✅ cambiar un tag |
| Escalar después | ❌ hay que separar igual | ✅ ya está separado |

**Recomendación:** hacer las dos cosas desde el día uno. El `Dockerfile` monolítico y el
`compose.yml` comparten el mismo build; mantenerlos en paralelo cuesta muy poco y el día
que quieras separar la base ya está resuelto. Arrancás con el contenedor único y migrás
cuando duela.

**Innegociable en cualquiera de las dos variantes:** el `PGDATA` va en un **volumen
nombrado**, no dentro del contenedor. Si no, el primer `docker rm` borra la base.

### Dockerfile (esqueleto)

```dockerfile
# --- 1. Front ---
FROM node:24-alpine AS web
WORKDIR /app
COPY src/Ways.Web/package*.json ./
RUN npm ci
COPY src/Ways.Web/ ./
RUN npm run build            # → /app/dist

# --- 2. API ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY *.sln .
COPY src/ src/
RUN dotnet publish src/Ways.Api -c Release -o /out

# --- 3. Runtime: Postgres + .NET + estáticos ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y postgresql-17 supervisor && rm -rf /var/lib/apt/lists/*
COPY --from=api /out            /app
COPY --from=web /app/dist       /app/wwwroot
COPY docker/entrypoint.sh       /entrypoint.sh
COPY docker/supervisord.conf    /etc/supervisor/conf.d/ways.conf
VOLUME /var/lib/postgresql/data
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
```

La API sirve el front como archivos estáticos (`app.UseStaticFiles()` + fallback a
`index.html`). Un solo puerto, sin CORS, sin proxy inverso adicional.

---

## Autenticación

Lo mínimo para paridad + lo que falta sí o sí:

- Login con `usuario + password`, `password_hash` con **BCrypt** o ASP.NET Identity.
- **Todas las contraseñas actuales se invalidan** en la migración. Se genera una temporal
  por empleado y se fuerza el cambio en el primer login. No hay forma de "migrar" hashes
  que no existen.
- Sesión con cookie `HttpOnly` + `SameSite=Lax` (más simple y más seguro que JWT en
  localStorage para una app interna que corre en una LAN).
- Punto de venta activo en el claim de la sesión, igual que hoy.
- **Autorización real por rol** desde el día uno: los roles ya están en la base
  (Administrador / Encargado / Vendedor) y el legacy nunca los usó. Endpoints como
  "editar saldo de cliente" o "cerrar caja" tienen que estar detrás de un rol.

---

## Estilos: reutilizar el template actual

El template es `Bootstrap-Admin-Template` v2.4.2 (metisAdmin), un admin de Bootstrap **3**
que el legacy ya está usando forzado sobre **Bootstrap 5**. O sea: hoy ya está medio roto
y compensado con overrides.

Plan pragmático:

1. Copiar tal cual a `src/Ways.Web/src/styles/`:
   - `ways.css` — la identidad de marca (colores por local, badges de tickets guardados)
   - `ticket.css` — la hoja de impresión del ticket (esto **hay que conservarlo exacto**,
     está calibrado contra la impresora térmica)
   - `assets/fonts/ways.ttf` — la fuente de la marca
   - `assets/img/favicon.png`
2. Bootstrap 5 desde npm (no CDN), no `5.2.0-beta1`.
3. `main.css` y `theme.css` (66 KB de metisAdmin de Bootstrap 3): **no copiarlos enteros**.
   Extraer solo lo que se usa: `.box`, `.box header`, `.toolbar`, `.icons`, `.inner`,
   `.outer`, `.Footer`. Son unas 150 líneas.
4. Componentizar en React manteniendo las mismas clases, para que visualmente sea idéntico:
   `<Box>`, `<BoxHeader>`, `<Toolbar>`, `<DataTable>`.

**Lo que sí hay que preservar sin negociar:**
- El color por punto de venta (naranja local 1 / violeta local 2). El cajero se orienta con eso.
- El display gigante del total (`.ways-total` / `.ways-numero`).
- Los atajos de teclado F1/F2/F3/F9/F10/F12 y `+`/`−`.
- El layout del ticket impreso.
- El flujo "escanear → Enter → siguiente" sin tocar el mouse.

Esa última es la más importante: el POS lo usa gente que factura rápido. Cualquier
rediseño que agregue un clic al flujo de venta es una regresión, por más lindo que quede.

---

## Endpoints de la API (primer corte)

```
POST   /api/auth/login                     { usuario, password }
POST   /api/auth/punto-venta               { puntoVentaId }
POST   /api/auth/logout
GET    /api/auth/me

GET    /api/articulos/buscar?q=            por código, EAN o nombre
GET    /api/articulos                      listado paginado + filtros
POST   /api/articulos
GET    /api/articulos/{id}
PUT    /api/articulos/{id}
POST   /api/articulos/{id}/codigos-barra
DELETE /api/articulos/{id}                 soft delete

GET    /api/ventas/en-curso                el carrito del operador
POST   /api/ventas/en-curso/lineas         { codigo, cantidad }  → recalcula ofertas
DELETE /api/ventas/en-curso/lineas/{id}
PUT    /api/ventas/en-curso/cliente
PUT    /api/ventas/en-curso/direccion
POST   /api/ventas/en-curso/guardar        → slot en espera
POST   /api/ventas/en-curso/recuperar/{n}
DELETE /api/ventas/en-curso                descartar
POST   /api/ventas                         cerrar la venta (transaccional)
GET    /api/ventas/{id}/ticket

GET    /api/caja/actual                    parcial en vivo
POST   /api/caja/cerrar
POST   /api/caja/retiro
GET    /api/caja/tickets                   tickets sin cerrar
POST   /api/ventas/{id}/anular
POST   /api/ventas/{id}/restaurar
PUT    /api/ventas/{id}/cliente            reasignar

GET    /api/gastos
POST   /api/gastos
DELETE /api/gastos/{id}

GET    /api/clientes
POST   /api/clientes
GET    /api/clientes/{id}/cuenta-corriente
POST   /api/clientes/{id}/pagos
POST   /api/clientes/{id}/ajustes
POST   /api/clientes/{id}/actualizar-precios

GET    /api/stock/resumen
GET    /api/reportes/cajas
GET    /api/reportes/caja-general
GET    /api/reportes/caja-virtual
```

> **El carrito se mueve del `$_SESSION` a la base** (tabla `ventas_en_curso` con estado
> `borrador`). Así sobrevive a un F5, a que se cierre el navegador y a que se corte la luz.
> Es la mejora de robustez más grande por menos esfuerzo de toda la reescritura.

---

## Cosas que hay que arreglar mientras se reescribe (no son "features nuevas")

| # | Bug del legacy | Corrección |
|---|---|---|
| 1 | Restaurar ticket **suma** stock en vez de restarlo | invertir el signo + `stock_movimientos` |
| 2 | Venta + stock + saldo sin transacción | una sola transacción |
| 3 | Stock descontado sin validar disponibilidad | validar y avisar (o permitir negativo, pero explícito) |
| 4 | Stock global para 2 locales | `stock` por punto de venta |
| 5 | Contraseñas en texto plano | hash |
| 6 | SQL injection en todo | EF Core parametrizado |
| 7 | Importes de cierre de caja editables desde el navegador | recalcular en el servidor, ignorar lo que mande el cliente |
| 8 | Endpoints AJAX sin auth | todos autenticados |
| 9 | Tolerancia $10 / vuelto $20 hardcodeados | configuración por punto de venta |
| 10 | IVA 21% hardcodeado en `ver-todos` | configuración |
| 11 | Sin roles aplicados | autorización por endpoint |
