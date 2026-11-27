import {
  ANCHO_DE_CARACTER_DEL_NOMBRE,
  anchoDelPrecioEnEm,
  ajustarNombre,
  ESCALA_DE_ANCHO_DEL_PRECIO,
  fuenteParaUnaLinea,
  INTERLINEADO_DE_LA_LINEA_CHICA,
  INTERLINEADO_DEL_NOMBRE,
  type AnchoDeFuente,
  type MedidaDeCelda,
  type NombreAjustado,
} from './ajusteDeTexto'
import { partirPrecio, type PrecioPartido } from './contenidoDeGondola'

export const IDS_DE_MODELO = ['pizarra', 'ticket', 'columna', 'subrayado', 'colgante', 'sello', 'corchetes', 'doble', 'centavos', 'banderin'] as const
export type IdDeModelo = (typeof IDS_DE_MODELO)[number]

/** Rectángulo en milímetros, medido desde la esquina superior izquierda de la celda. */
export type Caja = { readonly x: number; readonly y: number; readonly ancho: number; readonly alto: number }

export type ColorDeTrazo = 'acento' | 'borde'

/** Trazo de tinta sin relleno: un path SVG en milímetros de la celda. */
export type Trazo = { readonly d: string; readonly grosorMm: number; readonly color: ColorDeTrazo; readonly discontinuo?: boolean }

export type DisenioDeEtiqueta = {
  readonly logo: Caja
  readonly nombre: Caja
  /** Zona del precio protagonista; con oferta incluye la línea chica (tachado o precio normal). */
  readonly precio: Caja
  readonly trazos: readonly Trazo[]
}

export type ModeloDeGondola = {
  readonly id: IdDeModelo
  readonly anchoDelNombre: AnchoDeFuente
  readonly anchoDelPrecio: AnchoDeFuente
  /** Tipografía de supermercado: "$" y centavos chicos arriba del entero. */
  readonly precioPartido: boolean
  /** Grosor del subrayado del precio; se suma al alto de la línea grande. */
  readonly subrayadoMm: number
  readonly disenio: (celda: MedidaDeCelda) => DisenioDeEtiqueta
}

/** Proporción del logo de Ways (1800 × 981 px). */
export const ASPECTO_DEL_LOGO = 1800 / 981
/** Por debajo de este ancho la columna del nombre no aloja 60 caracteres en dos líneas: el modelo se apila. */
export const ANCHO_MINIMO_DE_COLUMNA_MM = 46
export const ESCALA_DEL_DESCUENTO = 0.85
export const ESCALA_DEL_SUPERINDICE = 0.5

const RELLENO = 0.06

type Interior = { x: number; y: number; ancho: number; alto: number; base: number; gx: number; gy: number }

function interior(celda: MedidaDeCelda, rellenoX: number, rellenoY: number): Interior {
  const ancho = celda.anchoMm - 2 * rellenoX
  const alto = celda.altoMm - 2 * rellenoY
  return { x: rellenoX, y: rellenoY, ancho, alto, base: Math.min(celda.anchoMm, celda.altoMm), gx: ancho * 0.03, gy: alto * 0.03 }
}

function base(celda: MedidaDeCelda): number {
  return Math.min(celda.anchoMm, celda.altoMm)
}

function caja(x: number, y: number, ancho: number, alto: number): Caja {
  return { x, y, ancho, alto }
}

/** El logo acompaña sin competir: nunca pasa del 10% del lado menor de la celda. */
function medidaDelLogo(m: Interior, altoMaximo: number, anchoMaximo: number): { ancho: number; alto: number } {
  const alto = Math.min(altoMaximo, m.base * 0.1, anchoMaximo / ASPECTO_DEL_LOGO)
  return { ancho: alto * ASPECTO_DEL_LOGO, alto }
}

const n = (valor: number) => String(Math.round(valor * 100) / 100)

