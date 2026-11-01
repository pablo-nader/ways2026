import { parsearNumeroDecimal } from '../../formato/numeros'
import type { SucursalDireccion, SucursalListado } from '../../api/tipos'

/** Las 24 jurisdicciones argentinas con el nombre canónico que guarda el servidor (`ProvinciasArgentinas`). */
export const PROVINCIAS_ARGENTINAS = [
  'Buenos Aires',
  'Ciudad Autónoma de Buenos Aires',
  'Catamarca',
  'Chaco',
  'Chubut',
  'Córdoba',
  'Corrientes',
  'Entre Ríos',
  'Formosa',
  'Jujuy',
  'La Pampa',
  'La Rioja',
  'Mendoza',
  'Misiones',
  'Neuquén',
  'Río Negro',
  'Salta',
  'San Juan',
  'San Luis',
  'Santa Cruz',
  'Santa Fe',
  'Santiago del Estero',
  'Tierra del Fuego',
  'Tucumán',
] as const

/** La dirección tal como se tipea: todo texto, `''` es "sin informar". */
export type DireccionDelFormulario = {
  calle: string
  altura: string
  ciudad: string
  provincia: string
  codigoPostal: string
  latitud: string
  longitud: string
  referencia: string
}

export const DIRECCION_VACIA: DireccionDelFormulario = {
  calle: '',
  altura: '',
  ciudad: '',
  provincia: '',
  codigoPostal: '',
  latitud: '',
  longitud: '',
  referencia: '',
}

/** Listado → texto del formulario: lo ausente pasa a `''` y las coordenadas a su texto. */
export function aDireccionDelFormulario(s: SucursalListado): DireccionDelFormulario {
  return {
    calle: s.calle ?? '',
    altura: s.altura ?? '',
    ciudad: s.ciudad ?? '',
    provincia: s.provincia ?? '',
    codigoPostal: s.codigoPostal ?? '',
    latitud: s.latitud == null ? '' : String(s.latitud),
    longitud: s.longitud == null ? '' : String(s.longitud),
    referencia: s.referencia ?? '',
  }
}

export type ResultadoDeDireccion = { error: string } | { error: null; direccion: SucursalDireccion }

const textoOpcional = (texto: string) => {
  const limpio = texto.trim()

  return limpio === '' ? null : limpio
}

function coordenada(texto: string, nombre: 'latitud' | 'longitud', limite: number): number | string | null {
  if (texto.trim() === '') return null
  const valor = parsearNumeroDecimal(texto, { permitirNegativo: true })
  if (valor === null) return `La ${nombre} no es un número válido.`
  if (Math.abs(valor) > limite) return `La ${nombre} tiene que estar entre -${limite} y ${limite}.`

  return valor
}

/** Texto del formulario → cuerpo para la API, o el primer error que el servidor rechazaría igual (coordenadas fuera de
 * rango, a medias o ilegibles); el resto de las reglas (largos, provincia) las decide el servidor. */
export function aDireccionDeLaApi(d: DireccionDelFormulario): ResultadoDeDireccion {
  const latitud = coordenada(d.latitud, 'latitud', 90)
  if (typeof latitud === 'string') return { error: latitud }
  const longitud = coordenada(d.longitud, 'longitud', 180)
  if (typeof longitud === 'string') return { error: longitud }
  if ((latitud === null) !== (longitud === null)) {
    return { error: 'Completá la latitud y la longitud, o dejá las dos vacías.' }
  }

  return {
    error: null,
    direccion: {
      calle: textoOpcional(d.calle),
      altura: textoOpcional(d.altura),
      ciudad: textoOpcional(d.ciudad),
      provincia: textoOpcional(d.provincia),
      codigoPostal: textoOpcional(d.codigoPostal),
      latitud,
      longitud,
      referencia: textoOpcional(d.referencia),
    },
  }
}
