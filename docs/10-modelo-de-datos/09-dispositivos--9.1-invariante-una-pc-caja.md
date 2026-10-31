> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 9. Dispositivos de escritorio (stage-desktop-pos)

## 9. Dispositivos de escritorio (stage-desktop-pos)

Un dispositivo Tauri (`{server}/pos.html`) se vincula UNA VEZ, por un Admin, a un punto de venta
del tenant; después cualquier cajero autorizado inicia sesión contra ESE dispositivo con
usuario + contraseña (doc 08 "En el front", desviación: acá SÍ hay una superficie sin sesión
previa —`pos.html`— porque el dispositivo mismo es la unidad de confianza, no el navegador).

```sql
dispositivos (                -- [operativa] id_tenant + id_punto_venta, doc 09
    id_dispositivo,
    id_punto_venta   integer NOT NULL,        -- FK compuesta (id_punto_venta, id_tenant)
    nombre           varchar(100) NOT NULL,   -- etiqueta humana ("Caja 1", "Mostrador")
    token_hash       char(64) NOT NULL,       -- SHA-256 hex del secreto — el secreto en sí
                                              -- NUNCA se persiste, solo viaja en la cookie
                                              -- HttpOnly ways.dispositivo
    id_usuario_alta  integer NOT NULL,        -- FK simple a usuarios: quién lo vinculó
    ultimo_uso_at    timestamptz NULL,        -- se actualiza en cada login-dispositivo exitoso
    created_at, updated_at, deleted_at        -- baja = revocación, siempre lógica
);
-- UNIQUE (token_hash)                              -- ux_dispositivos_token_hash
-- UNIQUE (id_tenant, id_punto_venta)                -- ux_dispositivos_punto_venta_activo
--   WHERE deleted_at IS NULL                        -- (invariante "una PC-caja = un punto de
--                                                    --  venta", ver más abajo; reemplaza el
--                                                    --  ix_dispositivos_tenant_punto_venta
--                                                    --  no-único de la migración original)
```

**RLS con la misma excepción de login que `usuarios` (doc 09/doc 08).** El dispositivo se tiene
que poder resolver por `token_hash` ANTES de que exista sesión alguna, así que además de la
policy estándar `dispositivos_tenant` lleva una policy de **SOLO LECTURA** en modo `login`
(`dispositivos_login_lectura`, mismo patrón que `usuarios_login_lectura`) — a diferencia de
`usuarios`, acá no hace falta una policy de *escritura* en modo login: `ServicioDeDispositivos`
resuelve el dispositivo (modo `Login`), pasa el contexto a modo `Tenant` en cuanto conoce el
tenant del dispositivo, y recién ahí actualiza `ultimo_uso_at` bajo la policy estándar.

**Estado (stage-desktop-pos): implementada.** Creada por la migración `DispositivosPos`.
`GET /api/dispositivos/actual` (anónimo) resuelve el dispositivo de la cookie; `POST /api/dispositivos`
(Admin) vincula uno nuevo y setea la cookie; `GET`/`DELETE /api/dispositivos` (Admin) listan y
revocan (baja lógica). `POST /api/auth/login-dispositivo` reusa el núcleo de
`ServicioDeAutenticacion` (timing-safe, lockout, rehash) con la lista de roles del POS
(Vendedor/Supervisor/Admin) y emite la misma cookie `ways.sesion` con una claim extra
(`ways:id_dispositivo`) y un `ExpiresUtc` propio de 365 días — `Program.cs`,
`OnValidatePrincipal`, revalida en cada request que ese dispositivo siga vigente (no revocado,
su punto de venta no dado de baja), igual que ya revalida el usuario y el tenant. Detalle
completo del flujo en doc 08 §"Login de dispositivo".

### 9.1 Invariante "una PC-caja = un punto de venta" (stage-desktop-pos)

`puntos_venta` gana la columna `modo`, tipo nativo `modo_punto_venta` (`escritorio`, `web`),
`NOT NULL` y **sin default de base**: el modo se elige explícitamente al crear el punto de venta,
nunca se infiere. Ortogonal a `numero_fiscal` — ninguno de los dos condiciona al otro.

```sql
puntos_venta (
    ...,                          -- columnas preexistentes (doc 01/09)
    modo   modo_punto_venta NOT NULL   -- 'escritorio' | 'web', sin default
);
```

`puntos_venta` también lleva `id_sucursal integer NOT NULL` (FK compuesta a `sucursales`, ver doc 10 §1;
sucursales T1): cada caja pertenece a una sucursal de su empresa, hoy la suya propia 1:1.

`puntos_venta` también lleva `envio_por_defecto boolean NOT NULL DEFAULT false` (ventas con
envío): si es `true`, las ventas nuevas del POS de ese punto de venta arrancan con "Con envío"
tildado y el cajero puede cambiarlo en cada venta. Se edita con `PUT /api/puntos-venta/{id}`
(no en el alta: nace en `false`) y el POS lo lee del mismo listado (`GET /api/puntos-venta`).

El backstop físico de "a lo sumo un dispositivo activo por punto de venta" es
`ux_dispositivos_punto_venta_activo` (§9). La aplicación agrega dos capas más, ambas en
`ServicioDeVentas.ResolverPuntoVentaAsync` (checkout) y `ServicioDeDispositivos.CrearAsync`
(vinculación):

