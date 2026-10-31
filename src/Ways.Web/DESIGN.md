# Mostrador, el sistema visual de Ways

Referencia completa (tokens, 17 componentes, logos): https://claude.ai/artifact/NM2a19CisSaShZ7xA3YrDP

El back office, el POS y la tienda comparten estos tokens. Su modo es **operar**: la gente viene a cargar, buscar y cobrar rápido, así que la legibilidad, la consistencia y la densidad mandan sobre la expresión. La marca vive en detalles precisos: el naranja en la acción principal, la tipografía de títulos y los chevrones del logo.

## Dónde vive

- Los tokens son variables CSS `--ws-*` en `src/estilos/tema.css`: los colores por tema (`[data-bs-theme='light']` y `[data-bs-theme='dark']`), lo demás en `:root`.
- Bootstrap 5.3 sigue funcionando debajo: sus variables `--bs-*` apuntan a los tokens. Las clases que usan las pantallas no se renombran.
- Los alias `--ways-fondo`, `--ways-barra`, `--ways-sombra`, `--ways-naranja`, `--ways-gris` y `--ways-fuente` se conservan para el CSS anterior.
- `src/estilos/componentes.css` se carga justo después de `tema.css` y viste los componentes de Bootstrap (tablas, botones, badges, alertas, navegación, modales, formularios, paginación) reasignando sus variables `--bs-*` a los tokens. Solo usa `var(--ws-*)` y `var(--bs-*)`; `componentes.test.ts` lo verifica. El panel `.box` vive en `template.css`.
- `src/estilos/tema.test.ts` lee `tema.css` y verifica que cada token exista en los dos temas y que los pares cumplan el contraste.
- La tienda pública (`publico.css`, `--pub-*`) tiene su propio tema y solo hereda la tipografía.
- La salida impresa (`@media print`, `impresion.css`) es siempre negro sobre blanco.

## Principios

- **Una acción principal por zona.** Va en `brand` con texto `on-brand`. Todo lo demás es secundario, fantasma o ícono.
- **El dato manda.** En una grilla, lo más fuerte es el nombre del registro y el importe, no los badges ni los bordes.
- **El naranja no es texto.** Para links y texto naranja se usa `brand-ink`; `brand` es solo relleno.
- **El color nunca va solo.** Todo estado lleva palabra o ícono además del color.
- **Los dos temas son de primera.** El oscuro es el modo por defecto; cada par de colores cumple 4.5:1 en los dos.

## Contenido y tono

- Español rioplatense neutro, de vos: "Revisá la conexión", "Elegí un punto de venta".
- Sentence case en todo: "Nuevo artículo", nunca "NUEVO ARTÍCULO" ni "Nuevo Artículo".
- Los botones dicen lo que hacen y el aviso repite el verbo: "Dar de baja" → "Artículo dado de baja."
- Los errores dicen qué pasó y cómo seguir, sin disculpas.
- Nombrar las cosas como las nombra el comerciante: "cuenta", "punto de venta", "lista de precios". Nunca "tenant", "id" ni términos del sistema.
- Plurales reales: "1 artículo", "912 artículos". Nunca "artículo(s)".
- Importes en formato argentino: `$ 14.200,00`. Negativos con signo menos real (−) y en `danger`.
- Sin emoji.

## Tipografía

- Títulos en **Bricolage Grotesque** (`--ws-font-display`); todo lo demás en **Instrument Sans** (`--ws-font-body`).
- Las fuentes se sirven desde el propio bundle con `@fontsource-variable/*`, nunca desde Google Fonts: el POS de escritorio corre sin conexión bajo una CSP que solo admite `font-src 'self'`.
- Un solo `display` (32/38, 700) por pantalla y solo en tableros. Título de página y de modal en `title` (22/28, 700); secciones de formulario en `section` (16/22, 650), siempre como heading real (h2/h3).
- Texto y celdas en `body` (14/20); el nombre del registro en `body-strong` (600); ayudas y pies en `small` (13/18); badges en `caption` (12/16, 600).
- Cifras con `font-variant-numeric: tabular-nums`: `figure` en grillas, alineadas a la derecha; `figure-lg` (26/32, 700) en indicadores y en el total a cobrar.

## Color

- Fondo de página `bg`; trabajo sobre `surface`; encabezados de tabla y barras de filtro en `surface-sunken`; modales y toasts en `surface-raised`.
- Texto en `ink`; secundario (etiquetas, códigos, ayudas) en `ink-muted`.
- Divisores en `line` (decorativos). Bordes de controles en `line-control`, que cumple 3:1.
- Estados: `ok`, `warn`, `danger`, `info`, cada uno con su `-soft` de fondo. `info` es azul para no competir con el naranja.
- `brand-gray` es el gris de los chevrones del logo: solo ilustración, nunca texto ni borde funcional.
- Las franjas de color por punto de venta (`.color_x`) son propias de cada PV y no se reemplazan con estos tokens.

## Espacio, forma y profundidad

- Escala de 4px: `--ws-space-1` (4) a `--ws-space-8` (32). Los grupos de hermanos se separan con `gap`.
- Radios por rol: `radius-md` en controles, `radius-lg` en paneles y grillas, `radius-xl` en modales y botones del POS, `radius-pill` en badges.
- Alturas: `control` (36px) en el back office, `control-sm` (30px) dentro de tablas, `control-lg` (48px) en el POS. Filas de grilla `row` (44px); reportes densos `row-compact` (36px).
- Sombras: `shadow-panel` apenas separa paneles en claro (en oscuro no hay sombra, separa la superficie); `shadow-overlay` solo para lo que flota.
- No se anidan paneles. Dentro de un panel se separa con secciones.