function rectangulo(x: number, y: number, ancho: number, alto: number, radio: number): string {
  const r = Math.min(radio, ancho / 2, alto / 2)
  return (
    `M ${n(x + r)} ${n(y)} H ${n(x + ancho - r)} A ${n(r)} ${n(r)} 0 0 1 ${n(x + ancho)} ${n(y + r)} V ${n(y + alto - r)} ` +
    `A ${n(r)} ${n(r)} 0 0 1 ${n(x + ancho - r)} ${n(y + alto)} H ${n(x + r)} A ${n(r)} ${n(r)} 0 0 1 ${n(x)} ${n(y + alto - r)} ` +
    `V ${n(y + r)} A ${n(r)} ${n(r)} 0 0 1 ${n(x + r)} ${n(y)} Z`
  )
}

function linea(x1: number, y1: number, x2: number, y2: number): string {
  return `M ${n(x1)} ${n(y1)} L ${n(x2)} ${n(y2)}`
}

function circulo(cx: number, cy: number, r: number): string {
  return `M ${n(cx - r)} ${n(cy)} A ${n(r)} ${n(r)} 0 1 0 ${n(cx + r)} ${n(cy)} A ${n(r)} ${n(r)} 0 1 0 ${n(cx - r)} ${n(cy)} Z`
}

function hastaElFondo(m: Interior, desde: number): number {
  return m.y + m.alto - desde
}

function pizarra(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  const logo = medidaDelLogo(m, m.alto * 0.11, m.ancho * 0.4)
  const nombre = caja(m.x, m.y + logo.alto + m.gy, m.ancho, m.alto * 0.27)
  const radioDelPunto = Math.max(0.5, m.base * 0.012)
  const yDelAdorno = nombre.y + nombre.alto + m.gy + radioDelPunto
  const yDelPrecio = yDelAdorno + radioDelPunto + m.gy
  const cx = celda.anchoMm / 2
  const brazo = celda.anchoMm * 0.1
  return {
    logo: caja(m.x + (m.ancho - logo.ancho) / 2, m.y, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, yDelPrecio, m.ancho, hastaElFondo(m, yDelPrecio)),
    trazos: [
      { d: rectangulo(0.4, 0.4, celda.anchoMm - 0.8, celda.altoMm - 0.8, 3), grosorMm: 0.4, color: 'acento' },
      {
        d: `${linea(cx - brazo, yDelAdorno, cx - 2.5 * radioDelPunto, yDelAdorno)} ${linea(cx + 2.5 * radioDelPunto, yDelAdorno, cx + brazo, yDelAdorno)} ${circulo(cx, yDelAdorno, radioDelPunto)}`,
        grosorMm: 0.35,
        color: 'acento',
      },
    ],
  }
}

function ticket(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const { anchoMm: W, altoMm: H } = celda
  const borde = 0.6
  const muesca = base(celda) * 0.075
  const m = interior(celda, borde + muesca + RELLENO * base(celda) * 0.3, RELLENO * base(celda))
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.26)
  const logo = medidaDelLogo(m, m.alto * 0.12, m.ancho * 0.3)
  const yDelLogo = m.y + m.alto - logo.alto
  const yDelPrecio = nombre.y + nombre.alto + m.gy
  const medio = H / 2
  const contorno =
    `M ${n(borde)} ${n(borde)} H ${n(W - borde)} V ${n(medio - muesca)} A ${n(muesca)} ${n(muesca)} 0 0 0 ${n(W - borde)} ${n(medio + muesca)} ` +
    `V ${n(H - borde)} H ${n(borde)} V ${n(medio + muesca)} A ${n(muesca)} ${n(muesca)} 0 0 0 ${n(borde)} ${n(medio - muesca)} Z`
  return {
    logo: caja(m.x, yDelLogo, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, yDelPrecio, m.ancho, yDelLogo - m.gy - yDelPrecio),
    trazos: [{ d: contorno, grosorMm: 0.5, color: 'borde', discontinuo: true }],
  }
}

