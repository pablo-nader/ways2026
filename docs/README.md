# Ways 2026 — Documentación

Reescritura del ERP/POS Ways. El sistema actual es una aplicación PHP monolítica
(carpeta `alsina/`) que opera dos puntos de venta reales.

## Índice

| Doc | Contenido |
|---|---|
| [00 — Inventario del legacy](00-inventario-legacy.md) | Stack, estructura de archivos, modelo de request, auth, código muerto, deuda técnica, volumetría |
| [01 — Features existentes](01-features-existentes.md) | Catálogo completo de funcionalidades y reglas de negocio. **El contrato de paridad funcional** |
| [02 — Base de datos actual](02-base-de-datos-actual.md) | Las 21 tablas MySQL, sus relaciones y sus problemas |
| [03 — Modelo destino](03-modelo-destino-postgres.md) | Schema PostgreSQL propuesto y mapeo legacy → destino |
| [04 — Plan de migración de datos](04-plan-migracion-datos.md) | Estrategia en dos etapas, el parser de `ventas.articulos`, validación y cutover |
| [05 — Arquitectura nueva](05-arquitectura-nueva.md) | .NET + React/Vite/TS + Postgres, Docker, endpoints, estilos |
| [06 — Roadmap](06-roadmap.md) | Fases, criterios de salida y qué hay que hacer hoy |
| [07 — Despliegue](07-despliegue.md) | EasyPanel, variables de entorno, modo todo-en-uno y desarrollo local |
| [08 — Usuarios y login](08-usuarios-y-login.md) | Primera tabla del sistema nuevo: esquema, reglas de rol y sesión |

## Estado

- [x] Relevamiento del legacy
- [x] Documentación de features
- [x] Documentación del schema actual
- [x] Modelo destino
- [x] Plan de migración
- [ ] Fase 0 — continuidad operativa en la VM nueva
- [ ] **Fase 1 — fundaciones** (en curso)
  - [x] Solución .NET 10 + React 19 / Vite 8 / TypeScript
  - [x] Contenedor Docker, en modo todo-en-uno y con base externa
  - [x] Tablas `roles` y `usuarios`
  - [x] Login con sesión por cookie, 1 h de inactividad
  - [x] ABM de usuarios con reglas de rol
  - [ ] Resto del schema, tabla por tabla

## Los tres números que importan

- **345.665** ventas históricas, con el detalle serializado como string dentro de una columna `text`.
- **5.992** artículos activos con 5 precios cada uno y 3 tipos de oferta.
- **2** puntos de venta compartiendo una única columna de stock.
