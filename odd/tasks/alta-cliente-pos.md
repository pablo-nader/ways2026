# Alta rápida de clientes desde el POS

## Objetivo

El POS puede dar de alta un cliente básico (nombre, teléfono, domicilio) en el momento de la venta, típicamente para una venta con envío.

## Problema y por qué

Hoy solo el Admin crea clientes (`POST /api/clientes` exige `GestionDeCatalogo`). El vendedor que toma un pedido con envío no puede registrar al cliente nuevo y la venta con envío exige un cliente no CF con domicilio.

## Decisiones del dueño (2026-11-01)

- Datos mínimos: nombre, teléfono, domicilio.
- Siempre sin cuenta corriente: límite 0, sin crédito ilimitado. El crédito se sigue configurando desde el back office (solo Admin).
- Solo con conexión: sin red el botón queda deshabilitado; no hay cola offline de clientes.

## Decisiones de diseño

- Endpoint nuevo `POST /api/clientes/rapido` bajo `OperacionDePos` (Vendedor, Supervisor, Admin). DTO mínimo `AltaRapidaDeCliente { Nombre, Telefono?, Domicilio? }`; el servidor fija condición fiscal Consumidor Final, lista de precios por defecto del tenant, empresa NULL (compartido), límite 0, crédito ilimitado false, activo true. El DTO no acepta crédito (dto-contract-honesty).
- Responde el mismo DTO que el alta del back office.
- Tras crearlo, el POS lo selecciona y lo agrega a la copia local (instantánea) para que pueda vender con él aunque después se corte la red.
- Modelo de datos: sin cambios de esquema.

## Alcance

- Backend: endpoint, servicio, tests unitarios e integración (autorización por rol, defaults forzados, validación de nombre).
- Web/POS: botón "Nuevo cliente" junto al selector de clientes, modal con tres campos, deshabilitado sin conexión, selección automática y alta en la copia local.
- Fuera de alcance: alta offline, deduplicación por teléfono, edición desde el POS.

## Tareas

- [x] T1 — Backend: `POST /api/clientes/rapido` con defaults forzados y tests. Ruta: delegada (2+ archivos no triviales). Evidencia: build OK, Domain ValidadorDePagos 52 OK, Application.Tests 1637 OK, integración filtrada 23 OK. Commit `feat(clientes): alta rápida desde el POS…`.
- [x] T2 — POS web: modal de alta rápida, selección y copia local, con tests. Ruta: delegada (2+ archivos no triviales). Evidencia: RED observado, tsc limpio, vitest completo 9352 OK, oxlint sin errores. Commit `feat(pos): botón Nuevo cliente…`.
- [ ] T3 — Judgment-day, PR(s), release del POS pendiente del dueño.

## Criterios de aceptación

- Un Vendedor crea un cliente con solo nombre; queda CF, lista default, límite 0, no ilimitado.
- Un pago en cuenta corriente con ese cliente se rechaza (`limite_credito_excedido`).
- Nombre vacío → 400.
- En el POS, sin conexión el botón está deshabilitado; con conexión el cliente creado queda seleccionado y habilita el envío si tiene domicilio.

## Progreso

- Exploración hecha. Worktree `.claude/worktrees/alta-cliente-pos`, rama `pnader/alta-cliente-pos`.
- T1 backend implementado (sin commit): `POST /api/clientes/rapido`, `ServicioDeClientes.CrearRapidoAsync` (delega en `CrearAsync`), tests de integración, dominio y allowlist de `SuperficieDeAutorizacion`; Application.Tests (1637) e integración filtrada (23) en verde.

## Siguiente paso

T3: judgment-day sobre los dos commits; ~650 líneas → dos PRs stacked-to-main (backend, luego web). Después, release del POS (bump Ways.Desktop + tag pos-v*) con OK del dueño.