function columna(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  const anchoDeColumna = m.ancho * 0.4
  const regla = 0.6
  if (celda.anchoMm >= celda.altoMm && anchoDeColumna >= ANCHO_MINIMO_DE_COLUMNA_MM) {
    const logo = medidaDelLogo(m, m.alto * 0.13, anchoDeColumna * 0.5)
    const altoDelNombre = m.alto * 0.6
    const yDelGrupo = m.y + (m.alto - (logo.alto + m.gy + altoDelNombre)) / 2
    const xDeLaRegla = m.x + anchoDeColumna + m.ancho * 0.04
    const xDelPrecio = xDeLaRegla + regla / 2 + m.ancho * 0.04
    return {
      logo: caja(m.x, yDelGrupo, logo.ancho, logo.alto),
      nombre: caja(m.x, yDelGrupo + logo.alto + m.gy, anchoDeColumna, altoDelNombre),
      precio: caja(xDelPrecio, m.y, m.x + m.ancho - xDelPrecio, m.alto),
      trazos: [{ d: linea(xDeLaRegla, m.y + m.alto * 0.06, xDeLaRegla, m.y + m.alto * 0.94), grosorMm: regla, color: 'acento' }],
    }
  }
  // Celda angosta o vertical: el nombre va arriba a lo ancho y la regla separa el logo del precio.
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.3)
  const yDeLaFila = nombre.y + nombre.alto + m.gy
  const altoDeLaFila = hastaElFondo(m, yDeLaFila)
  const logo = medidaDelLogo(m, m.alto * 0.14, m.ancho * 0.26)
  const xDeLaRegla = m.x + logo.ancho + m.gx
  const xDelPrecio = xDeLaRegla + regla / 2 + m.gx
  return {
    logo: caja(m.x, yDeLaFila + (altoDeLaFila - logo.alto) / 2, logo.ancho, logo.alto),
    nombre,
    precio: caja(xDelPrecio, yDeLaFila, m.x + m.ancho - xDelPrecio, altoDeLaFila),
    trazos: [{ d: linea(xDeLaRegla, yDeLaFila + altoDeLaFila * 0.08, xDeLaRegla, yDeLaFila + altoDeLaFila * 0.92), grosorMm: regla, color: 'acento' }],
  }
}

/** Logo arriba a la derecha, nombre a su izquierda y el precio debajo, a todo el ancho. */
function encabezadoConLogoALaDerecha(m: Interior, altoDelNombre: number) {
  const logo = medidaDelLogo(m, m.alto * 0.12, m.ancho * 0.16)
  const nombre = caja(m.x, m.y, m.ancho - logo.ancho - m.gx, altoDelNombre)
  return { logo: caja(m.x + m.ancho - logo.ancho, m.y, logo.ancho, logo.alto), nombre, debajo: nombre.y + nombre.alto + m.gy }
}

function subrayado(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  const { logo, nombre, debajo } = encabezadoConLogoALaDerecha(m, m.alto * 0.34)
  return { logo, nombre, precio: caja(m.x, debajo, m.ancho, hastaElFondo(m, debajo)), trazos: [] }
}

