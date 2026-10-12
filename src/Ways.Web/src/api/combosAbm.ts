/**
 * ABM de combos del back office: cliente de escritura y helpers puros (formulario ↔ cuerpo ↔ fila de listado,
 * validación local, rotulado y traducción de los rechazos del servidor). Vive fuera de `Combos.tsx` para que
 * cada regla se pruebe sin montar la pantalla (`web-descriptor-tests`).
 *
 * La validación local espeja `ReglaDeCombos` solo para que los errores comunes no viajen; el servidor sigue
 * siendo la autoridad y todos sus códigos se traducen en `errorDeServidorDeCombo`. La lectura del listado es
 * `clienteDeCombos.listar` de `combos.ts`.
 */
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from './bajas'
import { api, ErrorApi } from './cliente'
import type {
  AltaCombo,
  ArticuloListado,
  ComboListado,
  ComponenteDeComboListado,
  ComponenteDeSolicitudDeCombo,
  EdicionCombo,
} from './tipos'
import { formatearImporte } from '../formato/importes'
import { tieneMasDeDosDecimales } from '../pos/motorDeCombos'

export const UNIDADES_MINIMAS = 2
export const COMPONENTES_MAXIMOS = 50
export const CANTIDAD_MAXIMA = 999_999
export const CODIGO_MAXIMO = 999_999
export const PRECIO_MAXIMO = 999_999_999_999.99
export const LARGO_MAXIMO_DEL_NOMBRE = 150

export const clienteDeCombosAbm = {
  crear: (datos: AltaCombo) => api.post<ComboListado>('/combos', datos),
  /** Reemplaza el combo entero, componentes incluidos. 404 si lo dio de baja otra persona. */
  actualizar: (id: number, datos: EdicionCombo) => api.put<ComboListado>(`/combos/${id}`, datos),
  /** Baja lógica: los componentes se borran con el combo. */
  eliminar: (id: number) => api.delete(`/combos/${id}`),
}

/** Un componente de combo es un artículo por unidad y activo: el servidor rechaza los pesables y el POS no puede vender
 * uno inactivo. */
export function esArticuloElegibleParaCombo(articulo: Pick<ArticuloListado, 'unidadVenta' | 'activo'>): boolean {
  return articulo.unidadVenta === 'Unidad' && articulo.activo
}

export type TipoDeComponente = 'Articulo' | 'Familia'

/** Una fila del editor. `clave` es local y estable (identifica la fila al quitar otra); `nombre` es el rótulo de lo
 * elegido (vacío si el servidor no lo trajo); `disponible` es `false` solo para una fila que el servidor devolvió con
 * su artículo o familia dados de baja y que todavía no se reemplazó. */
export type FilaDeComponente = {
  clave: number
  tipo: TipoDeComponente
  idArticulo: number | ''
  idFamilia: number | ''
  nombre: string
  cantidad: string
  disponible: boolean
}

export type FormularioCombo = {
  id: number | null
  codigo: string
  nombre: string
  precio: number | null
  idEmpresa: number | ''
  activo: boolean
  disponibleOnline: boolean
  componentes: FilaDeComponente[]
}

let contadorDeClaves = 0

export function filaDeComponenteVacia(tipo: TipoDeComponente = 'Articulo'): FilaDeComponente {
  contadorDeClaves += 1
  return { clave: contadorDeClaves, tipo, idArticulo: '', idFamilia: '', nombre: '', cantidad: '1', disponible: true }
}

/** El alta arranca con dos filas vacías: es el mínimo que un combo necesita. */
export function formularioComboVacio(): FormularioCombo {
  return {
    id: null,
    codigo: '',
    nombre: '',
    precio: null,
    idEmpresa: '',
    activo: true,
    disponibleOnline: true,
    componentes: [filaDeComponenteVacia(), filaDeComponenteVacia()],
  }
}

export function aValoresCombo(combo: ComboListado): FormularioCombo {
  return {
    id: combo.id,
    codigo: String(combo.codigo),
    nombre: combo.nombre,
    precio: combo.precio,
    idEmpresa: combo.idEmpresa ?? '',
    activo: combo.activo,
    disponibleOnline: combo.disponibleOnline,
    componentes: combo.componentes.map((c) => {
      const fila = filaDeComponenteVacia(c.idArticulo !== null ? 'Articulo' : 'Familia')
      return {
        ...fila,
        idArticulo: c.idArticulo ?? '',
        idFamilia: c.idFamilia ?? '',
        nombre: c.nombre ?? '',
        cantidad: String(c.cantidad),
        disponible: c.disponible,
      }
    }),
  }
}

function aComponenteDeSolicitud(fila: FilaDeComponente): ComponenteDeSolicitudDeCombo {
  const cantidad = Number(fila.cantidad.trim())
  return fila.tipo === 'Articulo'
    ? { idArticulo: Number(fila.idArticulo), cantidad }
    : { idFamilia: Number(fila.idFamilia), cantidad }
}

