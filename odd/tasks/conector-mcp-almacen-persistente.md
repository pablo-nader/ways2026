# Conector MCP — almacén persistente de OpenIddict (etapa S1)

## Objetivo

Que las conexiones del conector MCP (cliente, autorizaciones, tokens) sobrevivan a los deploys y
reinicios de la app.

## Problema y por qué

El conector usa el proveedor EF InMemory y claves efímeras: cada deploy borra todas las
conexiones y obliga a volver a autorizar desde Claude. El dueño mergea varias veces por día, y la
etapa siguiente (cargar facturas de proveedor como borradores de compra desde Claude) necesita una
conexión estable.

## Alcance autorizado

- `ContextoOAuthDelConector` pasa a Npgsql sobre la misma base de Ways, con migraciones propias y
  tabla de historial propia (`historial_migraciones_conector_mcp`), sin tocar el snapshot de Ways.
- Tablas de plataforma (categoría global, sin tenant ni RLS), nombres en español según doc 10.
- Migraciones del contexto aplicadas al arrancar, solo con `Mcp:Habilitado` encendido.
- Tokens en formato Data Protection (anillo de claves en `DataProtectionKeys`).
- Sin `DisableBulkOperations()`.
- Poda diaria de tokens y autorizaciones vencidos.
- Docs 07 (límites) y 10 (tablas nuevas).

Fuera de alcance: pantalla de revocación, tools nuevas, compras, web. No tocar el bloque
`.AddMcpServer(...).WithTools<...>()` (S3).

## Decisiones

- **Data Protection y no certificados X.509.** OpenIddict formatea access tokens, códigos y
  refresh tokens con el anillo de claves de Data Protection de la app, que ya vive en la base.
  No hace falta administrar certificados ni secretos nuevos. Las claves efímeras de firma y
  cifrado quedan solo porque OpenIddict exige credenciales registradas: con Data Protection no
  firman ni cifran ningún token emitido (no hay identity tokens, el flujo no pide `openid`).
- **Rol de base.** En producción el rol de la app es el dueño de las tablas (ADR-5) y corre
  `MigrateAsync` al arrancar. En los tests `ways_app` tiene `CREATE` sobre `public`, así que la
  migración del conector la aplica ese mismo rol.
- **Cliente sincronizado al arrancar.** Con almacén persistente el cliente ya existe en el segundo
  arranque: se actualiza (redirect URIs, permisos y recursos salen de la configuración) y se borra
  cualquier otro cliente, para que cambiar `Mcp:IdDeCliente` siga dejando sin acceso al anterior.
- **Sin estrategia de reintentos en este contexto.** Los stores de OpenIddict abren transacciones
  propias, incompatibles con la estrategia con reintentos.

## Modelo (evidencia del gate levantado)

| Tabla | Entidad OpenIddict | PK |
|---|---|---|
| `aplicaciones_oauth` | Application | `id_aplicacion_oauth` |
| `autorizaciones_oauth` | Authorization | `id_autorizacion_oauth` |
| `alcances_oauth` | Scope | `id_alcance_oauth` |
| `tokens_oauth` | Token | `id_token_oauth` |

Categoría global (doc 09), sin `id_tenant` ni RLS. Columnas en snake_case español.

## Tareas

- [ ] T1 — Persistencia en Postgres: contexto con mapeo, fábrica de diseño, migración inicial,
  migración al arrancar, cliente sincronizado, Data Protection, sin `DisableBulkOperations`,
  poda diaria, tests y docs. Ruta: inline (un escritor, archivos acoplados).

## Criterios de aceptación

- Un token emitido por un host sirve en otro host que comparte la base (redeploy).
- Reuso de refresh y revocación siguen funcionando contra Postgres.
- La migración del conector se aplica con el rol de la app.
- Con el flag apagado no se crean las tablas.
- Tests existentes del conector en verde; guardas estructurales en verde.

## Checks

- `dotnet build src` sin advertencias nuevas.
- Integración filtrada: `FullyQualifiedName~ConectorMcp`.
- `tests/Ways.Application.Tests` completo (guardas estructurales).

## Progreso

- Exploración terminada; rama `pnader/conector-mcp-almacen-persistente`.

## Próximo paso

T1.
