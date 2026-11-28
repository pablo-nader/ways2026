import { describe, expect, it } from 'vitest'
import { anchoDelPrecioEnEm, fuenteParaUnaLinea, MAXIMO_DE_LINEAS_DEL_NOMBRE } from './ajusteDeTexto'
import { ETIQUETAS_POR_HOJA, disposicionDeHoja } from './disposicionDeHojas'
import {
  ajustarEtiqueta,
  ANCHO_MINIMO_DE_COLUMNA_MM,
  IDS_DE_MODELO,
  MODELOS_DE_GONDOLA,
  PRECIO_EN_EL_SELLO,
  TOPE_DE_LA_LINEA_CHICA,
  anchoDelPrecioPartidoEnEm,
  type Caja,
  type TextosDeEtiqueta,
} from './modelosDeGondola'

const NOMBRE_LARGO = 'Aceite de girasol alto oleico Cocinero botella plástica 900g'
const OFERTAS: Record<string, Omit<TextosDeEtiqueta, 'nombre'>> = {
  'sin oferta': { precio: '$ 12.345,67', lineaChica: null, descuento: null },
  'con tachado': { precio: '$ 10.493,82', lineaChica: '$ 12.345,67', descuento: '-15%' },
  'con promoción': { precio: '3x $ 29.999,99', lineaChica: '$ 12.345,67', descuento: null },
}
const TOLERANCIA = 1e-6

function celdaDe(porHoja: (typeof ETIQUETAS_POR_HOJA)[number]) {
  const d = disposicionDeHoja(porHoja)
  return { anchoMm: d.celdaAnchoMm, altoMm: d.celdaAltoMm }
}

function seSuperponen(a: Caja, b: Caja): boolean {
  return a.x < b.x + b.ancho - TOLERANCIA && b.x < a.x + a.ancho - TOLERANCIA && a.y < b.y + b.alto - TOLERANCIA && b.y < a.y + a.alto - TOLERANCIA
}