/** Cuerpo de alta y de edición (el mismo: `activo` y `disponibleOnline` viajan siempre). Se llama con el formulario ya validado. Un código
 * vacío no viaja: en el alta el servidor asigna el siguiente libre y en la edición conserva el actual. */
export function aSolicitudDeCombo(f: FormularioCombo): AltaCombo {
  const codigo = f.codigo.trim()
  return {
    ...(codigo === '' ? {} : { codigo: Number(codigo) }),
    nombre: f.nombre.trim(),
    precio: Number(f.precio),
    idEmpresa: f.idEmpresa === '' ? null : f.idEmpresa,
    activo: f.activo,
    disponibleOnline: f.disponibleOnline,
    componentes: f.componentes.map(aComponenteDeSolicitud),
  }
}

function claveDeComponente(c: { idArticulo?: number | null; idFamilia?: number | null; cantidad: number }): string {
  return c.idArticulo != null ? `a${c.idArticulo}x${c.cantidad}` : `f${c.idFamilia}x${c.cantidad}`
}

/** `true` si el combo del listado es el que se pidió crear: nombre (sin espacios ni mayúsculas), precio, empresa, estado
 * y conjunto de componentes (mismos artículos o familias con las mismas cantidades, sin importar el orden) iguales al
 * cuerpo enviado, y el mismo código cuando el cuerpo trajo uno. El nombre solo no alcanza: no es único. */
export function comboCoincideConAlta(cuerpo: AltaCombo, combo: ComboListado): boolean {
  if (cuerpo.codigo !== undefined && combo.codigo !== cuerpo.codigo) return false
  if (combo.nombre.trim().toLowerCase() !== cuerpo.nombre.trim().toLowerCase()) return false
  if (combo.precio !== cuerpo.precio) return false
  if (combo.idEmpresa !== cuerpo.idEmpresa) return false
  if (combo.activo !== cuerpo.activo) return false
  if (combo.disponibleOnline !== cuerpo.disponibleOnline) return false
  if (combo.componentes.length !== cuerpo.componentes.length) return false
  const esperados = cuerpo.componentes.map(claveDeComponente).sort()
  const reales = combo.componentes.map(claveDeComponente).sort()
  return esperados.every((clave, i) => clave === reales[i])
}

/** Lo que el cajero tipea en el POS para cargar el combo. */
export function etiquetaDeCodigoDeCombo(codigo: number): string {
  return `C${codigo}`
}

function rotuloDeComponente(tipo: TipoDeComponente, id: number | '', nombre: string): string {
  const base = nombre.trim() || `${tipo === 'Articulo' ? 'Artículo' : 'Familia'} #${id}`
  return tipo === 'Familia' ? `${base} (familia)` : base
}

function parteDeResumen(cantidad: number, rotulo: string, disponible: boolean): string {
  return `${cantidad} × ${rotulo}${disponible ? '' : ' (no disponible)'}`
}

/** Resumen de una línea de los componentes de un combo del listado: `1 × Pancho + 1 × Línea Coca 500 (familia)`. */
export function resumenDeComponentes(componentes: readonly ComponenteDeComboListado[]): string {
  return componentes
    .map((c) =>
      parteDeResumen(
        c.cantidad,
        rotuloDeComponente(c.idArticulo !== null ? 'Articulo' : 'Familia', c.idArticulo ?? c.idFamilia ?? '', c.nombre ?? ''),
        c.disponible,
      ),
    )
    .join(' + ')
}

/** `true` si algún componente quedó apuntando a un artículo o una familia dados de baja: el combo no se puede completar. */
export function tieneComponentesNoDisponibles(combo: Pick<ComboListado, 'componentes'>): boolean {
  return combo.componentes.some((c) => !c.disponible)
}

/** Qué es el combo según lo que el formulario lleva hasta ahora; omite las filas sin referencia o con cantidad
 * inválida y el precio mientras no es un importe. Vacío si no hay ninguna fila completa. */
export function resumenDeFormulario(f: FormularioCombo): string {
  const partes = f.componentes.flatMap((fila) => {
    const id = fila.tipo === 'Articulo' ? fila.idArticulo : fila.idFamilia
    const cantidad = fila.cantidad.trim()
    if (id === '' || !/^\d+$/.test(cantidad) || Number(cantidad) < 1) return []
    return [parteDeResumen(Number(cantidad), rotuloDeComponente(fila.tipo, id, fila.nombre), fila.disponible)]
  })
  if (partes.length === 0) return ''
  const componentes = partes.join(' + ')
  return f.precio !== null && f.precio > 0 ? `${componentes} por ${formatearImporte(f.precio, { simbolo: true })}` : componentes
}