function colgante(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const { anchoMm: W, altoMm: H } = celda
  const b = base(celda)
  const borde = 0.6
  const chaflan = b * 0.1
  const radio = b * 0.05
  const agujero = b * 0.08
  const m = interior(celda, RELLENO * b, RELLENO * b)
  const cx = W / 2
  const cyDelAgujero = m.y + agujero / 2
  const desde = m.y + agujero + m.gy
  const disponible = hastaElFondo(m, desde)
  const logo = medidaDelLogo(m, disponible * 0.12, m.ancho * 0.4)
  const nombre = caja(m.x, desde + logo.alto + m.gy, m.ancho, disponible * 0.3)
  const yDelPrecio = nombre.y + nombre.alto + m.gy
  const contorno =
    `M ${n(borde + chaflan)} ${n(borde)} H ${n(W - borde - chaflan)} L ${n(W - borde)} ${n(borde + chaflan)} V ${n(H - borde - radio)} ` +
    `A ${n(radio)} ${n(radio)} 0 0 1 ${n(W - borde - radio)} ${n(H - borde)} H ${n(borde + radio)} A ${n(radio)} ${n(radio)} 0 0 1 ${n(borde)} ${n(H - borde - radio)} ` +
    `V ${n(borde + chaflan)} Z`
  const hilo = `M ${n(cx)} ${n(cyDelAgujero - agujero / 2)} Q ${n(cx)} ${n(cyDelAgujero * 0.2)} ${n(cx + W * 0.08)} 0`
  return {
    logo: caja(cx - logo.ancho / 2, desde, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, yDelPrecio, m.ancho, hastaElFondo(m, yDelPrecio)),
    trazos: [
      { d: contorno, grosorMm: 0.5, color: 'borde' },
      { d: circulo(cx, cyDelAgujero, agujero / 2), grosorMm: 0.5, color: 'borde' },
      { d: hilo, grosorMm: 0.3, color: 'borde' },
    ],
  }
}

function anillos(cx: number, cy: number, diametro: number): Trazo[] {
  const exterior = diametro / 2 - 0.4
  return [
    { d: circulo(cx, cy, exterior), grosorMm: 0.8, color: 'acento' },
    { d: circulo(cx, cy, exterior - Math.min(1.3, diametro * 0.03)), grosorMm: 0.25, color: 'acento' },
  ]
}

/** Texto inscripto en el sello: sus esquinas quedan dentro del anillo interior. */
export const PRECIO_EN_EL_SELLO = { ancho: 0.76, alto: 0.44 } as const

function precioEnElSello(x: number, y: number, diametro: number): Caja {
  const ancho = diametro * PRECIO_EN_EL_SELLO.ancho
  const alto = diametro * PRECIO_EN_EL_SELLO.alto
  return caja(x + (diametro - ancho) / 2, y + (diametro - alto) / 2, ancho, alto)
}

function sello(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  // Al costado, el sello toma todo lo que deja libre la columna mínima del nombre.
  const diametroAlCostado = Math.min(m.alto, m.ancho - ANCHO_MINIMO_DE_COLUMNA_MM - m.gx)
  if (celda.anchoMm >= celda.altoMm && diametroAlCostado >= m.alto * 0.6) {
    const d = diametroAlCostado
    const anchoDeColumna = m.ancho - d - m.gx
    const x = m.x + m.ancho - d
    const y = m.y + (m.alto - d) / 2
    const logo = medidaDelLogo(m, m.alto * 0.13, anchoDeColumna * 0.5)
    const altoDelNombre = m.alto * 0.55
    const yDelGrupo = m.y + (m.alto - (logo.alto + m.gy + altoDelNombre)) / 2
    return {
      logo: caja(m.x, yDelGrupo, logo.ancho, logo.alto),
      nombre: caja(m.x, yDelGrupo + logo.alto + m.gy, anchoDeColumna, altoDelNombre),
      precio: precioEnElSello(x, y, d),
      trazos: anillos(x + d / 2, y + d / 2, d),
    }
  }
  // Celda angosta o vertical: nombre arriba a lo ancho, el sello abajo a la derecha y el logo a su izquierda.
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.24)
  const yDeLaFila = nombre.y + nombre.alto + m.gy
  const altoDeLaFila = hastaElFondo(m, yDeLaFila)
  const d = Math.min(altoDeLaFila, m.ancho * 0.88)
  const x = m.x + m.ancho - d
  const y = yDeLaFila + (altoDeLaFila - d) / 2
  const logo = medidaDelLogo(m, m.alto * 0.14, m.ancho - d - m.gx)
  return {
    logo: caja(m.x, m.y + m.alto - logo.alto, logo.ancho, logo.alto),
    nombre,
    precio: precioEnElSello(x, y, d),
    trazos: anillos(x + d / 2, y + d / 2, d),
  }
}

