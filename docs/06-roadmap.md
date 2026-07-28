# 06 — Roadmap

## La restricción que manda

El hosting vence **hoy** y mañana se contrata una VM. La reescritura completa en
.NET + React + Postgres, con migración de 345k ventas validada, **no entra en un día**.
Ni en una semana.

Entonces hay dos problemas distintos y no hay que confundirlos:

| Problema | Plazo | Solución |
|---|---|---|
| **A. Que el negocio siga facturando mañana** | horas | Dockerizar el PHP tal cual y subirlo a la VM |
| **B. Reescribir el sistema bien** | semanas | .NET + React + Postgres, por fases |

Meter A y B en el mismo movimiento es cómo se rompen los negocios. La Fase 0 existe para
comprar tiempo, no para hacer las cosas bien.

---

## Fase 0 — Continuidad operativa (HOY / mañana temprano)

**Objetivo:** que el 28 abran la caja y facturen exactamente igual que ayer.
**Nada de esto es código nuevo. Es empaquetar lo que ya funciona.**

- [ ] **Bajar el backup completo del hosting viejo antes de que expire.** Archivos + dump
      fresco de la base. El dump que tenemos es del relevamiento; hay que sacar uno del
      último minuto. Si esto no se hace hoy, se pierde.
- [ ] Contratar la VM (2 vCPU / 4 GB / 40 GB SSD alcanza y sobra para este volumen).
- [ ] `docker/legacy/compose.yml`: `php:8.2-apache` + `mysql:8` + volumen para la base.
- [ ] `conexion.php` pasa a leer variables de entorno. **Sacar las credenciales del código.**
- [ ] Restaurar el dump fresco en el MySQL del contenedor.
- [ ] Verificar a mano: login → cargar un artículo → cerrar una venta → imprimir ticket →
      cerrar caja. Si el ticket no imprime igual, no está terminado.
- [ ] DNS apuntando a la VM.
- [ ] HTTPS con Caddy o nginx + Let's Encrypt.
- [ ] **Backup automático diario** de la base a un bucket externo. El sistema viejo no tenía.
- [ ] Cambiar la password de la base y de los usuarios operativos (las viejas están en el repo).

**Riesgos conocidos de la Fase 0:**
- PHP 8.2 puede romper con este código (`@` suppression, `mysqli` deprecations, comparaciones
  laxas). Si rompe, bajar a `php:7.4-apache` — funciona, ya no tiene soporte, y no importa
  porque es temporal.
- El charset `latin1` tiene que quedar igual que en el hosting viejo o se rompen los acentos.

---

## Fase 1 — Fundaciones (semana 1)

- [ ] Repo `ways2026` con la estructura de `05-arquitectura-nueva.md`.
- [ ] Solución .NET + proyecto Vite + `compose.yml` de desarrollo.
- [ ] Dockerfile monolítico funcionando (`docker run` → app en `:8080`).
- [ ] Schema Postgres completo como EF Core migrations (modelo de `03-modelo-destino-postgres.md`).
- [ ] Etapa A de la migración: dump legacy cargado en el schema `legacy` de Postgres + checksums.
- [ ] Auth: login, selección de punto de venta, roles, hash de contraseñas.
- [ ] Layout base en React con el CSS del template actual. Que se vea igual.
- [ ] CI mínima: build + tests en cada push.

**Criterio de salida:** entrás con usuario y contraseña, elegís local, ves el layout de Ways
con el color del local. No hay ninguna pantalla funcional todavía.

---

## Fase 2 — Catálogo (semana 2)

- [ ] Etapa B parcial: catálogos (pasos 1–11 del plan de migración).
- [ ] CRUD Artículos con todos los campos del legacy.
- [ ] Códigos de barra múltiples.
- [ ] Marcas, grupos, proveedores, áreas.
- [ ] Listas de precio y precios.
- [ ] Ofertas (artículo y grupo) con la tabla unificada.
- [ ] Stock por punto de venta + tablero de reposición.
- [ ] Búsqueda de artículos (por ID corto, EAN y nombre con trigram).

**Criterio de salida:** el catálogo migrado se ve, se busca y se edita desde la app nueva.

---

## Fase 3 — POS (semanas 3–4) — el corazón

- [ ] Venta en curso persistida en base (no en sesión).
- [ ] Carga por escaneo, por ID corto, por `cantidad*codigo`, por búsqueda por nombre.
- [ ] **Motor de ofertas** con tests unitarios que reproduzcan los casos del legacy,
      incluida la oferta por cantidad en negativo (devoluciones).
- [ ] Cliente, domicilio de entrega, tickets en espera (ahora N, no 3).
- [ ] Pantalla de pago: efectivo / tarjetas / cuenta corriente / vuelto, con las validaciones
      del legacy (tolerancia y vuelto máximo, ahora configurables).
- [ ] Cierre de venta **transaccional**: venta + líneas + stock + saldo de cuenta corriente.
- [ ] Impresión del ticket idéntica al actual (incluida la comanda).
- [ ] Todos los atajos de teclado.

**Criterio de salida:** un cajero factura un día entero en la app nueva sin tocar el mouse
y sin notar diferencias contra el sistema viejo.

**Este es el hito que hay que testear más que ningún otro.** Todo lo demás se puede corregir
al día siguiente; una venta mal cobrada, no.

---

## Fase 4 — Caja y gastos (semana 5)

- [ ] Gastos: alta, listado, borrado, categorías.
- [ ] Retiros de efectivo + comprobante.
- [ ] Tickets sin cerrar: listado, anular (con el bug de stock **corregido**), restaurar,
      reasignar cliente, reimprimir.
- [ ] Parcial de caja.
- [ ] Cierre de caja: totales **calculados en el servidor**, transaccional, con caja general encadenada.
- [ ] Comprobante de cierre.

---

## Fase 5 — Cuenta corriente y reportes (semana 6)

- [ ] Cuenta corriente: movimientos, filtros, pagos, ajustes.
- [ ] Actualización de precios (la reindexación del fiado), con tests: es la lógica más
      delicada del sistema después de las ofertas.
- [ ] Ver Cajas + detalle.
- [ ] Caja General (Z).
- [ ] Caja Virtual con los 4 canales.
- [ ] Dashboard de 7 días.

---

## Fase 6 — Cutover (semana 7)

- [ ] Migración completa Etapa A + B con las 345k ventas.
- [ ] Validación: checksums, tolerancias, revisión de `migracion_errores`.
- [ ] **Operación en paralelo un día**: mismo turno cargado en los dos sistemas, comparar cierres.
- [ ] Capacitación (media hora alcanza si la UI es la misma).
- [ ] Cutover con ventana de mantenimiento.
- [ ] El sistema viejo queda levantado en solo lectura 30 días.

---

## Después del cutover (el "escalarlo y mejorarlo")

No antes. Ninguna de estas entra en la paridad funcional:

- Compras con detalle (la Fase C3 que el legacy nunca terminó) y actualización de costos masiva.
- Stock transferido entre locales.
- Reportes de rentabilidad por artículo / marca / proveedor — **ahora posible**, porque
  `items_venta` es una tabla real.
- Facturación electrónica AFIP/ARCA.
- App de consulta de precios para el salón.
- Etiquetas y carteles de góndola.
- Multi-empresa (hoy los locales son datos, no tenants).
- Notificaciones de bajo stock.

---

## Orden de trabajo propuesto para hoy

1. **Backup del hosting viejo, ya.** Es lo único irreversible.
2. Repo `ways2026` + commit del legacy como referencia histórica.
3. Fase 0: contenedor del PHP + compose + variables de entorno.
4. Recién después, Fase 1.
