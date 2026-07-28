# 07 — Despliegue

La imagen funciona en **dos modos** y decide sola cuál usar:

| Modo | Cuándo | Qué hace |
|---|---|---|
| **Base externa** | Hay `ConnectionStrings__Ways` o `DATABASE_URL` | Se conecta ahí. No levanta PostgreSQL adentro |
| **Todo en uno** | No hay ninguna de las dos | Inicializa y levanta su propio PostgreSQL 17 en `/var/lib/postgresql/data` |

En los dos casos la API aplica las migraciones y siembra roles y cuenta root al arrancar.
Es idempotente: se puede reiniciar todas las veces que haga falta.

---

## EasyPanel (servicio de app + servicio de base)

### 1. Servicio de base

Creá un servicio **Postgres**. EasyPanel te da una cadena así:

```
postgres://usuario:clave@nombre-interno:5432/basededatos?sslmode=disable
```

Usá el **host interno** (`aipos_aipos-postgres`), no el público: el tráfico no sale de la red del panel.

### 2. Servicio de app

| Campo | Valor |
|---|---|
| Source | GitHub → `pablo-nader/ways2026`, rama `main` |
| Build method | **Dockerfile** |
| Dockerfile path | `docker/Dockerfile` |
| Build context | `/` (la raíz del repo, **no** `docker/`) |
| Port | `8080` |

Dos cosas que se equivocan siempre:

- **El build context.** El `Dockerfile` copia `src/` y `Ways.slnx` desde la raíz.
  Si le ponés `docker/` como contexto, el build falla en el primer `COPY`.
- **El puerto.** EasyPanel asume 3000 por defecto. Si el build sale bien, el contenedor
  loguea `Now listening on: http://[::]:8080` y aun así el dominio devuelve
  **502 "Service is not reachable"**, es esto: el proxy está golpeando el puerto equivocado.

No hace falta pasar `INCLUIR_POSTGRES`: viene apagado por defecto, que es lo que corresponde
cuando la base es un servicio aparte. Encenderlo suma ~270 MB a la imagen sin usarse.

### 3. Variables de entorno

```env
ConnectionStrings__Ways=postgres://usuario:clave@aipos_aipos-postgres:5432/aipos?sslmode=disable
ASPNETCORE_ENVIRONMENT=Production

Semilla__Root__Usuario=root
Semilla__Root__Mail=test@test.com
Semilla__Root__Password=root
```

Notas:

- El **doble guión bajo** (`__`) es la forma de anidar secciones de configuración en .NET.
  `Semilla__Root__Password` es la sección `Semilla:Root`, clave `Password`.
- La cadena se acepta tanto en formato URI (`postgres://…`) como en el nativo de Npgsql
  (`Host=…;Port=…`). El conversor está en
  [CadenaDeConexion.cs](../src/Ways.Infrastructure/Persistencia/CadenaDeConexion.cs).
- `ASPNETCORE_URLS=http://+:8080` ya viene fijado en el `Dockerfile`. No hace falta ponerlo.
- La semilla root **solo corre si no existe ninguna cuenta root**. Una vez creada,
  cambiar estas variables no la modifica: la contraseña se cambia desde el ABM.

### 4. Salud

EasyPanel puede usar `GET /api/salud`, que es público y devuelve:

```json
{ "estado": "ok", "baseDeDatos": "ok" }
```

Responde `503` si la base no contesta.

### 5. Después del primer deploy

1. Entrá a la URL del servicio → te manda a `/login`.
2. Ingresá con `root` / `root`.
3. **Cambiá la contraseña de root desde Usuarios** antes de cargar nada real.
4. Creá tu cuenta admin y usá esa para el día a día.

---

## Detrás del proxy inverso

EasyPanel termina el TLS y le habla HTTP al contenedor. La app lee `X-Forwarded-Proto`
(`UseForwardedHeaders` en [Program.cs](../src/Ways.Api/Program.cs)) para saber que la
conexión original era HTTPS y marcar la cookie de sesión como `Secure`.

Sin eso, la cookie viajaría sin el flag `Secure` aunque el usuario esté sobre HTTPS.

Las redes y proxies conocidos se vacían a propósito, porque en Docker la IP del proxy
es dinámica. Es seguro mientras el contenedor **solo** sea alcanzable a través del proxy;
si algún día lo exponés directo a internet, hay que restringirlo.

---

## Sesiones y redeploys

Las claves de Data Protection —las que firman la cookie— se guardan en la tabla
`"DataProtectionKeys"` de la base, no en el disco del contenedor.

Sin esto, cada redeploy generaría claves nuevas y **todos los usuarios logueados se caerían**.
Verificado: recrear el contenedor entero mantiene la sesión abierta.

La sesión vence tras **1 hora de inactividad**. Cada request dentro de esa ventana la renueva.
Al cerrar el navegador la cookie sobrevive (es persistente).

---

## Local

### Todo en uno

```bash
docker compose up -d --build
```

→ `http://localhost:8080`. La base queda en el volumen `ways_ways-pgdata`.

### Desarrollo

```bash
docker compose -f compose.dev.yml up -d
```

```bash
dotnet run --project src/Ways.Api
```

```bash
npm --prefix src/Ways.Web run dev
```

- API en `http://localhost:5080`
- Front en `http://localhost:5173`, con proxy de `/api` al 5080.

### Migraciones

```bash
dotnet ef migrations add NombreDeLaMigracion --project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure --output-dir Persistencia/Migraciones
```

No hace falta aplicarlas a mano: la API las corre al arrancar.

---

## Checklist antes de considerarlo productivo

- [ ] Contraseña de root cambiada desde el ABM.
- [ ] Cuenta admin propia creada; root queda solo para emergencias.
- [ ] `POSTGRES_PASSWORD` de la base cambiada (la que se compartió en chat quedó comprometida).
- [ ] Backup automático de la base configurado en el panel.
- [ ] Dominio con HTTPS.
- [ ] `ASPNETCORE_ENVIRONMENT=Production` (así `/openapi` no queda expuesto).