function corchetes(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const { anchoMm: W, altoMm: H } = celda
  const b = base(celda)
  const e = b * 0.03
  const brazo = b * 0.16
  const m = interior(celda, b * 0.07, b * 0.07)
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.3)
  const logo = medidaDelLogo(m, m.alto * 0.1, m.ancho * 0.3)
  const yDelLogo = m.y + m.alto - logo.alto
  const yDelPrecio = nombre.y + nombre.alto + m.gy
  const esquinas = [
    `M ${n(e)} ${n(e + brazo)} V ${n(e)} H ${n(e + brazo)}`,
    `M ${n(W - e - brazo)} ${n(e)} H ${n(W - e)} V ${n(e + brazo)}`,
    `M ${n(W - e)} ${n(H - e - brazo)} V ${n(H - e)} H ${n(W - e - brazo)}`,
    `M ${n(e + brazo)} ${n(H - e)} H ${n(e)} V ${n(H - e - brazo)}`,
  ].join(' ')
  return {
    logo: caja(m.x + (m.ancho - logo.ancho) / 2, yDelLogo, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, yDelPrecio, m.ancho, yDelLogo - m.gy - yDelPrecio),
    trazos: [{ d: esquinas, grosorMm: 0.8, color: 'acento' }],
  }
}

function doble(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const { anchoMm: W, altoMm: H } = celda
  const b = base(celda)
  const m = interior(celda, b * 0.04, 1.6 + b * 0.05)
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.26)
  const debajo = nombre.y + nombre.alto + m.gy
  const logo = medidaDelLogo(m, m.alto * 0.11, m.ancho * 0.2)
  const yDelLogo = m.y + m.alto - logo.alto
  const reglas = [linea(0, 0.5, W, 0.5), linea(0, 1.4, W, 1.4), linea(0, H - 1.4, W, H - 1.4), linea(0, H - 0.5, W, H - 0.5)].join(' ')
  return {
    logo: caja(m.x + m.ancho - logo.ancho, yDelLogo, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, debajo, m.ancho, yDelLogo - m.gy - debajo),
    trazos: [{ d: reglas, grosorMm: 0.35, color: 'borde' }],
  }
}

function centavos(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  const nombre = caja(m.x, m.y, m.ancho, m.alto * 0.28)
  const yDelFilete = nombre.y + nombre.alto + m.gy / 2
  const yDelPrecio = yDelFilete + m.gy / 2 + 0.3
  const logo = medidaDelLogo(m, m.alto * 0.11, m.ancho * 0.28)
  const yDelLogo = m.y + m.alto - logo.alto
  return {
    logo: caja(m.x + m.ancho - logo.ancho, yDelLogo, logo.ancho, logo.alto),
    nombre,
    precio: caja(m.x, yDelPrecio, m.ancho, yDelLogo - m.gy - yDelPrecio),
    trazos: [{ d: linea(m.x, yDelFilete, m.x + m.ancho, yDelFilete), grosorMm: 0.25, color: 'borde' }],
  }
}

function banderin(celda: MedidaDeCelda): DisenioDeEtiqueta {
  const m = interior(celda, RELLENO * base(celda), RELLENO * base(celda))
  const { logo, nombre, debajo } = encabezadoConLogoALaDerecha(m, m.alto * 0.24)
  const x = m.x
  const y = debajo
  const ancho = m.ancho
  const alto = hastaElFondo(m, debajo)
  const punta = Math.min(alto * 0.5, ancho * 0.2)
  const banderinD = `M ${n(x + punta)} ${n(y)} H ${n(x + ancho)} V ${n(y + alto)} H ${n(x + punta)} L ${n(x)} ${n(y + alto / 2)} Z`
  const ojal = circulo(x + punta * 0.7, y + alto / 2, Math.min(alto * 0.1, punta * 0.25))
  return {
    logo,
    nombre,
    precio: caja(x + punta + ancho * 0.03, y + alto * 0.1, ancho - punta - ancho * 0.06, alto * 0.8),
    trazos: [
      { d: banderinD, grosorMm: 0.6, color: 'acento' },
      { d: ojal, grosorMm: 0.3, color: 'acento' },
    ],
  }
}