export const MENSAJE_COMPONENTE_NO_DISPONIBLE = 'Este componente ya no está disponible: quitalo o reemplazalo para poder guardar.'
const MENSAJE_CANTIDAD = `La cantidad tiene que ser un entero entre 1 y ${CANTIDAD_MAXIMA}.`
const MENSAJE_CODIGO = `El código tiene que ser un entero entre 1 y ${CODIGO_MAXIMO}.`

export type ErrorDeFila = { referencia?: string; cantidad?: string }

export type ErroresDeCombo = {
  nombre?: string
  precio?: string
  codigo?: string
  /** Reglas del conjunto: mínimo de unidades y tope de componentes. */
  componentes?: string
  /** Por `clave` de fila; solo figuran las filas con algún error. */
  filas: Record<number, ErrorDeFila>
}

export function validarCombo(f: FormularioCombo): ErroresDeCombo {
  const errores: ErroresDeCombo = { filas: {} }

  const nombre = f.nombre.trim()
  if (nombre === '') errores.nombre = 'Ingresá un nombre para el combo.'
  else if (nombre.length > LARGO_MAXIMO_DEL_NOMBRE) {
    errores.nombre = `El nombre no puede superar los ${LARGO_MAXIMO_DEL_NOMBRE} caracteres.`
  }

  if (f.precio === null || !(f.precio > 0)) errores.precio = 'Ingresá un precio mayor a cero.'
  else if (f.precio > PRECIO_MAXIMO) errores.precio = 'El precio no puede superar 999.999.999.999,99.'
  else if (tieneMasDeDosDecimales(f.precio)) errores.precio = 'El precio admite hasta dos decimales.'

  const codigo = f.codigo.trim()
  if (codigo !== '' && (!/^\d{1,6}$/.test(codigo) || Number(codigo) < 1)) errores.codigo = MENSAJE_CODIGO

  const articulosVistos = new Set<number>()
  const familiasVistas = new Set<number>()
  let unidades = 0
  let hayCantidadInvalida = false

  for (const fila of f.componentes) {
    const error: ErrorDeFila = {}
    const esArticulo = fila.tipo === 'Articulo'
    const id = esArticulo ? fila.idArticulo : fila.idFamilia

    if (!fila.disponible) error.referencia = MENSAJE_COMPONENTE_NO_DISPONIBLE
    else if (id === '') error.referencia = esArticulo ? 'Elegí un artículo.' : 'Elegí una familia.'
    else {
      const vistos = esArticulo ? articulosVistos : familiasVistas
      if (vistos.has(id)) error.referencia = esArticulo ? 'Este artículo ya está en el combo.' : 'Esta familia ya está en el combo.'
      vistos.add(id)
    }

    const cantidad = fila.cantidad.trim()
    if (cantidad === '') {
      error.cantidad = 'Ingresá la cantidad.'
      hayCantidadInvalida = true
    } else if (!/^\d+$/.test(cantidad) || Number(cantidad) < 1 || Number(cantidad) > CANTIDAD_MAXIMA) {
      error.cantidad = MENSAJE_CANTIDAD
      hayCantidadInvalida = true
    } else {
      unidades += Number(cantidad)
    }

    if (error.referencia !== undefined || error.cantidad !== undefined) errores.filas[fila.clave] = error
  }

  if (f.componentes.length > COMPONENTES_MAXIMOS) {
    errores.componentes = `Un combo admite hasta ${COMPONENTES_MAXIMOS} componentes.`
  } else if (!hayCantidadInvalida && unidades < UNIDADES_MINIMAS) {
    errores.componentes = `El combo tiene que sumar al menos ${UNIDADES_MINIMAS} unidades entre todos sus componentes.`
  }

  return errores
}

export function hayErrores(errores: ErroresDeCombo): boolean {
  return (
    errores.nombre !== undefined ||
    errores.precio !== undefined ||
    errores.codigo !== undefined ||
    errores.componentes !== undefined ||
    Object.keys(errores.filas).length > 0
  )
}

export type CampoDeError = 'nombre' | 'precio' | 'codigo' | 'componentes' | 'formulario'

export type ErrorDeServidorDeCombo = { campo: CampoDeError; mensaje: string }

export const MENSAJE_COMBO_INEXISTENTE = 'El combo ya no existe. Se actualizó el listado.'

/** Dónde cae cada rechazo conocido. Es un `Map` y no un objeto: el `codigo` viene del servidor y un código exótico
 * (`constructor`) no debe resolver contra el prototipo. El texto no se parsea nunca: el código es el contrato. */
