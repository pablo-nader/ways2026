# Familias que permiten desglosar el costo

## Objetivo

Una familia puede marcar **permite desglosar costo**. Con la marca activa, cada miembro tiene su propia
composición del costo (costo de lista, por unidad o bulto, con o sin IVA, descuento del proveedor e
impuesto interno), pero todos comparten el mismo `costo_nominal`, y por eso el mismo precio de venta.

## Problema y por qué

Muchas bebidas de una misma familia traen impuestos internos distintos en la factura del proveedor:
el neto y el interno varían, pero el costo final es el mismo, y también el precio de venta. Hoy la familia
obliga a que la composición sea idéntica, así que esos artículos no pueden estar en la misma familia sin
falsear el neto o el interno.

## Decisiones

- Columna nueva `familias.permite_desglosar_costo boolean NOT NULL DEFAULT false`. No hace falta backfill:
  las familias existentes quedan como están.
- **Composición** (propia del miembro cuando la marca está activa): `costo_lista`, `costo_lista_por`,
  `costo_lista_incluye_iva`, `descuento_proveedor`, `impuesto_interno`.
- Siguen compartidos siempre: los otros once campos, incluidos `id_alicuota_iva`, `unidades_por_bulto` y
  `costo_nominal`, más los precios de las listas fijas.
- **Edición de un miembro** (decisión del dueño, 2026-10-30): si la composición nueva da el mismo nominal de
  la familia, se guarda solo ese artículo, sin preguntar el alcance. Si da otro nominal, se pregunta el alcance
  como hoy: `Familia` aplica el nominal nuevo a todos los miembros, que conservan su composición; `SoloEste`
  saca al artículo de la familia.
- **Alta dentro de la familia**: hereda el nominal de la familia. Una composición que dé otro nominal es
  `409 costo_nominal_distinto_de_familia`.
- **Agrupar** en una familia con la marca: la alineación con la referencia no toca la composición.
- **Apagar la marca** exige que todos los miembros tengan la misma composición; si no, `409 composicion_distinta`.
- **Compras**: sin cambios; ya escriben solo `costo_nominal`, uno por familia.

## Alcance autorizado

Backend (dominio, migración, servicios de artículos, familias y agrupación, contratos), web (pantallas de
Familias y formulario de artículo) y doc 10 (parte 03 de artículos y familias).

## Tareas

Estrategia de entrega: `ask-on-risk` con `stacked-to-main`. Pronóstico: unas 700 líneas en dos PRs.

- [x] **T1 — backend** (delegado: más de dos archivos no triviales). Migración, `ValoresCompartidosDeFamilia`
      con la composición condicional, edición, alta, agrupación, alta y edición de la familia con la marca,
      apagar la marca, doc 10; tests unitarios y de integración. Commit `bd358a0c`. Pendiente: correr
      `FamiliasDesglosanCostoTests` y el resto de las clases de integración de familias/artículos (Docker no
      disponible en la sesión del escritor; las pruebas de integración compilan pero NO se ejecutaron).
- [ ] **T2 — web** (delegado). Check en NuevaFamilia y Familia, formulario de artículo con la composición
      editable sin alcance cuando la marca está activa y la pregunta de alcance que muestra el nominal
      anterior y el nuevo; tests Vitest colocados.

## Criterios de aceptación

- Con la marca, editar el interno y el neto de un miembro sin cambiar el nominal guarda solo ese miembro.
- Con la marca, un nominal distinto pide el alcance; `Familia` replica el nominal y conserva las composiciones.
- Sin la marca, el comportamiento actual no cambia (las pruebas existentes siguen verdes).
- Apagar la marca con composiciones distintas responde 409 y no cambia nada.

## Progreso

- 2026-10-30: exploración hecha y decisión del dueño sobre el nominal distinto. Siguiente: T1.
- 2026-10-31: T1 hecha (`bd358a0c`). Modelo: `familias.permite_desglosar_costo boolean NOT NULL DEFAULT false`
  (migración `FamiliasPermitenDesglosarCosto`, sin backfill). Verificación observada: `dotnet build Ways.slnx`
  0 errores; `Ways.Domain.Tests` 2081 verdes; `Ways.Application.Tests` 1369 verdes (incluidas las pruebas
  estructurales de escritores y de posición de locks, actualizada la de `ActualizarAsync`). Mutaciones: filtrar la
  composición en `CamposDistintos` y `PuedeApagarElDesgloseDeCosto <= 2` hacen fallar 17 casos del dominio;
  invertir `if (pidioApagar && familia.PermiteDesglosarCosto)` rompe la prueba estructural. NO verificado: las
  pruebas de integración (`FamiliasDesglosanCostoTests` y las existentes de familias/artículos) por falta del
  daemon de Docker; sin RED observado en el nivel de servicio. Contrato nuevo: `permiteDesglosarCosto` en
  `FamiliaListado`, `FamiliaDetalle`, `EdicionFamilia` (opcional), `AltaDeFamilia` y `SolicitudDePrevisualizacion`
  (opcionales); errores `composicion_distinta` y `costo_nominal_distinto_de_familia`; `alcance_requerido` con
  `datos.costoNominalAnterior/costoNominalNuevo` solo si la familia tiene la marca. Siguiente: T2 y correr la
  integración con Docker.
