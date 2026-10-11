# Padrones en modal

## Objetivo
La pantalla principal de cada padrón muestra solo la barra, los avisos y la grilla. El alta y la
edición abren en la `Modal` compartida (`src/Ways.Web/src/componentes/Modal.tsx`).

## Por qué
Pedido del dueño (2026-10-11): unificar los padrones con el criterio que ya usan Gastos y Artículos.

## Alcance
Solo Ways.Web. Sin cambios de API ni de esquema, salvo `nombreArticulo` en el listado de ofertas (T1).

## Convención (referencia: `Ofertas.tsx` → `ModalDeOferta`, `Gastos.tsx` + `ModalDeEdicionDeGasto.tsx`)
- Título "Nueva/Nuevo <singular>" / "Editar <singular>".
- Guardar y Cancelar en el pie; Guardar envía el formulario con el atributo `form`.
- Guardado correcto: cierra la modal y el aviso queda en la pantalla.
- Guardado rechazado: la modal queda abierta, con lo tipeado y el error adentro.
- La modal no se cierra mientras hay una escritura en curso.
- Errores de carga del listado y de la baja: en la pantalla.

## Tareas
- [x] T1 — Ofertas: nombre del artículo en el alcance. Ruta: delegada (2+ archivos). Commit `84e3da70`. PR #377.
- [x] T2 — Ofertas: alta y edición en modal. Ruta: delegada. Commits `c5da1f53`, `9a7f3b74`. PR #377.
- [x] T3 — `PaginaCatalogo` (áreas, marcas, grupos, medios de pago, listas de precio). Ruta: delegada. Commit `6d4b32c1`. PR #378.
- [ ] T4 — Categorías (`Categorias.tsx`). Ruta: delegada.
- [ ] T5 — Proveedores (`Proveedores.tsx`). Ruta: delegada.

## Verificación
- T1–T2: `ServicioDeOfertasTests` 25 en verde; suite web 4448 en verde; judgment-day sin severos.
- T3: suite web 4458 en verde; judgment-day sin severos ni advertencias.
- RED no observado en T2 y T3: los tests se escribieron después del cambio.
- No se corrió la revisión nativa (`gentle-ai review`) en T1–T3.

## Entrega
PRs apilados sobre `main`: #377 → #378 → T4+T5.

## Seguimientos abiertos (comunes a las modales, sin tocar)
- Tras guardar, el foco no vuelve al botón que abrió la modal (falta `focoDeReserva`).
- El foco inicial cae en el botón de cerrar y no en el primer campo.
- El error dentro de la modal no lleva `role="alert"`.
- `Ofertas.guardar` se protege del doble envío por estado y no por ref síncrona.
