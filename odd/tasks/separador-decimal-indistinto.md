# Separador decimal indistinto ("." o ",")

## Objetivo

Que cualquier campo numérico de Ways.Web acepte "." y "," como separador decimal, y que un combo
con cantidad fraccionaria (p. ej. 0,25) se pueda cargar en el POS.

## Problema

- El selector de miembros de un combo de familia (`componentes/SelectorDeMiembrosDeCombo.tsx`) usa
  `<input type="number">`: el navegador rechaza "0,25" como número inválido (validación nativa) y el
  valor llega vacío → 0, así que el combo nunca cierra.
- Muchos campos parsean con `Number(texto)` (solo punto) o usan `type="number"`; otros ya aceptan
  ambos. No hay un parser compartido.

## Alcance

- Parser compartido de cantidades/números decimales que acepta "." o "," (un solo separador, sin miles).
- Reemplazar `type="number"` por `type="text"` + `inputMode="decimal"|"numeric"` con el parser compartido.
- Reemplazar los `Number(texto)` sobre texto tipeado por el parser compartido.
- Fuera de alcance por ahora: `parsearImporte` (campos de dinero, "." = miles). Pendiente decisión del dueño.

## Restricciones

- Paso/mínimo por unidad de venta siguen en `api/cantidadPorUnidad.ts`.
- Tests Vitest por cada parser/helper tocado (skills `web-descriptor-tests`, `web-test-data-gates`).

## Tareas

- [x] T1 — Parser compartido + selector de miembros de combo + cantidad de línea del POS + escaneo offline `qty*code`. Ruta: delegada (writer: 2+ archivos no triviales).
- [x] T2 — Resto de campos numéricos (stock, compras, presupuestos, remitos, OC, ofertas, artículos, precios, gastos, caja, catálogos, familias, empresas, categorías, auditoría). Ruta: delegada.

## Criterios de aceptación

- "0,25" y "0.25" se aceptan igual en el selector de miembros y en la cantidad de línea del POS.
- Ningún campo numérico de entrada del usuario usa `type="number"` ni `Number(texto)` sin normalizar.
- Suite web en verde.

## Evidencia

T1: `npx tsc -b`: sin errores; `npm run lint`: 0 errores (warnings preexistentes); `npm test`: 242 archivos / 7450 tests en verde. Parser: `formato/numeros.ts` (`parsearNumeroDecimal`, `parsearEnteroTipeado`, `formatearNumeroDecimal`). T2: `npx tsc -b`: sin errores; `npm run lint`: 0 errores; `npm test`: 242 archivos / 7464 tests en verde. Ya no queda `type="number"` ni `Number(texto)` sobre texto tipeado salvo: ids/selects/URL, importes que vienen de `CampoImporte` (`String(n)`: caja.ts, EditorDePrecios, costoLista/costoNominal) y los parsers con regex propio (combosAbm, carrito, ajusteManual, cuentaDePersonal, importeDeVenta). Nota: el servidor (`ParserDeEscaneo`) lee la coma del prefijo `cantidad*codigo` como miles; el offline ahora acepta coma, el online no (seguimiento).

## Próximo paso

T3 (inline, 2 archivos mecánicos): `ParserDeEscaneo` acepta "," o "." en el prefijo `cantidad*codigo` (antes "0,5*x" daba 5 y "1,2,3*x" daba 123). RED 3 fallos → GREEN `dotnet test tests/Ways.Domain.Tests`: 1701/1701. Luego judgment-day y PR.

## Judgment-day

Ronda 1 sobre 5b260ffa..33c90e39: 0 críticos confirmados → `JUDGMENT: APPROVED`. Warnings arreglados igual (RED→GREEN): vista previa de costo nominal con coma (`costoNominal.ts`) y `+3*x` alineado entre servidor y offline. Dominio 1702/1702, web 242 archivos / 7465 tests, tsc y lint sin errores.

Seguimientos (info): el selector de miembros del combo y "Días de alerta" de Vencimientos no marcan el texto inválido; `atributosDeCampoDeCantidad` ya no aplica `min`; los campos editables se siembran con "." (`String(n)`); `parsearImporte` (dinero, "." = miles) sigue igual.