## Foco y estados

- Foco: anillo sólido de 2px en `focus` con 2px de separación, 3:1 sobre `bg`, `surface` y `surface-sunken`.
- Deshabilitado: opacidad .5 y cursor bloqueado; el motivo se explica en texto cerca, no solo en un `title`.
- Hover de fila en `surface-sunken`; fila seleccionada en `brand-soft`. Las tablas no llevan cebra ni bordes verticales: `table-striped` y `table-bordered` quedan en el markup sin efecto visual.
- `btn-success` (el "guardar" de las pantallas) se ve como la acción principal de marca; `btn-secondary`, `btn-outline-secondary` y `btn-light` son el botón secundario con borde `line-control`.
- Mientras se guarda, todo el formulario queda inerte y el botón dice "Guardando…".
- Movimiento mínimo y solo como respuesta a una acción; se respeta `prefers-reduced-motion`.

## Iconografía y logo

- Íconos de trazo propios de `BotonIcono` (24×24, trazo 2, extremos redondeados, `currentColor`). Un botón solo con ícono lleva siempre `aria-label` con la acción y el registro.
- `ways-logo-claro.png` (letras negras) sobre fondos claros; `ways-logo-oscuro.png` (letras blancas) sobre fondos oscuros. No se recolorea ni se deforma; altura mínima en la barra: 26px.

## Tema sin parpadeo

`admin.html` y `pos.html` cargan `public/tema-inicial.js` antes del primer pintado: lee `localStorage['ways.tema']` y, si vale `claro`, pone `data-bs-theme="light"`. Es un archivo externo y no un script en línea porque la CSP del POS de escritorio (`script-src 'self'`) bloquea los scripts en línea. La clave está repetida respecto de `CLAVE_DE_TEMA` en `src/tema/tema.ts`.

## Tokens de color

| Token | Claro | Oscuro | Uso |
| --- | --- | --- | --- |
| `bg` | `#f4f4f2` | `#121211` | Fondo de la página, detrás de paneles y grillas. |
| `surface` | `#ffffff` | `#1b1b1a` | Paneles, grillas, inputs y tarjetas: la superficie de trabajo. |
| `surface-sunken` | `#efefec` | `#161615` | Encabezado de tablas, barra de filtros, campos deshabilitados. |
| `surface-raised` | `#ffffff` | `#242422` | Modales, menús y toasts: lo que flota sobre la página. |
| `line` | `#e2e1dd` | `#31302d` | Divisores entre filas y bordes de paneles. Decorativo. |
| `line-control` | `#86837c` | `#6f6c66` | Borde de inputs, selects, checkboxes y botones secundarios. 3:1 sobre `surface` y `bg`. |
| `ink` | `#1c1b19` | `#ecebe8` | Texto principal. |
| `ink-muted` | `#5f5d58` | `#a6a39d` | Texto secundario: etiquetas, códigos, ayudas y encabezados de columna. |
| `brand` | `#f7941d` | `#f7941d` | Relleno de la acción principal, marca activa en la navegación. Nunca texto sobre claro. |
| `brand-hover` | `#e8830c` | `#ffa53d` | Relleno de la acción principal al pasar el puntero. |
| `on-brand` | `#1c1208` | `#1c1208` | Texto e íconos sobre `brand` y `brand-hover`. |
| `brand-soft` | `#fdebd3` | `#3a2606` | Fila seleccionada, chip activo, badge informativo de marca. |
| `brand-ink` | `#9a5200` | `#f9ab4a` | Links, botón fantasma y texto sobre `brand-soft`: el naranja legible como texto. |
| `brand-gray` | `#c4c4ca` | `#c4c4ca` | Gris de los chevrones del logo. Solo ilustración y portada. |
| `focus` | `#b85f00` | `#f9ab4a` | Anillo de foco de 2px con 2px de separación. |
| `ok` | `#1d7442` | `#5ccb8a` | Texto e ícono de estado correcto sobre `surface` y `ok-soft`. |
| `ok-soft` | `#ddf1e4` | `#14301f` | Fondo de badge y alerta de éxito. |
| `warn` | `#835600` | `#e9b955` | Texto e ícono de advertencia sobre `surface` y `warn-soft`. |
| `warn-soft` | `#fbeec9` | `#352a10` | Fondo de badge y alerta de advertencia. |
| `danger` | `#b3302a` | `#f27f74` | Texto de error, importes negativos y la acción de baja. |
| `danger-soft` | `#f8dcd9` | `#3d1814` | Fondo de alerta de error y del botón Dar de baja. |
| `info` | `#2c5aa0` | `#8fb6f2` | Texto de estado informativo sobre `surface` e `info-soft`. |
| `info-soft` | `#dfe8f6` | `#17253b` | Fondo de badge y alerta informativa. |
| `overlay` | `rgba(20, 19, 17, 0.45)` | `rgba(0, 0, 0, 0.62)` | Velo detrás de un modal. |
| `shadow-panel` | `0 1px 2px rgba(28, 27, 25, 0.06)` | `none` | Paneles y grillas sobre `bg`. |
| `shadow-overlay` | `0 12px 32px rgba(28, 27, 25, 0.18), 0 2px 6px rgba(28, 27, 25, 0.08)` | `0 12px 32px rgba(0, 0, 0, 0.55), 0 0 0 1px rgba(255, 255, 255, 0.06)` | Modales, menús y toasts. |

Todos se declaran como `--ws-<token>`. Las variables `--bs-*-rgb` de Bootstrap repiten el valor como tripleta; si cambia un token hay que cambiar también su tripleta.
