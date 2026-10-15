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
- [ ] T2 — Resto de campos numéricos (stock, compras, presupuestos, remitos, OC, ofertas, artículos, precios, gastos, caja, catálogos, familias, empresas, categorías, auditoría). Ruta: delegada.

## Criterios de aceptación

- "0,25" y "0.25" se aceptan igual en el selector de miembros y en la cantidad de línea del POS.
- Ningún campo numérico de entrada del usuario usa `type="number"` ni `Number(texto)` sin normalizar.
- Suite web en verde.

## Evidencia

T1: `npx tsc -b`: sin errores; `npm run lint`: 0 errores (warnings preexistentes); `npm test`: 242 archivos / 7450 tests en verde. Parser: `formato/numeros.ts` (`parsearNumeroDecimal`, `parsearEnteroTipeado`, `formatearNumeroDecimal`). Nota: el servidor (`ParserDeEscaneo`) lee la coma del prefijo `cantidad*codigo` como miles; el offline ahora acepta coma, el online no (seguimiento).

## Próximo paso

T2.