- Un actor con la claim `ways:id_dispositivo` (sesión de dispositivo) solo puede vender contra el
  punto de venta **Escritorio** que ESE dispositivo tiene vinculado — nunca otro, aunque sea del
  mismo tenant.
- Un actor sin esa claim (sesión web normal) solo puede vender contra un punto de venta **Web**.
- Vincular un dispositivo nuevo exige que el punto de venta destino ya sea **Escritorio**.

Cualquier otra combinación es `409 punto_venta_modo_incompatible` — nunca `404`: el punto de
venta existe y es del tenant correcto, lo que falla es la compatibilidad de modo.

El **flip administrativo** de modo (`POST /api/puntos-venta/{id}/modo`, `Politicas.
GestionDeOrganizacion`) exige que el punto de venta NO tenga un dispositivo activo vinculado
(`409 punto_venta_con_dispositivo_activo` si lo tiene — el admin lo revoca primero desde la
pantalla de Dispositivos) y queda auditado vía `ServicioDeAuditoria` (`pv.modo`). Nota: cuando
exista la cola de escritura offline del POS de escritorio, este flip además va a tener que exigir
esa cola vacía — flipear a Web con ventas pendientes de sincronizar las dejaría huérfanas.

**Backfill (migración `ModoPuntoVentaYDispositivoActivoUnico`):** un punto de venta con un
dispositivo activo (`deleted_at IS NULL`) al momento de migrar pasa a `Escritorio`; cualquier
otro, a `Web`. La migración también resuelve determinísticamente cualquier violación preexistente
de la unicidad (conserva el dispositivo de mayor `ultimo_uso_at`, desempate por `created_at` y
después `id_dispositivo`, ambos DESC) antes de crear el índice único.

**Estado (stage-desktop-pos): implementada.**

**Alcance de `PoliticaDeModoDePuntoVenta` (revisión adversarial post-stage-17).** Hasta acá esta
sección describía la regla escrita en singular ("un actor... solo puede **vender**...") sin dejar
registrado qué otros caminos de escritura la comparten — ausencia que dejó pasar un hallazgo: un
actor de dispositivo podía crear/editar/emitir remitos, presupuestos y órdenes de compra contra
CUALQUIER punto de venta de su tenant, no solo el suyo. El alcance real, explícito:

- **Las dos mitades** (compatibilidad de modo completa, dispositivo↔Escritorio y web↔Web) aplican
  a los emisores que numeran desde `numeraciones_comprobante` (doc 09, "salvo la numeración"): el checkout
  (`ServicioDeVentas`), `ServicioDeFacturacionDeRemitos`, `ServicioDeCuentaCorriente.
  RegistrarPagoAsync`, más `POST /api/ventas/reservas-numeracion` y `pos/instantanea` (la claim de
  dispositivo ahí es una condición adicional sobre el mismo espacio de numeración, no una regla
  distinta).
- **Solo la mitad dispositivo** aplica a remitos, presupuestos y órdenes de compra
  (`PoliticaDeModoDePuntoVenta.ExigirPuntoVentaPropioDelDispositivoAsync`): un actor de dispositivo
  únicamente puede tocar el punto de venta que ese dispositivo tiene vinculado. La superficie
  cubierta es **crear, editar, emitir/enviar, anular (los tres documentos) y cerrar (órdenes de
  compra)** — el guard se re-verifica en cada uno de esos write sites, nunca solo al crear/editar.
  La razón no es la numeración (REM/PRES/OC son filas separadas en `numeraciones_comprobante` por PK
  `(id_punto_venta, tipo_comprobante)`, doc 09 — un bloque `TX` reservado offline no puede colisionar
  con ellas): es que `ServicioDeRemitos.EmitirAsync` decrementa `stock`/`stock_lotes` del punto de
  venta del documento, y `ordenes_compra.id_punto_venta` es el destino de recepción que
  `ServicioDeCompras` usa después — autorización + integridad operativa, no numeración. **Anular
  importa más que ninguno de los otros**: la reversa de un remito (`ServicioDeRemitos.AnularAsync`)
  vuelve a escribir `stock`/`stock_lotes` en el punto de venta del documento, así que sin el guard un
  dispositivo podía anular un remito ajeno y mover stock de un local que no es el suyo — el mismo
  daño concreto que emitir, del lado inverso. **La mitad web NO existe acá, a propósito**: un actor
  web sigue eligiendo cualquier punto de venta de su tenant, en cualquier `modo`, para estos tres
  documentos (§9 arriba, "la selección de punto de venta del legacy (A2) se conserva";
  `openspec/specs/operacion-de-pos/spec.md`, escenario "Same user operates two puntos de venta in
  sequence" — agregar esa mitad rompería el back office de cualquier tenant cuyo único punto de
  venta sea una caja Escritorio).
- **Exenciones nombradas, sin cambios:** `ServicioDeFacturacionFiscal` numera desde
  `numeraciones_fiscales` (espacio distinto, doc 09/10 fiscal); `ServicioDeCuentaCorriente.
  RegistrarAjusteAsync` no emite ningún comprobante.