export const MODELOS_DE_GONDOLA: Readonly<Record<IdDeModelo, ModeloDeGondola>> = {
  pizarra: { id: 'pizarra', anchoDelNombre: 'normal', anchoDelPrecio: 'normal', precioPartido: false, subrayadoMm: 0, disenio: pizarra },
  ticket: { id: 'ticket', anchoDelNombre: 'normal', anchoDelPrecio: 'normal', precioPartido: false, subrayadoMm: 0, disenio: ticket },
  columna: { id: 'columna', anchoDelNombre: 'condensado', anchoDelPrecio: 'condensado', precioPartido: false, subrayadoMm: 0, disenio: columna },
  subrayado: { id: 'subrayado', anchoDelNombre: 'normal', anchoDelPrecio: 'condensado', precioPartido: false, subrayadoMm: 1.2, disenio: subrayado },
  colgante: { id: 'colgante', anchoDelNombre: 'normal', anchoDelPrecio: 'normal', precioPartido: false, subrayadoMm: 0, disenio: colgante },
  sello: { id: 'sello', anchoDelNombre: 'condensado', anchoDelPrecio: 'condensado', precioPartido: false, subrayadoMm: 0, disenio: sello },
  corchetes: { id: 'corchetes', anchoDelNombre: 'normal', anchoDelPrecio: 'normal', precioPartido: false, subrayadoMm: 0, disenio: corchetes },
  doble: { id: 'doble', anchoDelNombre: 'ancho', anchoDelPrecio: 'normal', precioPartido: false, subrayadoMm: 0, disenio: doble },
  centavos: { id: 'centavos', anchoDelNombre: 'normal', anchoDelPrecio: 'normal', precioPartido: true, subrayadoMm: 0, disenio: centavos },
  banderin: { id: 'banderin', anchoDelNombre: 'condensado', anchoDelPrecio: 'condensado', precioPartido: false, subrayadoMm: 0, disenio: banderin },
}

/** Alto de la línea chica (tachado o precio normal) y separación, como fracción del alto de la zona del precio. */
export const FRACCION_DE_LA_LINEA_CHICA = 0.24
export const FRACCION_DE_SEPARACION = 0.04
/** La línea chica nunca supera esta fracción de la fuente del precio grande: el precio manda. */
export const TOPE_DE_LA_LINEA_CHICA = 0.6
/** El nombre nunca supera esta fracción de la fuente del precio, ni esta fracción del lado menor de la celda. */
export const TOPE_DEL_NOMBRE_SOBRE_EL_PRECIO = 0.6
export const TOPE_DEL_NOMBRE_SOBRE_LA_CELDA = 0.1
/** El tope sobre el precio puede achicar el nombre, pero nunca por debajo de este tamaño legible. */
export const NOMBRE_MINIMO_LEGIBLE_MM = 2.8
/** Relleno, borde y separación de la marca de descuento, en em. */
const EM_EXTRA_DEL_DESCUENTO = 1
/** Separación entre el prefijo, el entero y los centavos, en em. */
const EM_EXTRA_DEL_PRECIO_PARTIDO = 0.2

export type TextosDeEtiqueta = {
  nombre: string
  /** El texto grande: precio o promoción por cantidad. */
  precio: string
  /** Precio tachado o precio normal; `null` sin oferta. */
  lineaChica: string | null
  /** "-15%" junto al tachado. */
  descuento: string | null
}

export type BloqueMedido = { fuenteMm: number; anchoMm: number; altoMm: number }

export type AjusteDeEtiqueta = {
  disenio: DisenioDeEtiqueta
  nombre: NombreAjustado & { anchoMm: number; altoMm: number }
  precio: BloqueMedido
  lineaChica: BloqueMedido | null
  separacionMm: number
}