describe('modelos de góndola', () => {
  it('el nombre largo de prueba tiene 60 caracteres', () => {
    expect(NOMBRE_LARGO).toHaveLength(60)
  })

  it('son 10 y cada uno declara su propio diseño', () => {
    expect(IDS_DE_MODELO).toHaveLength(10)
    expect(new Set(IDS_DE_MODELO.map((id) => MODELOS_DE_GONDOLA[id].disenio)).size).toBe(10)
  })

  it('los trazos nunca son vacíos ni tienen relleno: son paths de tinta', () => {
    for (const id of IDS_DE_MODELO) {
      for (const trazo of MODELOS_DE_GONDOLA[id].disenio(celdaDe(8)).trazos) {
        expect(trazo.d).toMatch(/^M /)
        expect(trazo.grosorMm).toBeGreaterThan(0)
        expect(Object.keys(trazo)).not.toContain('relleno')
      }
    }
  })

  for (const id of IDS_DE_MODELO) {
    for (const porHoja of ETIQUETAS_POR_HOJA) {
      it(`${id}, ${porHoja} por hoja: las cajas entran en la celda y no se pisan`, () => {
        const celda = celdaDe(porHoja)
        const { logo, nombre, precio } = MODELOS_DE_GONDOLA[id].disenio(celda)
        for (const caja of [logo, nombre, precio]) {
          expect(caja.ancho).toBeGreaterThan(0)
          expect(caja.alto).toBeGreaterThan(0)
          expect(caja.x).toBeGreaterThanOrEqual(1)
          expect(caja.y).toBeGreaterThanOrEqual(1)
          expect(caja.x + caja.ancho).toBeLessThanOrEqual(celda.anchoMm - 1)
          expect(caja.y + caja.alto).toBeLessThanOrEqual(celda.altoMm - 1)
        }
        expect(seSuperponen(logo, nombre)).toBe(false)
        expect(seSuperponen(logo, precio)).toBe(false)
        expect(seSuperponen(nombre, precio)).toBe(false)
      })

      for (const [caso, oferta] of Object.entries(OFERTAS)) {
        for (const nombre of ['Sal', NOMBRE_LARGO]) {
          it(`${id}, ${porHoja} por hoja, ${caso}, nombre de ${nombre.length} caracteres: los bloques entran en sus cajas`, () => {
            const ajuste = ajustarEtiqueta(id, celdaDe(porHoja), { nombre, ...oferta })
            const { disenio } = ajuste

            expect(ajuste.nombre.lineas.length).toBeLessThanOrEqual(MAXIMO_DE_LINEAS_DEL_NOMBRE)
            expect(ajuste.nombre.anchoMm).toBeLessThanOrEqual(disenio.nombre.ancho + TOLERANCIA)
            expect(ajuste.nombre.altoMm).toBeLessThanOrEqual(disenio.nombre.alto + TOLERANCIA)

            expect(ajuste.precio.fuenteMm).toBeGreaterThan(0)
            expect(ajuste.precio.anchoMm).toBeLessThanOrEqual(disenio.precio.ancho + TOLERANCIA)
            const altoDelBloque = ajuste.precio.altoMm + ajuste.separacionMm + (ajuste.lineaChica?.altoMm ?? 0)
            expect(altoDelBloque).toBeLessThanOrEqual(disenio.precio.alto + TOLERANCIA)

            if (oferta.lineaChica === null) {
              expect(ajuste.lineaChica).toBeNull()
            } else {
              expect(ajuste.lineaChica!.anchoMm).toBeLessThanOrEqual(disenio.precio.ancho + TOLERANCIA)
              expect(ajuste.lineaChica!.fuenteMm).toBeLessThan(ajuste.precio.fuenteMm)
            }
            expect(ajuste.nombre.fuenteMm).toBeLessThan(ajuste.precio.fuenteMm)
          })
        }
      }
    }
  }

  it('la línea chica respeta el tope respecto del precio grande', () => {
    const ajuste = ajustarEtiqueta('pizarra', celdaDe(2), { nombre: 'Sal', precio: '$ 9', lineaChica: '$ 10', descuento: null })
    expect(ajuste.lineaChica!.fuenteMm).toBeLessThanOrEqual(ajuste.precio.fuenteMm * TOPE_DE_LA_LINEA_CHICA)
  })

  it('el subrayado suma su grosor al alto del precio', () => {
    const ajuste = ajustarEtiqueta('subrayado', celdaDe(4), { nombre: 'Sal', precio: '$ 9', lineaChica: null, descuento: null })
    expect(ajuste.precio.altoMm).toBeCloseTo(ajuste.precio.fuenteMm + MODELOS_DE_GONDOLA.subrayado.subrayadoMm, 6)
  })

  it('centavos arriba: el "$" y los centavos chicos dejan crecer el entero', () => {
    const textos = { nombre: 'Sal', precio: '$ 12.345,67', lineaChica: null, descuento: null }
    const celda = { anchoMm: 60, altoMm: 200 }
    const zona = MODELOS_DE_GONDOLA.centavos.disenio(celda).precio
    const sinPartir = fuenteParaUnaLinea(anchoDelPrecioEnEm(textos.precio), { anchoMm: zona.ancho, altoMm: zona.alto })
    expect(ajustarEtiqueta('centavos', celda, textos).precio.fuenteMm).toBeGreaterThan(sinPartir)
    expect(anchoDelPrecioPartidoEnEm({ prefijo: '$', entero: '12.345', centavos: '67' })).toBeCloseTo(5 * 0.6 + 0.26 + (0.6 + 1.2) * 0.5 + 0.2, 6)
  })

  it('el texto del sello queda dentro del anillo interior', () => {
    for (const porHoja of ETIQUETAS_POR_HOJA) {
      const { precio, trazos } = MODELOS_DE_GONDOLA.sello.disenio(celdaDe(porHoja))
      const radioInterior = Number(/^M (\S+) (\S+) A (\S+)/.exec(trazos[1].d)![3])
      const semidiagonal = Math.hypot(precio.ancho / 2, precio.alto / 2)
      expect(semidiagonal + trazos[1].grosorMm).toBeLessThanOrEqual(radioInterior)
      expect(precio.alto / precio.ancho).toBeCloseTo(PRECIO_EN_EL_SELLO.alto / PRECIO_EN_EL_SELLO.ancho, 6)
    }
  })

  it('columna y sello van lado a lado solo cuando la columna del nombre es ancha; si no, se apilan', () => {
    const ancha = MODELOS_DE_GONDOLA.columna.disenio(celdaDe(4))
    expect(ancha.precio.x).toBeGreaterThan(ancha.nombre.x + ancha.nombre.ancho)
    expect(ancha.nombre.ancho).toBeGreaterThanOrEqual(ANCHO_MINIMO_DE_COLUMNA_MM)
    for (const porHoja of [2, 8, 16] as const) {
      const apilada = MODELOS_DE_GONDOLA.columna.disenio(celdaDe(porHoja))
      expect(apilada.precio.y).toBeGreaterThan(apilada.nombre.y + apilada.nombre.alto)
      const sello = MODELOS_DE_GONDOLA.sello.disenio(celdaDe(porHoja))
      expect(sello.precio.y).toBeGreaterThan(sello.nombre.y + sello.nombre.alto)
    }
  })
})

