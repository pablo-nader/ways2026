import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Link, MemoryRouter, Route, Routes, useLocation, useParams } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { NuevaFamilia } from './NuevaFamilia'
import { CAMPOS_COMPARTIDOS, CAMPOS_PROPIOS } from './articulos/familia'
import { ErrorApi } from '../api/cliente'
import type {
  ArticuloListado,
  CambiosDeUnArticulo,
  PaginaDe,
  PrevisualizacionDeAgrupacion,
  ResultadoDeAgrupacion,
  ValoresCompartidosDeLaFamilia,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

// ---- fixtures ----------------------------------------------------------------------------------------------

const valores: ValoresCompartidosDeLaFamilia = {
  idArea: 1,
  idCategoria: null,
  idGrupo: null,
  idProveedorHabitual: null,
  idAlicuotaIva: 5,
  unidadVenta: 'Unidad',
  unidadesPorBulto: null,
  esProducto: true,
  controlaLote: false,
  acumulaEnVenta: true,
  costoLista: 150,
  descuentoProveedor: null,
  costoNominal: null,
}

function articulo(id: number, sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  const nombres: Record<number, string> = { 31: 'Vainilla', 32: 'Frutilla', 33: 'Chocolate' }
  return {
    id,
    codigoInterno: `A00${id}`,
    nombre: nombres[id] ?? `Articulo ${id}`,
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 1,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

function pagina(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function cambiosDe(idArticulo: number, sobrescribir: Partial<CambiosDeUnArticulo> = {}): CambiosDeUnArticulo {
  return { idArticulo, campos: [], actual: valores, nuevo: valores, precios: [], ...sobrescribir }
}

function previsualizacionDe(sobrescribir: Partial<PrevisualizacionDeAgrupacion> = {}): PrevisualizacionDeAgrupacion {
  return { idArticuloReferencia: 31, idFamilia: null, articulos: [cambiosDe(32), cambiosDe(33)], problemas: [], ...sobrescribir }
}

function resultadoDe(articulos: CambiosDeUnArticulo[] = [cambiosDe(32), cambiosDe(33)]): ResultadoDeAgrupacion {
  return { idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos }
}

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

// ---- API simulada ------------------------------------------------------------------------------------------

type Escenario = {
  /** Resultados de la búsqueda de artículos; por defecto 31, 32 y 33, sin familia. */
  busqueda?: ArticuloListado[]
  catalogoCaido?: boolean
}

function mockearApi(escenario: Escenario = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/articulos?busqueda=')) {
      return Promise.resolve(pagina(escenario.busqueda ?? [articulo(31), articulo(32), articulo(33)]))
    }
    if (ruta.startsWith('/catalogos/areas')) {
      return escenario.catalogoCaido ? Promise.reject(new Error('caído')) : Promise.resolve([{ id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1 }])
    }
    if (ruta.startsWith('/catalogos/')) return Promise.resolve([])
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([{ id: 5, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true }])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** Lo que muestra la pantalla a la que se navega después de crear: el id y el aviso que le deja la creación. */
function DetalleDeFamilia() {
  const { id } = useParams()
  const ubicacion = useLocation()
  return (
    <p>
      Detalle de la familia {id}: {(ubicacion.state as { aviso?: string } | null)?.aviso ?? 'sin aviso'}
    </p>
  )
}

function montar(escenario: Escenario = {}) {
  mockearApi(escenario)
  return render(
    <MemoryRouter initialEntries={['/familias/nueva']}>
      <Routes>
        <Route path="/familias/nueva" element={<NuevaFamilia />} />
        <Route path="/familias/:id" element={<DetalleDeFamilia />} />
        <Route path="/familias" element={<p>Pantalla del listado de familias</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Como `montar`, con un enlace afuera de la pantalla que hace de menú de la aplicación: el usuario puede irse por él
 * mientras la pantalla tiene una escritura en vuelo. */
function montarConMenu() {
  mockearApi()
  return render(
    <MemoryRouter initialEntries={['/familias/nueva']}>
      <Link to="/otra">Ir a otra pantalla</Link>
      <Routes>
        <Route path="/familias/nueva" element={<NuevaFamilia />} />
        <Route path="/familias/:id" element={<DetalleDeFamilia />} />
        <Route path="/otra" element={<p>Otra pantalla</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

async function buscarYElegir(titulo: string, ...ids: number[]) {
  const dialogo = await screen.findByRole('dialog', { name: titulo })
  await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
  await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))
  for (const id of ids) await userEvent.click(await within(dialogo).findByLabelText(`Elegir A00${id} ${articulo(id).nombre}`))
  await userEvent.click(within(dialogo).getByRole('button', { name: 'Listo' }))
}

async function elegirReferencia(id = 31) {
  await userEvent.click(screen.getByRole('button', { name: /artículo de referencia$/ }))
  await buscarYElegir('Elegir el artículo de referencia', id)
}

async function agregarOtros(...ids: number[]) {
  await userEvent.click(screen.getByRole('button', { name: 'Agregar artículos' }))
  await buscarYElegir('Elegir otros artículos de la familia', ...ids)
}

async function escribirNombre(nombre: string) {
  await userEvent.type(screen.getByLabelText('Nombre de la familia'), nombre)
}

async function previsualizar() {
  await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))
}

/** Arma lo mínimo para crear: nombre, referencia 31, los artículos 32 y 33, y una previsualización sin problemas. */
async function armarYPrevisualizar(prev: PrevisualizacionDeAgrupacion = previsualizacionDe()) {
  apiPostMock.mockImplementation((ruta: string) =>
    ruta === '/familias/previsualizacion' ? Promise.resolve(prev) : Promise.resolve(resultadoDe()),
  )
  await escribirNombre('Sabores')
  await elegirReferencia()
  await agregarOtros(32, 33)
  await previsualizar()
  await screen.findByRole('heading', { name: 'A0032 — Frutilla' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('NuevaFamilia — la pantalla', () => {
  it('explica que todos toman de la referencia los trece campos compartidos y los precios, y qué es propio', () => {
    montar()

    const explicacion = screen.getByText(/Todos los artículos de la familia toman del artículo de referencia/)
    for (const campo of CAMPOS_COMPARTIDOS) {
      expect(explicacion).toHaveTextContent(campo.etiqueta.charAt(0).toLowerCase() + campo.etiqueta.slice(1))
    }
    expect(explicacion).toHaveTextContent('el precio de cada lista de precios fija')
    expect(explicacion).toHaveTextContent(`Son propios de cada artículo: ${CAMPOS_PROPIOS}.`)
  })

  /** Cláusula bajo prueba: `etiquetaEnMinuscula` baja solo la inicial: una sigla ("IVA") no se escribe en minúscula. Evidencia
   * de mutación (mutation-proof-tests): volver a `toLowerCase()` de toda la etiqueta hace fallar este test (dice "alícuota
   * de iva"); revertido, vuelve a verde. */
  it('la lista de campos compartidos no baja a minúscula las siglas: dice "alícuota de IVA"', () => {
    montar()

    const explicacion = screen.getByText(/Todos los artículos de la familia toman del artículo de referencia/)
    expect(explicacion).toHaveTextContent('alícuota de IVA')
    expect(explicacion).not.toHaveTextContent('alícuota de iva')
  })

  it('arranca sin nada elegido: no se puede previsualizar ni crear, y dice por qué', () => {
    montar()

    expect(screen.getByText('Todavía no elegiste el artículo de referencia.')).toBeInTheDocument()
    expect(screen.getByText('Todavía no elegiste otros artículos.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    expect(screen.getByText('Previsualizá antes de crear: así ves qué cambia en cada artículo.')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre de la familia')).toHaveAttribute('maxlength', '150')
  })

  it('"Volver al listado" lleva al listado', async () => {
    montar()

    await userEvent.click(screen.getByRole('link', { name: 'Volver al listado' }))

    expect(await screen.findByText('Pantalla del listado de familias')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `aria-disabled={creando}` y el `preventDefault` del enlace: con el POST en vuelo no se puede ir
   * por "Volver al listado". Evidencia de mutación (mutation-proof-tests): sacar cualquiera de los dos hace fallar este
   * test; revertido, vuelve a verde. */
  it('con el POST en vuelo "Volver al listado" no lleva a ningún lado', async () => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))
    expect(screen.getByRole('button', { name: 'Creando…' })).toBeDisabled()

    const enlace = screen.getByRole('link', { name: 'Volver al listado' })
    expect(enlace).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(enlace)

    expect(screen.queryByText('Pantalla del listado de familias')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Creando…' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el enlace se bloquea con la escritura (`creando`), no con la previsualización: es una lectura y
   * irse a mitad de ella no deja nada a medias. Evidencia de mutación (mutation-proof-tests): bloquearlo con `bloqueado`
   * (que incluye `previsualizando`) hace fallar este test; revertido, vuelve a verde. */
  it('con la previsualización en vuelo "Volver al listado" sigue llevando al listado', async () => {
    montar()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    await elegirReferencia()
    await previsualizar()
    expect(screen.getByRole('button', { name: 'Previsualizando…' })).toBeDisabled()

    expect(screen.getByRole('link', { name: 'Volver al listado' })).not.toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('link', { name: 'Volver al listado' }))

    expect(await screen.findByText('Pantalla del listado de familias')).toBeInTheDocument()
  })

  it('si un catálogo no carga lo avisa', async () => {
    montar({ catalogoCaido: true })

    expect(await screen.findByText('No se pudieron cargar: áreas. Sus valores se muestran con el código en vez del nombre.')).toBeInTheDocument()
  })
})

describe('NuevaFamilia — elegir la referencia y los demás artículos', () => {
  it('la referencia se elige entre artículos sin familia, con botones de opción, y queda nombrada', async () => {
    montar()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículo de referencia' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir el artículo de referencia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))
    expect(await within(dialogo).findByLabelText('Elegir A0031 Vainilla')).toHaveAttribute('type', 'radio')
    await userEvent.click(within(dialogo).getByLabelText('Elegir A0031 Vainilla'))
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Listo' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('A0031 — Vainilla')).toBeInTheDocument()
    expect(screen.queryByText('Todavía no elegiste el artículo de referencia.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cambiar artículo de referencia' })).toBeInTheDocument()
  })

  it.each([
    ['de la referencia', 'Elegir artículo de referencia', 'Elegir el artículo de referencia'],
    ['de los demás artículos', 'Agregar artículos', 'Elegir otros artículos de la familia'],
  ])('"Cancelar" en el buscador %s lo cierra y no elige nada', async (_cual, boton, titulo) => {
    montar()
    await userEvent.click(screen.getByRole('button', { name: boton }))
    const dialogo = await screen.findByRole('dialog', { name: titulo })

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Todavía no elegiste el artículo de referencia.')).toBeInTheDocument()
    expect(screen.getByText('Todavía no elegiste otros artículos.')).toBeInTheDocument()
  })

  it('al reabrir el buscador de la referencia, la que ya se eligió sigue marcada', async () => {
    montar()
    await elegirReferencia(31)

    await userEvent.click(screen.getByRole('button', { name: 'Cambiar artículo de referencia' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir el artículo de referencia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    expect(await within(dialogo).findByLabelText('Elegir A0031 Vainilla')).toBeChecked()
    expect(within(dialogo).getByLabelText('Elegir A0032 Frutilla')).not.toBeChecked()
  })

  it('al reabrir el buscador de los demás artículos, los que ya se eligieron siguen marcados', async () => {
    montar()
    await agregarOtros(32)

    await userEvent.click(screen.getByRole('button', { name: 'Agregar artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir otros artículos de la familia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    expect(await within(dialogo).findByLabelText('Elegir A0032 Frutilla')).toBeChecked()
    expect(within(dialogo).getByLabelText('Elegir A0033 Chocolate')).not.toBeChecked()
  })

  it('un artículo que ya tiene familia aparece pero no se puede elegir', async () => {
    montar({ busqueda: [articulo(31, { idFamilia: 9 }), articulo(32)] })

    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículo de referencia' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir el artículo de referencia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    expect(await within(dialogo).findByLabelText('Elegir A0031 Vainilla')).toBeDisabled()
    expect(within(dialogo).getByText('Ya está en una familia')).toBeInTheDocument()
  })

  it('"Quitar referencia" la saca', async () => {
    montar()
    await elegirReferencia()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar referencia' }))

    expect(screen.getByText('Todavía no elegiste el artículo de referencia.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()
  })

  it('los demás artículos se eligen con casillas, se listan y se quitan de a uno', async () => {
    montar()

    await agregarOtros(32, 33)

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('A0032 — Frutilla')).toBeInTheDocument()
    expect(screen.getByText('A0033 — Chocolate')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Quitar A0032 Frutilla' }))
    expect(screen.queryByText('A0032 — Frutilla')).not.toBeInTheDocument()
    expect(screen.getByText('A0033 — Chocolate')).toBeInTheDocument()
  })

  it('al elegir los demás, la referencia ya elegida se ve pero no se puede elegir como uno de ellos', async () => {
    montar()
    await elegirReferencia(31)

    await userEvent.click(screen.getByRole('button', { name: 'Agregar artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir otros artículos de la familia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    const casilla = await within(dialogo).findByLabelText('Elegir A0031 Vainilla')
    expect(casilla).toBeDisabled()
    expect(within(casilla.closest('tr') as HTMLElement).getByText('Ya es el artículo de referencia')).toBeInTheDocument()
  })

  it('al elegir la referencia, los artículos ya elegidos como demás no se pueden elegir', async () => {
    montar()
    await agregarOtros(32)

    await userEvent.click(screen.getByRole('button', { name: 'Elegir artículo de referencia' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir el artículo de referencia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))

    const casilla = await within(dialogo).findByLabelText('Elegir A0032 Frutilla')
    expect(casilla).toBeDisabled()
    expect(within(casilla.closest('tr') as HTMLElement).getByText('Ya está elegido como otro artículo de la familia')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el tope de 100 artículos por pedido, además de la referencia, en `excedido`. */
  it('con más de 100 artículos además de la referencia avisa cuántos sobran y no deja previsualizar', async () => {
    const muchos = Array.from({ length: 102 }, (_, i) => articulo(500 + i))
    montar({ busqueda: muchos })
    await elegirReferencia(500)
    await userEvent.click(screen.getByRole('button', { name: 'Agregar artículos' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Elegir otros artículos de la familia' })
    await userEvent.type(within(dialogo).getByLabelText('Buscar artículo'), 'art')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Buscar' }))
    await within(dialogo).findByLabelText('Elegir A00501 Articulo 501')
    for (const a of muchos.slice(1)) fireEvent.click(within(dialogo).getByLabelText(`Elegir ${a.codigoInterno} ${a.nombre}`))
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Listo' }))

    expect(
      screen.getByText('Se pueden agrupar hasta 100 artículos por vez además de la referencia: quitá 1 para poder previsualizar.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar A00501 Articulo 501' }))

    expect(screen.queryByText(/Se pueden agrupar hasta 100/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })
})

describe('NuevaFamilia — previsualizar', () => {
  it('solo se puede con una referencia elegida; sin otros artículos previsualiza la referencia sola', async () => {
    montar()
    await elegirReferencia()
    apiPostMock.mockResolvedValue(previsualizacionDe({ articulos: [] }))

    await userEvent.click(screen.getByRole('button', { name: 'Previsualizar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/familias/previsualizacion', { idArticuloReferencia: 31, idsArticulos: [] }))
  })

  it('manda la referencia y los demás artículos, y muestra qué cambia en cada uno', async () => {
    montar()
    apiPostMock.mockResolvedValue(
      previsualizacionDe({ articulos: [cambiosDe(32, { campos: ['costo_lista'], actual: { ...valores, costoLista: 90 } }), cambiosDe(33)] }),
    )
    await elegirReferencia()
    await agregarOtros(32, 33)

    await previsualizar()

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/familias/previsualizacion', { idArticuloReferencia: 31, idsArticulos: [32, 33] }))
    expect(await screen.findByRole('heading', { name: 'A0032 — Frutilla' })).toBeInTheDocument()
    const fila = screen.getByText('Costo de lista', { selector: 'td' }).closest('tr') as HTMLElement
    expect(within(fila).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Costo de lista', '$ 90,00', '$ 150,00'])
    expect(screen.getByText('Ya está alineado con la referencia: no cambia nada.')).toBeInTheDocument()
  })

  it('un fallo de la previsualización se muestra con la ayuda de su código', async () => {
    montar()
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'referencia_invalida', 'No existe el área 1.'))
    await elegirReferencia()

    await previsualizar()

    expect(await screen.findByText(/No existe el área 1\. Revisá que todos los artículos existan/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
  })

  it('con la previsualización en vuelo todo queda inerte', async () => {
    const lectura = diferida<PrevisualizacionDeAgrupacion>()
    montar()
    apiPostMock.mockReturnValue(lectura.promesa)
    await elegirReferencia()
    await agregarOtros(32)

    await previsualizar()

    expect(screen.getByRole('button', { name: 'Previsualizando…' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre de la familia')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cambiar artículo de referencia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar referencia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar A0032 Frutilla' })).toBeDisabled()
    await act(async () => {
      lectura.resolver(previsualizacionDe())
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled())
  })

  it('cambiar la referencia, o los demás artículos, descarta la previsualización y vuelve a bloquear "Crear familia"', async () => {
    montar()
    await armarYPrevisualizar()
    await userEvent.click(screen.getByRole('button', { name: 'Quitar A0033 Chocolate' }))

    expect(screen.queryByRole('heading', { name: 'A0032 — Frutilla' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()

    apiPostMock.mockResolvedValue(previsualizacionDe())
    await previsualizar()
    await screen.findByRole('heading', { name: 'A0032 — Frutilla' })
    await userEvent.click(screen.getByRole('button', { name: 'Quitar referencia' }))

    expect(screen.queryByRole('heading', { name: 'A0032 — Frutilla' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
  })

  it('elegir más artículos desde el selector también descarta la previsualización', async () => {
    montar({ busqueda: [articulo(31), articulo(32), articulo(33), articulo(34)] })
    await armarYPrevisualizar()

    await agregarOtros(34)

    expect(screen.queryByRole('heading', { name: 'A0032 — Frutilla' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })

  it('elegir otra referencia desde el selector también descarta la previsualización', async () => {
    montar({ busqueda: [articulo(31), articulo(32), articulo(33), articulo(34)] })
    await armarYPrevisualizar()

    await elegirReferencia(34)

    expect(screen.getByText('A0034 — Articulo 34')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'A0032 — Frutilla' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
  })

  it('cambiar el nombre NO descarta la previsualización: no habla del nombre', async () => {
    montar()
    await armarYPrevisualizar()

    await userEvent.type(screen.getByLabelText('Nombre de la familia'), ' 2')

    expect(screen.getByRole('heading', { name: 'A0032 — Frutilla' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeEnabled()
  })
})

describe('NuevaFamilia — crear', () => {
  it('"Crear familia" exige nombre, referencia y una previsualización sin problemas', async () => {
    montar()
    apiPostMock.mockResolvedValue(previsualizacionDe())
    await elegirReferencia()
    await agregarOtros(32, 33)
    await previsualizar()
    await screen.findByRole('heading', { name: 'A0032 — Frutilla' })
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()

    await escribirNombre('   ')
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    await escribirNombre('Sabores')

    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeEnabled()
    expect(screen.queryByText(/Previsualizá antes de crear/)).not.toBeInTheDocument()
  })

  /** Cláusulas bajo prueba: `problemas.length === 0` dentro de `sinProblemas` (los problemas de la previsualización
   * bloquean "Crear familia") y la guarda `!listaParaCrear(...)` de `crear`. Con el botón deshabilitado el teclado no
   * llega a ningún envío, así que `fireEvent.submit` es una palanca sintética que alcanza la guarda por debajo de él.
   * Evidencia de mutación (mutation-proof-tests): sacar esa guarda hace fallar este test y el siguiente (se emite el
   * POST con problemas); revertida, vuelven a verde. */
  it('los problemas de la previsualización se muestran y bloquean "Crear familia"', async () => {
    montar()
    await armarYPrevisualizar(
      previsualizacionDe({
        problemas: [{ codigo: 'familia_precio_inalineable', mensaje: 'No se puede alinear el precio de "Frutilla" en "General".', idArticulo: 32, idListaPrecio: 2 }],
      }),
    )

    expect(screen.getByText(/No se puede alinear el precio de "Frutilla" en "General"\. Un precio nunca se quita/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('button', { name: 'Crear familia' }).closest('form') as HTMLFormElement)
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/familias')).toHaveLength(0)
  })

  /** Cláusula bajo prueba: `referenciaConFamilia` en `sinProblemas`: si la previsualización dice que la referencia ya
   * es miembro de una familia (`idFamilia`), agrupar sería sumar a ESA familia, no crear una nueva. El enlace del aviso
   * lleva al detalle de esa familia (`Link` con `idFamiliaDeLaReferencia`). */
  it('una referencia que ya es miembro de una familia bloquea "Crear familia" y manda a su detalle', async () => {
    montar()
    await armarYPrevisualizar(previsualizacionDe({ idFamilia: 9 }))

    expect(
      screen.getByText('El artículo de referencia ya es miembro de una familia: agrupar no mueve a nadie de la que tiene. Para sumarle artículos usá el detalle de su familia.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('button', { name: 'Crear familia' }).closest('form') as HTMLFormElement)
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/familias')).toHaveLength(0)
    // Y "manda a su detalle": el aviso trae el enlace a la familia que ya tiene la referencia.
    const enlace = screen.getByRole('link', { name: 'Ver el detalle de su familia' })
    expect(enlace).toHaveAttribute('href', '/familias/9')
    await userEvent.click(enlace)
    expect(await screen.findByText(/Detalle de la familia 9/)).toBeInTheDocument()
  })

  it('crea la familia: POST con el nombre recortado, la referencia y los demás, y navega a su detalle con el aviso', async () => {
    montar()
    await armarYPrevisualizar()
    await escribirNombre('  ')

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(await screen.findByText('Detalle de la familia 7: Se creó la familia "Sabores" con 3 artículos.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/familias', { nombre: 'Sabores', idArticuloReferencia: 31, idsArticulos: [32, 33] })
  })

  it('Enter en el nombre crea la familia cuando está lista', async () => {
    montar()
    await armarYPrevisualizar()

    await userEvent.type(screen.getByLabelText('Nombre de la familia'), '{Enter}')

    expect(await screen.findByText(/Detalle de la familia 7/)).toBeInTheDocument()
  })

  it('Enter en el nombre sin previsualizar no crea nada', async () => {
    montar()
    await escribirNombre('Sabores')
    await elegirReferencia()

    await userEvent.type(screen.getByLabelText('Nombre de la familia'), '{Enter}')

    expect(apiPostMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: la ventana inerte del POST (react-async-state regla 5) y `creandoRef`, el espejo
   * sincrónico de reentrancia (regla 11). */
  it('con el POST en vuelo todo queda inerte y dos envíos sincrónicos emiten un solo POST', async () => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation(() => new Promise(() => {}))

    const formulario = screen.getByRole('button', { name: 'Crear familia' }).closest('form') as HTMLFormElement
    await act(async () => {
      fireEvent.submit(formulario)
      fireEvent.submit(formulario)
      await Promise.resolve()
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/familias')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Creando…' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre de la familia')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cambiar artículo de referencia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar referencia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar artículos' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar A0032 Frutilla' })).toBeDisabled()
  })

  it('un nombre repetido (409) se muestra con su ayuda y CONSERVA la previsualización para volver a intentar con otro nombre', async () => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias'
        ? Promise.reject(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Sabores" en este tenant.'))
        : Promise.resolve(previsualizacionDe()),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(await screen.findByText('Ya existe una familia llamada "Sabores" en este tenant. Elegí otro nombre.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'A0032 — Frutilla' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeEnabled()
    expect(screen.getByLabelText('Nombre de la familia')).toBeEnabled()
  })

  it.each([
    [409, 'articulo_en_otra_familia', 'El artículo 32 ya está en la familia "Talles".', 'Agrupar no mueve a nadie de su familia'],
    [422, 'familia_precio_inalineable', 'No se puede alinear el precio de "Frutilla" en "General".', 'Un precio nunca se quita'],
    [400, 'demasiados_articulos', 'Son demasiados pares.', 'Elegí menos artículos'],
    [400, 'referencia_invalida', 'No existe el artículo 99.', 'Revisá que todos los artículos existan'],
  ])('%i %s: muestra el motivo con su ayuda y descarta la previsualización, que quedó vieja', async (estado, codigo, mensaje, ayuda) => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias' ? Promise.reject(new ErrorApi(estado, codigo, mensaje)) : Promise.resolve(previsualizacionDe()),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(await screen.findByText(new RegExp(`${mensaje.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} ${ayuda}`))).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'A0032 — Frutilla' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previsualizar' })).toBeEnabled()
    // Lo elegido se conserva: solo hay que volver a previsualizar.
    expect(screen.getByText('A0032 — Frutilla')).toBeInTheDocument()
  })

  it.each([
    ['nombre_requerido', 'El campo nombre es obligatorio.'],
    ['nombre_muy_largo', 'El campo nombre no puede superar los 150 caracteres.'],
  ])('%s: un nombre rechazado por el servidor conserva la previsualización, igual que uno repetido', async (codigo, mensaje) => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias' ? Promise.reject(new ErrorApi(400, codigo, mensaje)) : Promise.resolve(previsualizacionDe()),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(await screen.findByText(mensaje)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'A0032 — Frutilla' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear familia' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `setErrorDeCreacion('')` al empezar `crear`. El rechazo del intento anterior (que conservó la
   * previsualización) no puede seguir en pantalla mientras el nuevo POST está en vuelo. */
  it('crear de nuevo borra el rechazo del intento anterior apenas empieza', async () => {
    const segundo = diferida<ResultadoDeAgrupacion>()
    let creaciones = 0
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta !== '/familias') return Promise.resolve(previsualizacionDe())

      return ++creaciones === 1
        ? Promise.reject(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Sabores" en este tenant.'))
        : segundo.promesa
    })
    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))
    await screen.findByText(/Ya existe una familia llamada "Sabores"/)

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(creaciones).toBe(2)
    expect(screen.getByRole('button', { name: 'Creando…' })).toBeDisabled()
    expect(screen.queryByText(/Ya existe una familia llamada "Sabores"/)).not.toBeInTheDocument()
    await act(async () => {
      segundo.resolver(resultadoDe())
    })
  })

  /** Cláusulas bajo prueba: cada cambio de lo elegido, y volver a previsualizar, borran el motivo del rechazo anterior. */
  describe('después de que el servidor rechazó la creación', () => {
    const motivo = /El artículo 32 ya está en la familia "Talles"\./

    async function conRechazoVisible() {
      montar({ busqueda: [articulo(31), articulo(32), articulo(33), articulo(34)] })
      await armarYPrevisualizar()
      apiPostMock.mockImplementation((ruta: string) =>
        ruta === '/familias'
          ? Promise.reject(new ErrorApi(409, 'articulo_en_otra_familia', 'El artículo 32 ya está en la familia "Talles".'))
          : Promise.resolve(previsualizacionDe()),
      )
      await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))
      await screen.findByText(motivo)
    }

    it.each([
      ['elegir otra referencia desde el buscador', () => elegirReferencia(34)],
      ['elegir otros artículos desde el buscador', () => agregarOtros(34)],
      ['quitar la referencia', () => userEvent.click(screen.getByRole('button', { name: 'Quitar referencia' }))],
      ['quitar uno de los otros artículos', () => userEvent.click(screen.getByRole('button', { name: 'Quitar A0033 Chocolate' }))],
      ['volver a previsualizar', () => previsualizar()],
    ])('%s lo borra', async (_accion, hacer) => {
      await conRechazoVisible()

      await hacer()

      expect(screen.queryByText(motivo)).not.toBeInTheDocument()
    })
  })

  /** Cláusula bajo prueba: `mensajeDeFalloDeEscritura` en `crear` (react-async-state regla 7: el aviso dice lo que de verdad
   * se sabe). Con la red caída o un 5xx no se sabe si el POST llegó a commitear: decir solo "No se pudo crear" invita a
   * crear de nuevo una familia que quizá ya existe. Evidencia de mutación (mutation-proof-tests): volver a `mensajeDeError`
   * hace fallar estas dos; revertido, vuelven a verde. */
  it.each<[string, () => unknown]>([
    ['un fallo que no es del servidor (la red)', () => new TypeError('Failed to fetch')],
    ['un 5xx sin código estable', () => new ErrorApi(500, 'error_interno', 'Se cayó el servidor.')],
  ])('con %s no se sabe si se creó: lo dice y no anexa el texto del servidor', async (_caso, error) => {
    montar()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/familias' ? Promise.reject(error()) : Promise.resolve(previsualizacionDe()),
    )

    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))

    expect(
      await screen.findByText('No se pudo crear la familia. No se pudo confirmar el resultado: verificá el listado antes de reintentar.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Se cayó el servidor/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `if (!montado.current) return` de `crear`, antes de navegar. Quien se fue mientras el POST estaba
   * en vuelo (el menú, el Atrás) no vuelve a una pantalla que no pidió. El control positivo es la prueba de arriba: con el
   * usuario en la pantalla, sí navega al detalle. Evidencia de mutación (mutation-proof-tests): sacar esa línea hace fallar
   * este test (aparece el detalle de la familia creada); revertido, vuelve a verde. */
  it('si el usuario se fue mientras el POST estaba en vuelo, la respuesta no lo manda al detalle de la familia', async () => {
    const creacion = diferida<ResultadoDeAgrupacion>()
    montarConMenu()
    await armarYPrevisualizar()
    apiPostMock.mockImplementation(() => creacion.promesa)
    await userEvent.click(screen.getByRole('button', { name: 'Crear familia' }))
    await userEvent.click(screen.getByRole('link', { name: 'Ir a otra pantalla' }))
    expect(await screen.findByText('Otra pantalla')).toBeInTheDocument()

    await act(async () => {
      creacion.resolver(resultadoDe())
    })

    expect(screen.getByText('Otra pantalla')).toBeInTheDocument()
    expect(screen.queryByText(/Detalle de la familia/)).not.toBeInTheDocument()
  })
})