/** Ancho en em, con la fuente normal, del precio con el prefijo y los centavos a media altura. */
export function anchoDelPrecioPartidoEnEm(partes: PrecioPartido): number {
  const chicas = anchoDelPrecioEnEm(partes.prefijo) + anchoDelPrecioEnEm(partes.centavos ?? '')
  return anchoDelPrecioEnEm(partes.entero) + chicas * ESCALA_DEL_SUPERINDICE + EM_EXTRA_DEL_PRECIO_PARTIDO
}

/** Tamaños de fuente de cada bloque, ajustados a las cajas del modelo para esta celda. */
export function ajustarEtiqueta(id: IdDeModelo, celda: MedidaDeCelda, textos: TextosDeEtiqueta): AjusteDeEtiqueta {
  const modelo = MODELOS_DE_GONDOLA[id]
  const disenio = modelo.disenio(celda)
  const zona = disenio.precio

  const conLineaChica = textos.lineaChica !== null
  const altoChico = conLineaChica ? zona.alto * FRACCION_DE_LA_LINEA_CHICA : 0
  const separacionMm = conLineaChica ? zona.alto * FRACCION_DE_SEPARACION : 0
  const altoGrande = zona.alto - altoChico - separacionMm

  const enEm = modelo.precioPartido ? anchoDelPrecioPartidoEnEm(partirPrecio(textos.precio)) : anchoDelPrecioEnEm(textos.precio)
  const anchoDelPrecio = enEm * ESCALA_DE_ANCHO_DEL_PRECIO[modelo.anchoDelPrecio]
  const fuenteDelPrecio = fuenteParaUnaLinea(anchoDelPrecio, { anchoMm: zona.ancho, altoMm: altoGrande - modelo.subrayadoMm })
  const precio = { fuenteMm: fuenteDelPrecio, anchoMm: anchoDelPrecio * fuenteDelPrecio, altoMm: fuenteDelPrecio + modelo.subrayadoMm }

  let lineaChica: BloqueMedido | null = null
  if (textos.lineaChica !== null) {
    const descuento = textos.descuento ? anchoDelPrecioEnEm(textos.descuento) * ESCALA_DEL_DESCUENTO + EM_EXTRA_DEL_DESCUENTO : 0
    const anchoChico = anchoDelPrecioEnEm(textos.lineaChica) + descuento
    const fuente = Math.min(
      fuenteParaUnaLinea(anchoChico, { anchoMm: zona.ancho, altoMm: altoChico }, INTERLINEADO_DE_LA_LINEA_CHICA),
      Math.floor(fuenteDelPrecio * TOPE_DE_LA_LINEA_CHICA * 100) / 100,
    )
    lineaChica = { fuenteMm: fuente, anchoMm: anchoChico * fuente, altoMm: fuente * INTERLINEADO_DE_LA_LINEA_CHICA }
  }

  const factorDelNombre = ANCHO_DE_CARACTER_DEL_NOMBRE[modelo.anchoDelNombre]
  const topePorJerarquia = Math.min(fuenteDelPrecio * TOPE_DEL_NOMBRE_SOBRE_EL_PRECIO, Math.min(celda.anchoMm, celda.altoMm) * TOPE_DEL_NOMBRE_SOBRE_LA_CELDA)
  const topeDelNombre = Math.max(topePorJerarquia, NOMBRE_MINIMO_LEGIBLE_MM)
  const ajustado = ajustarNombre(textos.nombre, { anchoMm: disenio.nombre.ancho, altoMm: disenio.nombre.alto }, factorDelNombre, topeDelNombre)
  const caracteresDeLaLineaMasLarga = Math.max(0, ...ajustado.lineas.map((l) => l.length))
  const nombre = {
    ...ajustado,
    anchoMm: caracteresDeLaLineaMasLarga * factorDelNombre * ajustado.fuenteMm,
    altoMm: ajustado.lineas.length * ajustado.fuenteMm * INTERLINEADO_DEL_NOMBRE,
  }

  return { disenio, nombre, precio, lineaChica, separacionMm }
}