describe('el precio es el protagonista', () => {
  // Precio de góndola típico; los pisos se fijaron con Pizarra y Subrayado como referencia.
  const PRECIOS_TIPICOS: Record<string, Omit<TextosDeEtiqueta, 'nombre'>> = {
    'sin oferta': { precio: '$ 4.850', lineaChica: null, descuento: null },
    'con tachado': { precio: '$ 2.720', lineaChica: '$ 3.200', descuento: '-15%' },
  }
  /** Fuente mínima del precio como fracción del lado menor de la celda. */
  const PISO_DEL_PRECIO: Record<(typeof ETIQUETAS_POR_HOJA)[number], number> = { 2: 0.22, 4: 0.22, 8: 0.22, 16: 0.16 }
  const NOMBRE_MINIMO_MM = 2.5

  for (const id of IDS_DE_MODELO) {
    for (const porHoja of ETIQUETAS_POR_HOJA) {
      for (const [caso, oferta] of Object.entries(PRECIOS_TIPICOS)) {
        it(`${id}, ${porHoja} por hoja, ${caso}: el precio ocupa al menos el ${PISO_DEL_PRECIO[porHoja] * 100}% del lado menor y el nombre se lee`, () => {
          const celda = celdaDe(porHoja)
          const ajuste = ajustarEtiqueta(id, celda, { nombre: NOMBRE_LARGO, ...oferta })
          expect(ajuste.precio.fuenteMm).toBeGreaterThanOrEqual(PISO_DEL_PRECIO[porHoja] * Math.min(celda.anchoMm, celda.altoMm))
          expect(ajuste.nombre.fuenteMm).toBeGreaterThanOrEqual(NOMBRE_MINIMO_MM)
        })
      }
    }
  }

  it('en columna el precio llena su columna a 4 por hoja', () => {
    const ajuste = ajustarEtiqueta('columna', celdaDe(4), { nombre: 'Yerba mate Playadito suave con palo 1 kg', ...PRECIOS_TIPICOS['sin oferta'] })
    expect(ajuste.precio.anchoMm).toBeGreaterThanOrEqual(ajuste.disenio.precio.ancho * 0.75)
    expect(ajuste.nombre.fuenteMm).toBeGreaterThanOrEqual(4)
  })

  it('a 4 por hoja el sello toma casi todo el alto disponible', () => {
    const celda = celdaDe(4)
    const { precio } = MODELOS_DE_GONDOLA.sello.disenio(celda)
    const diametro = precio.ancho / PRECIO_EN_EL_SELLO.ancho
    expect(diametro).toBeGreaterThanOrEqual(celda.altoMm * 0.8)
  })
})