const RECHAZOS_CONOCIDOS: ReadonlyMap<string, ErrorDeServidorDeCombo> = new Map<string, ErrorDeServidorDeCombo>([
  [
    'combo_precio_invalido',
    { campo: 'precio', mensaje: 'El precio tiene que ser mayor a cero, con hasta dos decimales y no superar 999.999.999.999,99.' },
  ],
  ['combo_componente_invalido', { campo: 'componentes', mensaje: 'Cada componente tiene que ser un artículo o una familia.' }],
  ['combo_cantidad_invalida', { campo: 'componentes', mensaje: MENSAJE_CANTIDAD }],
  [
    'combo_componente_duplicado',
    { campo: 'componentes', mensaje: 'Un artículo o una familia puede figurar una sola vez en el combo.' },
  ],
  [
    'combo_unidades_insuficientes',
    { campo: 'componentes', mensaje: `El combo tiene que sumar al menos ${UNIDADES_MINIMAS} unidades entre todos sus componentes.` },
  ],
  ['combo_componentes_excedidos', { campo: 'componentes', mensaje: `Un combo admite hasta ${COMPONENTES_MAXIMOS} componentes.` }],
  [
    'combo_componente_no_unitario',
    {
      campo: 'componentes',
      mensaje:
        'Un combo solo admite artículos que se venden por unidad: algún artículo (o algún artículo de una familia elegida) se vende por peso.',
    },
  ],
  ['combo_codigo_invalido', { campo: 'codigo', mensaje: MENSAJE_CODIGO }],
  ['combo_codigo_duplicado', { campo: 'codigo', mensaje: 'Ya hay un combo con ese código. Elegí otro.' }],
  ['combo_codigo_agotado', { campo: 'codigo', mensaje: 'El último código posible ya está en uso: indicá un código libre.' }],
  ['nombre_requerido', { campo: 'nombre', mensaje: 'Ingresá un nombre para el combo.' }],
  ['nombre_muy_largo', { campo: 'nombre', mensaje: `El nombre no puede superar los ${LARGO_MAXIMO_DEL_NOMBRE} caracteres.` }],
  [
    'activo_requerido',
    { campo: 'formulario', mensaje: 'Falta indicar si el combo está activo. Recargá la pantalla e intentá de nuevo.' },
  ],
  // Defensivo y hoy inalcanzable: el servidor toma un `disponibleOnline` ausente como "sin cambios" y el formulario lo manda siempre.
  [
    'disponible_online_requerido',
    { campo: 'formulario', mensaje: 'Falta indicar si el combo está disponible online. Recargá la pantalla e intentá de nuevo.' },
  ],
])

const AYUDA_DE_REFERENCIA_INVALIDA =
  'Quitá o reemplazá el componente que ya no existe, o elegí otra empresa, y volvé a guardar.'

/** `true` cuando el servidor dice que el combo ya no existe (404): quien llama cierra el formulario y relee el listado. */
export function esComboInexistente(e: unknown): boolean {
  return e instanceof ErrorApi && e.estado === 404
}

/** Un 5xx o un fallo sin respuesta no dice si el servidor llegó a escribir: se avisa antes de reintentar. */
export function esResultadoIncierto(e: unknown): boolean {
  return !(e instanceof ErrorApi) || e.estado >= 500
}

function detalleDeResultadoIncierto(e: unknown): string {
  const delServidor = e instanceof ErrorApi ? e.message.trim() : ''
  const esCommitAmbiguo = e instanceof ErrorApi && e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0
  return esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO
}

/** Traduce el fallo de un alta o una edición al lugar donde se muestra y al mensaje. Un código desconocido cae en el
 * formulario con el mensaje del servidor. */
export function errorDeServidorDeCombo(e: unknown): ErrorDeServidorDeCombo {
  if (esComboInexistente(e)) return { campo: 'formulario', mensaje: MENSAJE_COMBO_INEXISTENTE }
  if (esResultadoIncierto(e)) return { campo: 'formulario', mensaje: `No se pudo guardar el combo. ${detalleDeResultadoIncierto(e)}` }

  const error = e as ErrorApi
  const conocido = RECHAZOS_CONOCIDOS.get(error.codigo)
  if (conocido !== undefined) return conocido

  const detalle = error.message.trim()
  if (error.codigo === 'referencia_invalida') {
    return { campo: 'formulario', mensaje: detalle === '' ? AYUDA_DE_REFERENCIA_INVALIDA : `${detalle} ${AYUDA_DE_REFERENCIA_INVALIDA}` }
  }

  return { campo: 'formulario', mensaje: detalle === '' ? 'No se pudo guardar el combo.' : detalle }
}

/** Texto a rendir ante el fallo de una baja de combo. */
export function mensajeDeFalloDeBajaDeCombo(e: unknown): string {
  if (esComboInexistente(e)) return MENSAJE_COMBO_INEXISTENTE
  if (esResultadoIncierto(e)) return `No se pudo dar de baja el combo. ${detalleDeResultadoIncierto(e)}`

  return (e as ErrorApi).message.trim() || 'No se pudo dar de baja el combo.'
}
