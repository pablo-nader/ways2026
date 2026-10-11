import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CompraEditor } from './CompraEditor'
import { RutaProtegida } from '../auth/RutaProtegida'
import { cargarConcepto, cargarFila, filaDeItems, filasDeItems } from './compras/ayudasDePruebaDeGrilla'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type {
  AlicuotaIvaListado,
  ArticuloListado,
  CompraDetalle,
  GastoDeAdministracionListado,
  ItemDeCompra,
  ListaPrecioListado,
  ProveedorListado,
  PuntoVentaListado,
  ResultadoAnulacion,
  ResultadoAplicarPrecio,
  TipoComprobanteListado,
  UsuarioAutenticado,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

// Permite retener la navegación para observar la pantalla en la ventana entre guardar y que la
// navegación la reemplace; por defecto navega de verdad.
const navegacion = vi.hoisted(() => ({ retener: false, llamadas: [] as unknown[][] }))

vi.mock('react-router', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('react-router')>()
  return {
    ...real,
    useNavigate: () => {
      const navegar = real.useNavigate()
      return (...args: unknown[]) => {
        if (navegacion.retener) {
          navegacion.llamadas.push(args)
          return
        }
        return (navegar as (...a: unknown[]) => unknown)(...args)
      }
    },
  }
})

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown?])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor Uno SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
    ...sobrescribir,
  }
}

function tipoFixture(sobrescribir: Partial<TipoComprobanteListado> = {}): TipoComprobanteListado {
  return {
    id: 5,
    clase: 'Compra',
    codigo: 'C-FA',
    nombre: 'Factura A de compra',
    letra: 'A',
    signo: 1,
    discriminaIva: true,
    esFiscal: false,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    registraLibroIva: true,
    ...sobrescribir,
  }
}

function alicuotaFixture(sobrescribir: Partial<AlicuotaIvaListado> = {}): AlicuotaIvaListado {
  return { id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true, ...sobrescribir }
}

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 2,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Casa Central',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    modo: 'Web',
    ...sobrescribir,
  }
}

function listaPrecioFixture(sobrescribir: Partial<ListaPrecioListado> = {}): ListaPrecioListado {
  return {
    id: 1,
    nombre: 'Lista General',
    activo: true,
    idEmpresa: null,
    esDefault: true,
    modo: 'Fija',
    idListaBase: null,
    porcentaje: null,
    ...sobrescribir,
  }
}

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 20,
    codigoInterno: 'ART-20',
    nombre: 'Leche en polvo 800g',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 3,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: true,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

function itemFixture(sobrescribir: Partial<ItemDeCompra> = {}): ItemDeCompra {
  return {
    orden: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: 10,
    bultos: null,
    unidadesPorBulto: null,
    costoUnitario: 100,
    descuento: 50,
    idAlicuotaIva: 3,
    porcentajeIva: 21,
    total: 950,
    actualizaCosto: true,
    precioSugerido: 114.95,
    codigoLote: null,
    fechaVencimiento: null,
    idLote: null,
    codigoProveedor: null,
    ...sobrescribir,
  }
}

function compraFixture(sobrescribir: Partial<CompraDetalle> = {}): CompraDetalle {
  return {
    id: 1,
    idProveedor: 1,
    idTipoComprobante: 5,
    idPuntoVenta: 2,
    numeroExterno: '0003-00012345',
    fechaComprobante: '2026-08-01',
    fechaRecepcion: null,
    subtotal: 1000,
    descuentoTotal: 50,
    ivaTotal: 199.5,
    total: 1149.5,
    observaciones: null,
    estado: 'Borrador',
    items: [itemFixture()],
    idOrdenCompra: null,
    discriminaIva: true,
    alicuotas: [{ idAlicuotaIva: 3, porcentaje: 21, neto: 950, iva: 199.5 }],
    preciosIncluyenIva: false,
    percepciones: [],
    pagado: 0,
    saldoPendiente: 0,
    ...sobrescribir,
  }
}

function renderEditor(idCompra: string | number = 1) {
  return render(
    <MemoryRouter initialEntries={[`/compras/${idCompra}`]}>
      <Routes>
        <Route path="/compras/:id" element={<CompraEditor />} />
        <Route path="/compras" element={<div>Listado de compras</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** stage-16-ordenes-de-compra, Slice 6: monta con una ruta completa (incluye `?idOrdenCompra=`
 * cuando hace falta) — `renderEditor` fija `/compras/:id` sin lugar para query params. */
function renderEditorEnRuta(ruta: string) {
  return render(
    <MemoryRouter initialEntries={[ruta]}>
      <Routes>
        <Route path="/compras/:id" element={<CompraEditor />} />
        <Route path="/compras" element={<div>Listado de compras</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/compras/:id` (decisión 11: la
 * lectura sigue `Politicas.OperacionDePos`) — a diferencia de `renderEditor`, esto prueba que el
 * rol realmente llega a la pantalla en vez de asumirlo. */
function renderEditorProtegido(idCompra: string | number = 1) {
  return render(
    <MemoryRouter initialEntries={[`/compras/${idCompra}`]}>
      <Routes>
        <Route
          path="/compras/:id"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
              <CompraEditor />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Rutas de referencia compartidas por casi todos los tests — cada test suma encima las rutas de
 * compra que le hacen falta. */
function mockearReferencia(
  sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined,
  tipos: TipoComprobanteListado[] = [tipoFixture()],
  puntosVenta: PuntoVentaListado[] = [puntoVentaFixture()],
  proveedores: ProveedorListado[] = [proveedorFixture()],
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: proveedores, total: proveedores.length, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve(tipos)
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([alicuotaFixture()])
    if (ruta === '/puntos-venta') return Promise.resolve(puntosVenta)
    if (ruta === '/catalogos/listas-precio') return Promise.resolve([listaPrecioFixture()])
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  navegacion.retener = false
  navegacion.llamadas = []
  usuarioActual = usuarioFixture()
})

describe('CompraEditor — borrador existente', () => {
  it('carga el header y los items del borrador en la grilla, con una fila vacía al final', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    renderEditor()

    expect(await screen.findByDisplayValue('00012345')).toBeInTheDocument()
    const filas = filasDeItems()
    expect(filas).toHaveLength(2)

    const fila = within(filas[0])
    expect(fila.getByLabelText(/^Detalle, línea \d+$/)).toHaveValue('Fideos 500g')
    expect(fila.getByLabelText(/^UM, línea \d+$/)).toHaveValue('Unidad')
    expect(fila.getByLabelText(/^Cantidad, línea \d+$/)).toHaveValue(10)
    expect(fila.getByLabelText(/^Importe, línea \d+$/)).toHaveValue('100,0000')
    expect(fila.getByLabelText(/^Descuento \(%\), línea \d+$/)).toHaveValue('5,00')
    await waitFor(() => expect(fila.getByLabelText(/^IVA, línea \d+$/)).toHaveValue('3'))
    expect(fila.getByText('$ 950,00')).toBeInTheDocument()

    const vacia = within(filas[1])
    expect(vacia.getByLabelText(/^Código, línea \d+$/)).toHaveValue('')
    expect(vacia.getByLabelText(/^Detalle, línea \d+$/)).toHaveValue('')

    expect(screen.getByRole('button', { name: 'Guardar' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '+ Agregar línea' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '+ Agregar concepto' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cargar por total' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar compra' })).not.toBeInTheDocument()
  })

  it('guardar borrador manda un PUT con el replace-set completo (header + items editados) y sin la fila vacía', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture({ observaciones: 'Actualizado' }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')

    const importe = within(filaDeItems(0)).getByLabelText(/^Importe, línea \d+$/)
    await usuario.clear(importe)
    await usuario.type(importe, '120')

    await usuario.type(screen.getByLabelText('Observaciones'), 'Actualizado')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/compras/1')
    expect(cuerpo.observaciones).toBe('Actualizado')
    expect(cuerpo.items).toHaveLength(1)
    // Tocar el importe recalcula el descuento desde el 5 %: 5 % de 10 × 120.
    expect((cuerpo.items as Record<string, unknown>[])[0]).toMatchObject({ costoUnitario: 120, descuento: 60, unidades: 10 })
    // replace-set: el header viaja completo, no solo el campo tocado.
    expect(cuerpo.idProveedor).toBe(1)
    expect(cuerpo.idTipoComprobante).toBe(5)
    expect(cuerpo.idPuntoVenta).toBe(2)

    expect(await screen.findByText('Borrador guardado.')).toBeInTheDocument()
  })

  it('guardar un borrador sin tocar sus líneas las manda exactamente como estaban guardadas', async () => {
    const guardado = compraFixture({
      items: [itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0.07 })],
    })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(guardado) : undefined))
    apiPutMock.mockResolvedValue(guardado)
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, { items: Record<string, unknown>[] }]
    expect(cuerpo.items).toEqual([
      {
        idArticulo: 10,
        descripcion: 'Fideos 500g',
        unidades: 2,
        bultos: 3,
        unidadesPorBulto: 6,
        costoUnitario: 10,
        descuento: 0.07,
        idAlicuotaIva: 3,
        actualizaCosto: true,
        codigoLote: null,
        fechaVencimiento: null,
        codigoProveedor: null,
      },
    ])
  })

  it('un 409 compra_duplicada al guardar se muestra tal cual, sin envolver el mensaje', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra confirmada con ese número.'))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('Ya existe una compra confirmada con ese número.')).toBeInTheDocument()
  })

  it('Guardar actualiza el borrador y lo confirma en una sola acción; un doble click síncrono manda un solo PUT y un solo confirmar', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    let resolverConfirmar: (valor: CompraDetalle) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/confirmar') return new Promise((resolve) => (resolverConfirmar = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    renderEditor()
    await screen.findByDisplayValue('00012345')

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    act(() => {
      guardar.click()
      guardar.click()
    })
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/compras/1/confirmar'))

    // Mientras se confirma, toda la acción queda inerte.
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeDisabled()

    await act(async () => {
      resolverConfirmar(compraFixture({ estado: 'Confirmada', fechaRecepcion: '2026-08-05T12:00:00Z' }))
    })
    expect(await screen.findByText('Compra confirmada: el stock y el costo ya se actualizaron.')).toBeInTheDocument()

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock.mock.calls.filter((c: unknown[]) => c[0] === '/compras/1/confirmar')).toHaveLength(1)
    // Primero se guarda, después se confirma.
    expect(apiPutMock.mock.invocationCallOrder[0]).toBeLessThan(apiPostMock.mock.invocationCallOrder[0])
    // La compra confirmada se muestra de solo lectura, sin la grilla.
    expect(screen.queryByRole('table', { name: 'Items de la compra' })).not.toBeInTheDocument()
  })

  it('si el guardado falla no se intenta confirmar', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra confirmada con ese número.'))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ya existe una compra confirmada con ese número.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un 400 al confirmar deja el borrador guardado a la vista con el error, y reintentar actualiza el mismo borrador', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/confirmar') {
        return Promise.reject(
          new ErrorApi(400, 'compra_incompleta_para_confirmar', 'La compra necesita número de comprobante y fecha para confirmarse.'),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('La compra necesita número de comprobante y fecha para confirmarse.')).toBeInTheDocument()
    expect(screen.getByText('El borrador quedó guardado, pero la compra NO se confirmó.')).toBeInTheDocument()
    expect(screen.queryByText(/Compra confirmada/)).not.toBeInTheDocument()
    // Sigue siendo el borrador editable.
    expect(screen.getByRole('table', { name: 'Items de la compra' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock.mock.calls.filter((c: unknown[]) => c[0] === '/compras')).toHaveLength(0)
  })
})

describe('CompraEditor — compra confirmada', () => {
  it('muestra los items de solo lectura con el precio sugerido, sin grilla editable', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    renderEditor()

    await screen.findByText('Fideos 500g')
    expect(screen.queryByLabelText(/^Importe, línea \d+$/)).not.toBeInTheDocument()
    expect(screen.getByText('$ 114,95')).toBeInTheDocument() // precio sugerido
    expect(screen.getByRole('button', { name: 'Anular compra' })).toBeInTheDocument()
  })

  it('anular: reporta honestamente los gastos ligados colgados, y un doble click manda un solo POST', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    let resolverAnular: (valor: ResultadoAnulacion) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/anular') return new Promise((resolve) => (resolverAnular = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByRole('button', { name: 'Anular compra' })
    await screen.findByRole('button', { name: 'Aplicar' })
    await usuario.click(screen.getByRole('button', { name: 'Anular compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero anular esta compra/))

    const botonAnularFinal = screen.getByRole('button', { name: 'Anular' })
    await usuario.click(botonAnularFinal)
    await usuario.click(botonAnularFinal)

    // regla 9, gate simétrico: anulando bloquea aplicar-precios mientras la anulación está en vuelo.
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeDisabled()

    resolverAnular({ compra: compraFixture({ estado: 'Anulada' }), gastosLigados: 2 })
    expect(await screen.findByText(/Quedan 2 gasto\(s\) ligado\(s\)/)).toBeInTheDocument()

    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1/anular')
    expect(llamadas).toHaveLength(1)
  })

  it('el refusal por stock negativo (409 compra_anulacion_stock_negativo) se muestra claro, nombrando el artículo', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/anular') {
        return Promise.reject(
          new ErrorApi(409, 'compra_anulacion_stock_negativo', 'El artículo 10 quedaría con stock negativo al anular esta compra.'),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByRole('button', { name: 'Anular compra' })
    await usuario.click(screen.getByRole('button', { name: 'Anular compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero anular esta compra/))
    await usuario.click(screen.getByRole('button', { name: 'Anular' }))

    expect(await screen.findByText('El artículo 10 quedaría con stock negativo al anular esta compra.')).toBeInTheDocument()
  })

  it('aplicar precio sugerido: éxito parcial por línea, un 2xx nunca se reporta como fallo', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    const resultados: ResultadoAplicarPrecio[] = [{ orden: 1, idArticulo: 10, aplicado: true, precio: 114.95, error: null }]
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/precios') return Promise.resolve(resultados)
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByLabelText('Lista de precios')
    await usuario.selectOptions(screen.getByLabelText('Lista de precios'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(await screen.findByText('Precios aplicados — revisá el detalle por línea abajo.')).toBeInTheDocument()
    expect(screen.getByText('Aplicado')).toBeInTheDocument()

    const [, cuerpo] = apiPostMock.mock.calls.find((call: unknown[]) => call[0] === '/compras/1/precios') as [string, Record<string, unknown>]
    expect(cuerpo).toEqual({ idListaPrecio: 1, confirmarReemplazo: false })
  })

  it('aplicar precio sugerido: dos líneas del mismo artículo se listan cada una con su línea y su resultado, sin claves de React repetidas', async () => {
    // Una compra admite dos líneas del mismo artículo, así que `idArticulo` no identifica la fila del
    // resultado: la clave es `orden`. Con `key={r.idArticulo}` las dos filas igualmente se renderizan
    // con su propio contenido y lo único que cambia es el aviso de React por claves repetidas, que es
    // lo que se espía.
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      mockearReferencia((ruta) =>
        ruta === '/compras/1'
          ? Promise.resolve(
              compraFixture({
                estado: 'Confirmada',
                items: [
                  itemFixture({ orden: 1, idArticulo: null, descripcion: 'Flete', actualizaCosto: false, precioSugerido: null }),
                  itemFixture({ orden: 2, precioSugerido: 114.95 }),
                  itemFixture({ orden: 3, precioSugerido: 229.9 }),
                ],
              }),
            )
          : undefined,
      )
      const resultados: ResultadoAplicarPrecio[] = [
        { orden: 2, idArticulo: 10, aplicado: true, precio: 114.95, error: null },
        {
          orden: 3,
          idArticulo: 10,
          aplicado: false,
          precio: null,
          error: 'Ya existe un precio pendiente para este artículo en esta lista; confirmá el reemplazo.',
        },
      ]
      apiPostMock.mockImplementation((ruta: string) => {
        if (ruta === '/compras/1/precios') return Promise.resolve(resultados)
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })
      const usuario = userEvent.setup()

      renderEditor()
      const lista = await screen.findByLabelText('Lista de precios')
      await within(lista).findByRole('option', { name: 'Lista General' })
      await usuario.selectOptions(lista, '1')
      await usuario.click(screen.getByRole('button', { name: 'Aplicar' }))

      const encabezadoDeLinea = await screen.findByRole('columnheader', { name: 'Línea' })
      const [, cuerpo] = within(encabezadoDeLinea.closest('table') as HTMLElement).getAllByRole('rowgroup')
      const filas = within(cuerpo).getAllByRole('row')
      expect(filas).toHaveLength(2)

      const celdasPorFila = filas.map((fila) => within(fila).getAllByRole('cell').map((celda) => celda.textContent))
      expect(celdasPorFila).toEqual([
        ['2', '#10', 'Aplicado', '$ 114,95'],
        ['3', '#10', 'Ya existe un precio pendiente para este artículo en esta lista; confirmá el reemplazo.', '—'],
      ])

      const avisosDeClaveRepetida = errorSpy.mock.calls.filter((llamada) =>
        llamada.some((argumento) => typeof argumento === 'string' && argumento.includes('Encountered two children with the same key')),
      )
      expect(avisosDeClaveRepetida).toEqual([])
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('aplicar precio sugerido en vuelo bloquea "Anular compra" (regla 9, gate simétrico con anulando)', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    let resolverAplicar: (valor: ResultadoAplicarPrecio[]) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/precios') return new Promise((resolve) => (resolverAplicar = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByLabelText('Lista de precios')
    await usuario.selectOptions(screen.getByLabelText('Lista de precios'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(screen.getByRole('button', { name: 'Anular compra' })).toBeDisabled()

    resolverAplicar([{ orden: 1, idArticulo: 10, aplicado: true, precio: 114.95, error: null }])
    await waitFor(() => expect(screen.getByRole('button', { name: 'Anular compra' })).toBeEnabled())
  })

  it('con el panel de anular ya abierto y tildado, aplicar precio sugerido en vuelo bloquea el botón interno Anular (regla 9)', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    let resolverAplicar: (valor: ResultadoAplicarPrecio[]) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/precios') return new Promise((resolve) => (resolverAplicar = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByRole('button', { name: 'Anular compra' })
    await usuario.click(screen.getByRole('button', { name: 'Anular compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero anular esta compra/))

    const botonAnularFinal = screen.getByRole('button', { name: 'Anular' })
    expect(botonAnularFinal).toBeEnabled()

    await screen.findByLabelText('Lista de precios')
    await usuario.selectOptions(screen.getByLabelText('Lista de precios'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(botonAnularFinal).toBeDisabled()

    resolverAplicar([{ orden: 1, idArticulo: 10, aplicado: true, precio: 114.95, error: null }])
    await waitFor(() => expect(botonAnularFinal).toBeEnabled())

    const llamadasAnular = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1/anular')
    expect(llamadasAnular).toHaveLength(0)
  })
})

/** Elige proveedor, tipo y punto de venta de una compra nueva, esperando los DATOS (no el select). */
async function completarEncabezadoDeCompraNueva(usuario: ReturnType<typeof userEvent.setup>) {
  await screen.findByRole('option', { name: 'Proveedor Uno SA' })
  await screen.findByRole('option', { name: /Factura A de compra/ })
  await screen.findByRole('option', { name: 'Casa Central' })
  await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
  await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
  await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
  await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeEnabled())
}

describe('CompraEditor — compra nueva', () => {
  it('guardar borrador manda un POST con el header completo y solo las líneas completas', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await cargarConcepto(usuario, '500', 'Flete')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, { items: unknown[] }]
    expect(ruta).toBe('/compras')
    expect(cuerpo).toMatchObject({ idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2 })
    expect(cuerpo.items).toHaveLength(1)
    expect(apiPostMock.mock.calls.some((c: unknown[]) => String(c[0]).endsWith('/confirmar'))).toBe(false)
  })

  it('un 409 compra_duplicada al guardar se muestra sin navegar (sigue en modo nuevo)', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.reject(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra con ese número.'))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('Ya existe una compra con ese número.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Nueva compra' })).toBeInTheDocument()
  })

  it('Guardar de una compra nueva dice qué falta, antes de llamar a la API', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(
        'Para guardar y confirmar falta: el número del comprobante, la fecha del comprobante y al menos una línea completa.',
      ),
    ).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  async function prepararCompraNuevaListaParaConfirmar(usuario: ReturnType<typeof userEvent.setup>) {
    await completarEncabezadoDeCompraNueva(usuario)
    await usuario.type(screen.getByLabelText('Punto de venta del comprobante'), '3')
    await usuario.type(screen.getByLabelText('Número del comprobante'), '12345')
    await usuario.type(screen.getByLabelText('Fecha del comprobante'), '01/08/2026')
    await usuario.tab()
    await cargarConcepto(usuario, '500', 'Flete')
  }

  it('Guardar de una compra nueva la crea, la confirma y aterriza en la compra confirmada', async () => {
    mockearReferencia((ruta) =>
      ruta === '/compras/99'
        ? Promise.resolve(compraFixture({ id: 99, estado: 'Confirmada', items: [itemFixture({ idArticulo: null, descripcion: 'Flete' })] }))
        : undefined,
    )
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      if (ruta === '/compras/99/confirmar') {
        return Promise.resolve(compraFixture({ id: 99, estado: 'Confirmada', items: [itemFixture({ idArticulo: null, descripcion: 'Flete' })] }))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await prepararCompraNuevaListaParaConfirmar(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Compra confirmada.')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Anular compra' })).toBeInTheDocument()
    expect(apiPostMock.mock.calls.map((c: unknown[]) => c[0])).toEqual(['/compras', '/compras/99/confirmar'])
  })

  async function guardarUnaCompraNuevaCuyaConfirmacionFalla(usuario: ReturnType<typeof userEvent.setup>) {
    mockearReferencia((ruta) => (ruta === '/compras/99' ? Promise.resolve(compraFixture({ id: 99 })) : undefined))
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      if (ruta === '/compras/99/confirmar') {
        return Promise.reject(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra confirmada con ese número.'))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    apiPutMock.mockResolvedValue(compraFixture({ id: 99 }))

    renderEditor('nueva')
    await prepararCompraNuevaListaParaConfirmar(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
  }

  it('si la confirmación falla tras crear, aterriza en el borrador guardado con el error a la vista', async () => {
    const usuario = userEvent.setup()
    await guardarUnaCompraNuevaCuyaConfirmacionFalla(usuario)

    // Aterrizó en la compra 99 (ya no es "Nueva compra") con la advertencia y el error del servidor.
    expect(await screen.findByText('Ya existe una compra confirmada con ese número.')).toBeInTheDocument()
    expect(screen.getByText('El borrador quedó guardado, pero la compra NO se confirmó.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /Compra 0003-00012345/ })).toBeInTheDocument()
    expect(screen.queryByText(/Compra confirmada/)).not.toBeInTheDocument()
  })

  it('reintentar después de una confirmación fallida actualiza el mismo borrador: no vuelve a hacer POST /compras', async () => {
    const usuario = userEvent.setup()
    await guardarUnaCompraNuevaCuyaConfirmacionFalla(usuario)
    await screen.findByRole('heading', { name: /Compra 0003-00012345/ })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/compras/99')
    // Un único alta: el reintento actualizó el borrador existente.
    expect(apiPostMock.mock.calls.filter((c: unknown[]) => c[0] === '/compras')).toHaveLength(1)
    expect(apiPostMock.mock.calls.filter((c: unknown[]) => c[0] === '/compras/99/confirmar')).toHaveLength(2)
  })

  it('con una compra nueva que viene de un gasto, Guardar la confirma y la vincula al gasto de origen', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/gastos/administracion/5') return Promise.resolve(gastoFixture({ idProveedor: 1 }))
      if (ruta === '/compras/99') return Promise.resolve(compraFixture({ id: 99, estado: 'Confirmada', items: [] }))
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      if (ruta === '/compras/99/confirmar') return Promise.resolve(compraFixture({ id: 99, estado: 'Confirmada', items: [] }))
      if (ruta === '/gastos/5/vincular-compra') return Promise.resolve(compraFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditorEnRuta('/compras/nueva?desdeGasto=5')
    await screen.findByText('Se vinculará al gasto #5 al confirmar.')
    await waitFor(() => expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1'))
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeEnabled())
    await cargarConcepto(usuario, '1500', 'Mercadería')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Vinculada al gasto #5.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/gastos/5/vincular-compra', { idComprobanteCompra: 99 })
  })
})

describe('CompraEditor — cancelar', () => {
  it('sin cambios vuelve al listado sin preguntar', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByText('Listado de compras')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('una compra nueva sin nada cargado también vuelve directo', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeEnabled())
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByText('Listado de compras')).toBeInTheDocument()
  })

  it('con cambios sin guardar pide confirmación; Cancelar de la puerta se queda, Descartar vuelve al listado', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.type(screen.getByLabelText('Observaciones'), 'cambio')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    const puerta = await screen.findByRole('alertdialog', { name: 'Descartar cambios' })
    expect(within(puerta).getByText(/Descartar los cambios sin guardar de esta compra/)).toBeInTheDocument()
    // La puerta deja inerte el resto de la pantalla.
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeDisabled()

    await usuario.click(within(puerta).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Observaciones')).toHaveValue('cambio')
    expect(screen.queryByText('Listado de compras')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Descartar cambios' }))
    expect(await screen.findByText('Listado de compras')).toBeInTheDocument()
  })

  it('un cambio en la grilla también cuenta como cambio sin guardar', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.type(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/), '5')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByRole('alertdialog', { name: 'Descartar cambios' })).toBeInTheDocument()
  })

  it('después de guardar el borrador ya no hay cambios sin guardar', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture({ observaciones: 'cambio' }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await usuario.type(screen.getByLabelText('Observaciones'), 'cambio')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await screen.findByText('Borrador guardado.')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByText('Listado de compras')).toBeInTheDocument()
  })

  it('una compra nueva con algo tipeado pregunta antes de descartar', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeEnabled())
    await usuario.type(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/), 'Flete')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByRole('alertdialog', { name: 'Descartar cambios' })).toBeInTheDocument()
  })
})

describe('CompraEditor — selectores del encabezado', () => {
  it('el proveedor se rotula con el nombre de fantasía y cae a la razón social', async () => {
    mockearReferencia(undefined, [tipoFixture()], [puntoVentaFixture()], [
      proveedorFixture({ id: 1, razonSocial: 'Proveedor Uno SA', nombreFantasia: 'Uno' }),
      proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA', nombreFantasia: null }),
    ])
    renderEditor('nueva')

    expect(await screen.findByRole('option', { name: 'Uno' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Proveedor Dos SA' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Proveedor Uno SA' })).not.toBeInTheDocument()
  })

  it('el tipo de comprobante se rotula con el nombre, no con el código', async () => {
    mockearReferencia()
    renderEditor('nueva')

    expect(await screen.findByRole('option', { name: 'Factura A de compra' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'C-FA' })).not.toBeInTheDocument()
  })

  it('con un único punto de venta lo preselecciona en una compra nueva', async () => {
    mockearReferencia()
    renderEditor('nueva')

    const puntoVenta = await screen.findByLabelText('Punto de venta')
    await waitFor(() => expect(puntoVenta).toHaveValue('2'))
  })

  it('con varios puntos de venta no preselecciona ninguno', async () => {
    mockearReferencia(undefined, [tipoFixture()], [puntoVentaFixture(), puntoVentaFixture({ id: 3, nombre: 'Sucursal' })])
    renderEditor('nueva')

    const puntoVenta = await screen.findByLabelText('Punto de venta')
    await screen.findByRole('option', { name: 'Sucursal' })
    expect(puntoVenta).toHaveValue('')
  })

  it('un borrador existente conserva su punto de venta aunque la lista tenga uno solo y distinto', async () => {
    mockearReferencia(
      (ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ idPuntoVenta: 2 })) : undefined),
      [tipoFixture()],
      [puntoVentaFixture({ id: 3, nombre: 'Sucursal' })],
    )
    apiPutMock.mockResolvedValue(compraFixture({ idPuntoVenta: 2 }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await screen.findByRole('option', { name: 'Sucursal' })
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect((apiPutMock.mock.calls[0] as [string, Record<string, unknown>])[1].idPuntoVenta).toBe(2)
  })
})

describe('CompraEditor — número de comprobante', () => {
  it('rellena con ceros al salir de cada campo y manda PPPP-NNNNNNNN', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/compras' ? Promise.resolve(compraFixture({ id: 99 })) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    const usuario = userEvent.setup()

    renderEditor('nueva')
    const proveedor = await screen.findByLabelText('Proveedor')
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await waitFor(() => expect(proveedor).toBeEnabled())
    await usuario.selectOptions(proveedor, '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    await usuario.type(screen.getByLabelText('Punto de venta del comprobante'), '10')
    await usuario.type(screen.getByLabelText('Número del comprobante'), '9985')
    await usuario.tab()

    expect(screen.getByLabelText('Punto de venta del comprobante')).toHaveValue('0010')
    expect(screen.getByLabelText('Número del comprobante')).toHaveValue('00009985')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect((apiPostMock.mock.calls[0] as [string, Record<string, unknown>])[1].numeroExterno).toBe('0010-00009985')
  })

  it('solo admite dígitos y respeta el largo máximo de cada campo', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await usuario.type(screen.getByLabelText('Punto de venta del comprobante'), 'a1b2c3d4e5')
    await usuario.type(screen.getByLabelText('Número del comprobante'), '1x2345678901')

    expect(screen.getByLabelText('Punto de venta del comprobante')).toHaveValue('1234')
    expect(screen.getByLabelText('Número del comprobante')).toHaveValue('12345678')
  })

  it('sin ninguna de las dos partes manda numeroExterno null', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/compras' ? Promise.resolve(compraFixture({ id: 99 })) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    const usuario = userEvent.setup()

    renderEditor('nueva')
    const proveedor = await screen.findByLabelText('Proveedor')
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await waitFor(() => expect(proveedor).toBeEnabled())
    await usuario.selectOptions(proveedor, '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect((apiPostMock.mock.calls[0] as [string, Record<string, unknown>])[1].numeroExterno).toBeNull()
  })

  it('con solo el punto de venta cargado, al salir del grupo marca la parte que falta y no manda nada al crear', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    const proveedor = await screen.findByLabelText('Proveedor')
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await waitFor(() => expect(proveedor).toBeEnabled())
    await usuario.selectOptions(proveedor, '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    const puntoVenta = screen.getByLabelText('Punto de venta del comprobante')
    const numero = screen.getByLabelText('Número del comprobante')
    await usuario.type(puntoVenta, '10')
    await usuario.tab()
    expect(numero).toHaveFocus()
    expect(numero).not.toHaveClass('is-invalid')

    await usuario.tab()
    expect(puntoVenta).toHaveValue('0010')
    expect(numero).toHaveClass('is-invalid')
    expect(puntoVenta).not.toHaveClass('is-invalid')
    expect(screen.getByText(/Falta el número\./)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText(/El número del comprobante está incompleto/)).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('con solo el número cargado marca el punto de venta y no manda nada al guardar un borrador existente', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    const puntoVenta = await screen.findByDisplayValue('0003')
    await usuario.clear(puntoVenta)
    await usuario.tab()
    await usuario.tab()

    expect(screen.getByLabelText('Número del comprobante')).toHaveValue('00012345')
    expect(puntoVenta).toHaveClass('is-invalid')
    expect(screen.getByText(/Falta el punto de venta\./)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText(/El número del comprobante está incompleto/)).toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('Guardar se rechaza con el número a medio cargar y no manda ni el guardado ni la confirmación', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    await usuario.clear(await screen.findByDisplayValue('00012345'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText(/El número del comprobante está incompleto/)).toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('pegar un número completo en cualquiera de los dos campos rellena ambas partes', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    const puntoVenta = screen.getByLabelText('Punto de venta del comprobante')
    const numero = screen.getByLabelText('Número del comprobante')

    await usuario.click(puntoVenta)
    await usuario.paste('0010-00009985')
    expect(puntoVenta).toHaveValue('0010')
    expect(numero).toHaveValue('00009985')

    await usuario.clear(puntoVenta)
    await usuario.clear(numero)
    await usuario.click(numero)
    await usuario.paste('10-9985')
    expect(puntoVenta).toHaveValue('0010')
    expect(numero).toHaveValue('00009985')

    await usuario.clear(puntoVenta)
    await usuario.clear(numero)
    await usuario.click(puntoVenta)
    await usuario.paste('001000009985')
    expect(puntoVenta).toHaveValue('0010')
    expect(numero).toHaveValue('00009985')
  })

  it('un número anterior al formato no se pierde ni se altera si no se edita', async () => {
    mockearReferencia((ruta) =>
      ruta === '/compras/1' ? Promise.resolve(compraFixture({ numeroExterno: 'A-0001-00000012' })) : undefined,
    )
    apiPutMock.mockResolvedValue(compraFixture({ numeroExterno: 'A-0001-00000012' }))
    const usuario = userEvent.setup()

    renderEditor()
    expect(await screen.findByText('Número original: A-0001-00000012')).toBeInTheDocument()
    const numero = screen.getByLabelText('Número del comprobante')
    await usuario.click(numero)
    await usuario.tab()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect((apiPutMock.mock.calls[0] as [string, Record<string, unknown>])[1].numeroExterno).toBe('A-0001-00000012')
  })
})

describe('CompraEditor — líneas incompletas', () => {
  it('una fila con datos pero incompleta no entra en el total, se cuenta y bloquea los dos guardados con un mensaje claro', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    expect(screen.getByText('$ 1.149,50')).toBeInTheDocument()

    // La fila vacía del final se llena a medias (sin detalle ni IVA): queda incompleta a propósito,
    // pero con cantidad e importe para que, si el mirror la sumara, el total cambiaría.
    await cargarFila(usuario, 1, { cantidad: '5', importe: '20' })

    expect(await screen.findByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()
    expect(screen.getByText('$ 1.149,50')).toBeInTheDocument()
    // Tipear en la fila vacía abrió otra vacía debajo.
    expect(filasDeItems()).toHaveLength(3)

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    expect(
      await screen.findByText('Hay 1 línea(s) incompleta(s): complételas o quítelas antes de guardar.'),
    ).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() =>
      expect(screen.getAllByText('Hay 1 línea(s) incompleta(s): complételas o quítelas antes de guardar.')).toHaveLength(1),
    )
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(apiPostMock).not.toHaveBeenCalled()

    // Tras el intento, las celdas que faltan quedan marcadas; las que están bien, no.
    const fila = within(filaDeItems(1))
    expect(fila.getByLabelText(/^Detalle, línea \d+$/)).toHaveAttribute('aria-invalid', 'true')
    expect(fila.getByLabelText(/^IVA, línea \d+$/)).toHaveAttribute('aria-invalid', 'true')
    expect(fila.getByLabelText(/^Cantidad, línea \d+$/)).not.toHaveAttribute('aria-invalid')
    expect(fila.getByLabelText(/^Importe, línea \d+$/)).not.toHaveAttribute('aria-invalid')
  })

  it('quitar la fila incompleta destraba el guardado', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await cargarFila(usuario, 1, { cantidad: '5' })
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    expect(await screen.findByText(/Hay 1 línea\(s\) incompleta\(s\)/)).toBeInTheDocument()

    await usuario.click(within(filaDeItems(1)).getByRole('button', { name: /^Quitar línea \d+$/ }))
    expect(screen.queryByText(/línea\(s\) incompleta\(s\)/)).not.toBeInTheDocument()
    expect(filasDeItems()).toHaveLength(2)

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect((apiPutMock.mock.calls[0] as [string, { items: unknown[] }])[1].items).toHaveLength(1)
  })
})

describe('CompraEditor — líneas con control de lote (stage-12-lotes-vencimientos, Slice 14)', () => {
  function mockearLoteConBusqueda(items: ArticuloListado[], compra: CompraDetalle) {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compra)
      if (ruta.startsWith('/articulos?busqueda=')) return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
      return undefined
    })
  }

  async function elegirArticuloDeLaBusqueda(usuario: ReturnType<typeof userEvent.setup>, texto: string, opcion: string) {
    await usuario.type(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/), texto)
    await usuario.click(await screen.findByRole('option', { name: opcion }))
  }

  it('elegir un artículo que controla lote muestra la fila de lote y bloquea el guardado hasta cargar el vencimiento', async () => {
    mockearLoteConBusqueda([articuloFixture()], compraFixture({ items: [] }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await elegirArticuloDeLaBusqueda(usuario, 'leche', 'ART-20 — Leche en polvo 800g')

    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()
    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toBeInTheDocument()
    expect(await screen.findByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()

    await cargarFila(usuario, 0, { cantidad: '5', importe: '20' })

    // Sigue incompleta: el artículo controla lote y la fecha de vencimiento es obligatoria
    // (espejo del `lote_requerido` server-side) — código de lote NO es obligatorio, se deriva.
    expect(screen.getByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/), '01/12/2026')

    await waitFor(() => expect(screen.queryByText(/línea\(s\) incompleta\(s\)/)).not.toBeInTheDocument())
  })

  it('el lote queda en el orden de tabulación justo después de las celdas de su fila', async () => {
    mockearLoteConBusqueda([articuloFixture()], compraFixture({ items: [] }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await elegirArticuloDeLaBusqueda(usuario, 'leche', 'ART-20 — Leche en polvo 800g')

    await usuario.click(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/))
    await usuario.tab()
    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toHaveFocus()
    await usuario.tab()
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toHaveFocus()
    await usuario.tab()
    // el calendario del vencimiento es parte de la misma fila de lote
    expect(document.activeElement).toHaveAccessibleName('Abrir calendario')
    expect(document.activeElement?.closest('tr[data-lote-de]')).not.toBeNull()
    await usuario.tab()
    expect(within(filaDeItems(1)).getByLabelText(/^Código, línea \d+$/)).toHaveFocus()
  })

  it('un artículo que no controla lote nunca muestra los inputs de lote', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    renderEditor()

    await screen.findByDisplayValue('00012345')
    expect(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/)).toHaveValue('Fideos 500g')
    expect(screen.queryByLabelText(/^Fecha de vencimiento, línea \d+$/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/^Código de lote, línea \d+$/)).not.toBeInTheDocument()
  })

  it('re-elegir el artículo de una línea con lote cargado, por uno que no controla lote, no deja codigoLote/fechaVencimiento stale en el payload (judgment-day, MAJOR juez A)', async () => {
    const articuloSinLote = articuloFixture({ id: 30, codigoInterno: 'ART-30', nombre: 'Coca Cola 1.5L', controlaLote: false })
    mockearLoteConBusqueda(
      [articuloSinLote],
      compraFixture({ items: [itemFixture({ codigoLote: 'LOTE-A', fechaVencimiento: '2026-06-01' })] }),
    )
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toHaveValue('LOTE-A')
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toHaveValue('01/06/2026')

    await usuario.clear(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/))
    await elegirArticuloDeLaBusqueda(usuario, 'coca', 'ART-30 — Coca Cola 1.5L')

    // El artículo nuevo no controla lote: la fila de lote desaparece — el reset no es solo
    // interno, también es visible (el operador ve que el lote del artículo anterior ya no aplica).
    expect(screen.queryByLabelText(/^Código de lote, línea \d+$/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/^Fecha de vencimiento, línea \d+$/)).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    const items = cuerpo.items as Record<string, unknown>[]
    expect(items).toHaveLength(1)
    expect(items[0].idArticulo).toBe(30)
    expect(items[0].codigoLote).toBeNull()
    expect(items[0].fechaVencimiento).toBeNull()
  })

  it('re-elegir el artículo de una línea con lote cargado, por otro que también controla lote, resetea codigoLote/fechaVencimiento visualmente (el operador recarga los del lote nuevo)', async () => {
    const otroArticuloConLote = articuloFixture({ id: 40, codigoInterno: 'ART-40', nombre: 'Yerba 1kg', controlaLote: true })
    mockearLoteConBusqueda(
      [otroArticuloConLote],
      compraFixture({ items: [itemFixture({ codigoLote: 'LOTE-A', fechaVencimiento: '2026-06-01' })] }),
    )
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toHaveValue('LOTE-A')
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toHaveValue('01/06/2026')

    await usuario.clear(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/))
    await elegirArticuloDeLaBusqueda(usuario, 'yerba', 'ART-40 — Yerba 1kg')

    expect(screen.getByLabelText(/^Código de lote, línea \d+$/)).toHaveValue('')
    expect(screen.getByLabelText(/^Fecha de vencimiento, línea \d+$/)).toHaveValue('')
  })
})

describe('CompraEditor — role gating', () => {
  it('un Vendedor llega a la ruta (decisión 11) y ve el borrador de solo lectura, sin acciones de escritura', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))

    renderEditorProtegido()
    await screen.findByDisplayValue('00012345')

    // el gate de rol de la ruta lo dejó pasar — no lo mandó a "/".
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar borrador' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancelar' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Proveedor')).toBeDisabled()
    expect(screen.queryByLabelText(/^Importe, línea \d+$/)).not.toBeInTheDocument()
  })

  it.each(['Borrador', 'Confirmada'] as const)(
    'un Vendedor con una compra %s sin costos por línea ve — y conserva los totales del encabezado',
    async (estado) => {
      usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
      const sinCostos = compraFixture({
        estado,
        items: [itemFixture({ costoUnitario: null, descuento: null, total: null, precioSugerido: null })],
      })
      mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(sinCostos) : undefined))

      renderEditorProtegido()
      await screen.findByDisplayValue('00012345')

      const fila = screen.getByRole('row', { name: /Fideos 500g/ })
      // lote, vencimiento, costo unitario, descuento, total y precio sugerido.
      expect(within(fila).getAllByText('—')).toHaveLength(6)
      expect(fila.textContent).not.toMatch(/\$|NaN|0,00/)
      expect(screen.getByText('Total', { selector: 'div' }).nextElementSibling).toHaveTextContent('$ 1.149,50')
      expect(screen.getByText('Subtotal', { selector: 'div' }).nextElementSibling).toHaveTextContent('$ 1.000,00')
      expect(screen.queryByText(/incompleta/)).not.toBeInTheDocument()
    },
  )

  it('un rol fuera de la lista (Root) es redirigido a "/" antes de llegar a la pantalla', async () => {
    usuarioActual = usuarioFixture({ id: 99, usuario: 'root', mail: 'root@ways.test', rolId: ROL.Root, rol: 'Root', idTenant: null })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))

    renderEditorProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
  })
})

describe('CompraEditor — referencia fallida', () => {
  it('un fallo al cargar proveedores/tipos/alícuotas/puntos de venta muestra un aviso y bloquea el guardado', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/proveedores')) return Promise.reject(new Error('caído'))
      if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve([tipoFixture()])
      if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([alicuotaFixture()])
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta === '/catalogos/listas-precio') return Promise.resolve([listaPrecioFixture()])
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    renderEditor()

    expect(await screen.findByText(/No se pudieron cargar los proveedores\./)).toBeInTheDocument()
    await screen.findByDisplayValue('00012345')
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeDisabled()
  })
})

describe('CompraEditor — carga inicial', () => {
  it('muestra el spinner mientras el detalle está en vuelo y lo reemplaza por el formulario al resolver', async () => {
    let resolverGet: (valor: CompraDetalle) => void = () => {}
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return new Promise((resolve) => (resolverGet = resolve))
      return undefined
    })

    renderEditor()
    await screen.findByText('Cargando…')

    resolverGet(compraFixture())
    await waitFor(() => expect(screen.queryByText('Cargando…')).not.toBeInTheDocument())
    expect(await screen.findByDisplayValue('00012345')).toBeInTheDocument()
  })
})

// stage-16-ordenes-de-compra, Slice 6: pre-carga desde `?idOrdenCompra=` — "Registrar recepción"
// de OrdenDeCompra.tsx navega acá con la OC en la URL, nunca en location.state (a diferencia del
// botón de Reposicion.tsx, que sí usa state — acá el origen es un Link real de otra pantalla que
// puede recargarse en frío).
function coberturaFixture(sobrescribir: Partial<{ idArticulo: number; pedida: number; recibida: number; pendiente: number; costoEstimado: number | null; costoReal: number | null; desvio: number | null }> = {}) {
  return { idArticulo: 10, pedida: 7, recibida: 5, pendiente: 2, costoEstimado: 100, costoReal: null, desvio: null, ...sobrescribir }
}

// idProveedor/idPuntoVenta usan los mismos ids que `proveedorFixture()`/`puntoVentaFixture()` (1/2)
// — la lista de referencia mockeada por `mockearReferencia` solo trae esos dos, así que un id
// distinto nunca tendría una <option> para reflejar (no es un bug de producción, es la fixture).
function ordenFixture(sobrescribir: Partial<{ id: number; idProveedor: number; idPuntoVenta: number; cobertura: ReturnType<typeof coberturaFixture>[]; items: { orden: number; idArticulo: number; descripcion: string; cantidadPedida: number; costoUnitarioEstimado: number | null }[] }> = {}) {
  return {
    id: 30,
    idProveedor: 1,
    idPuntoVenta: 2,
    numero: 12,
    fechaEmision: '2026-08-19T12:00:00Z',
    fechaEnvio: '2026-08-19T12:00:00Z',
    fechaEsperada: null,
    fechaCierre: null,
    cierreManual: false,
    observaciones: null,
    estado: 'Enviada',
    items: [{ orden: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidadPedida: 7, costoUnitarioEstimado: 100 }],
    cobertura: [coberturaFixture()],
    totalEstimado: 700,
    totalReal: null,
    desvioTotal: null,
    comprobantesLigados: [],
    ...sobrescribir,
  }
}

describe('CompraEditor — pre-carga desde una orden de compra (?idOrdenCompra=)', () => {
  it('precarga proveedor/punto de venta/idOrdenCompra y una línea por artículo con Pendiente > 0', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.resolve(ordenFixture())
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    expect(await screen.findByText(/Vinculada a la orden de compra/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '#30' })).toHaveAttribute('href', '/ordenes-compra/30')

    // Los proveedores/puntos de venta de referencia cargan en un fetch INDEPENDIENTE del de la
    // orden a precargar — hasta que aterriza, el <select> no tiene ninguna <option value="4">
    // para reflejar; se auto-corrige apenas React monta esas opciones (sin bug de producción).
    await waitFor(() => {
      expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1')
      expect((screen.getByLabelText('Punto de venta') as HTMLSelectElement).value).toBe('2')
    })
    expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toHaveValue(2) // Pendiente, no Pedida
    expect(within(filaDeItems(0)).getByLabelText(/^Importe, línea \d+$/)).toHaveValue('100,0000') // CostoEstimado de la cobertura
    expect(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/)).toHaveValue('Yerba mate 1kg')
    expect(filasDeItems()).toHaveLength(2) // la fila precargada y la vacía del final
  })

  it('excluye artículos con Pendiente = 0 (ya recibidos por completo)', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') {
        return Promise.resolve(
          ordenFixture({ cobertura: [coberturaFixture({ idArticulo: 10, pendiente: 0 }), coberturaFixture({ idArticulo: 11, pendiente: 3 })] }),
        )
      }
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    await screen.findByText(/Vinculada a la orden de compra/)
    expect(filasDeItems()).toHaveLength(2)
    expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toHaveValue(3)
  })

  it('un artículo nunca cotizado precarga costo unitario vacío, nunca "0"', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.resolve(ordenFixture({ cobertura: [coberturaFixture({ costoEstimado: null })] }))
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    await screen.findByText(/Vinculada a la orden de compra/)
    expect(within(filaDeItems(0)).getByLabelText(/^Importe, línea \d+$/)).toHaveValue('')
  })

  it('sin ?idOrdenCompra= no dispara ningún fetch a /ordenes-compra ni muestra el aviso de vínculo', async () => {
    mockearReferencia()
    renderEditor('nueva')

    await screen.findByLabelText('Proveedor')
    expect(screen.queryByText(/Vinculada a la orden de compra/)).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c: unknown[]) => (c[0] as string).startsWith('/ordenes-compra'))).toBe(false)
  })

  it('una compra existente (no nueva) ignora ?idOrdenCompra= — solo aplica a un borrador nuevo', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture({ idOrdenCompra: 7 }))
      return undefined
    })

    renderEditorEnRuta('/compras/1?idOrdenCompra=30')

    // El vínculo mostrado viene del `idOrdenCompra` YA PERSISTIDO de la compra (7), nunca del
    // query param (30) — una compra existente no se re-precarga.
    expect(await screen.findByRole('link', { name: '#7' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: '#30' })).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c: unknown[]) => (c[0] as string) === '/ordenes-compra/30')).toBe(false)
  })

  it('un fallo al cargar la orden de compra muestra un aviso, sin bloquear el resto de la pantalla', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe la orden de compra 30.'))
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    expect(await screen.findByText('No existe la orden de compra 30.')).toBeInTheDocument()
    expect(await screen.findByLabelText('Proveedor')).toBeInTheDocument()
  })
})

// stage-gasto-a-compra (PR4): pre-carga desde `?desdeGasto=` — "Crear compra" de Gastos.tsx navega
// acá con el gasto en la URL; a diferencia de `?idOrdenCompra=`, el vínculo real se dispara recién
// al CONFIRMAR (nunca al crear el borrador), así que el id sobrevive el remontaje nuevo→existente.
function gastoFixture(sobrescribir: Partial<GastoDeAdministracionListado> = {}): GastoDeAdministracionListado {
  return {
    id: 5,
    // Mediodía UTC, no medianoche: evita que el offset local (este runner corre en
    // America/Buenos_Aires, UTC-3) corra el día calendario un día para atrás.
    fecha: '2026-08-15T12:00:00Z',
    idEmpresa: 1,
    idPuntoVenta: 2,
    idTurnoCaja: null,
    categoria: 'Otros',
    idProveedor: null,
    nombreProveedor: null,
    idArea: null,
    nombreArea: null,
    concepto: 'Pago de mercadería',
    detalle: null,
    idMedioPago: 1,
    nombreMedioPago: 'Efectivo',
    numeroFactura: '0003-00099999',
    importe: 1500,
    origenFondos: 'Tesoreria',
    idComprobanteCompra: null,
    turnoAbierto: false,
    ...sobrescribir,
  }
}

describe('CompraEditor — pre-carga desde un gasto (?desdeGasto=)', () => {
  it('precarga proveedor/PV/fecha/número/observaciones desde el gasto y muestra el banner de vínculo pendiente', async () => {
    mockearReferencia((ruta) => (ruta === '/gastos/administracion/5' ? Promise.resolve(gastoFixture({ idProveedor: 1 })) : undefined))

    renderEditorEnRuta('/compras/nueva?desdeGasto=5')

    expect(await screen.findByText('Se vinculará al gasto #5 al confirmar.')).toBeInTheDocument()
    await waitFor(() => {
      expect((screen.getByLabelText('Proveedor') as HTMLSelectElement).value).toBe('1')
      expect((screen.getByLabelText('Punto de venta') as HTMLSelectElement).value).toBe('2')
    })
    expect(screen.getByLabelText('Fecha del comprobante')).toHaveValue('15/08/2026')
    expect(screen.getByLabelText('Punto de venta del comprobante')).toHaveValue('0003')
    expect(screen.getByLabelText('Número del comprobante')).toHaveValue('00099999')
    expect(screen.getByLabelText('Observaciones')).toHaveValue('Pago de mercadería')
  })

  it('sin ?desdeGasto= no dispara ningún fetch a /gastos/administracion ni muestra el banner', async () => {
    mockearReferencia()
    renderEditor('nueva')

    await screen.findByLabelText('Proveedor')
    expect(screen.queryByText(/Se vinculará al gasto/)).not.toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((c: unknown[]) => (c[0] as string).startsWith('/gastos/administracion'))).toBe(false)
  })

  it('al confirmar, vincula automáticamente al gasto de origen', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/gastos/administracion/5') return Promise.resolve(gastoFixture())
      if (ruta === '/compras/1') return Promise.resolve(compraFixture({ idProveedor: 1 }))
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/confirmar') return Promise.resolve(compraFixture({ estado: 'Confirmada' }))
      if (ruta === '/gastos/5/vincular-compra') return Promise.resolve(compraFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    apiPutMock.mockResolvedValue(compraFixture({ idProveedor: 1 }))
    const usuario = userEvent.setup()

    renderEditorEnRuta('/compras/1?desdeGasto=5')
    await screen.findByText('Se vinculará al gasto #5 al confirmar.')

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos/5/vincular-compra', { idComprobanteCompra: 1 }))
    expect(await screen.findByText('Vinculada al gasto #5.')).toBeInTheDocument()
  })

  it('si el vínculo automático falla, la compra queda confirmada y se puede reintentar', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/gastos/administracion/5') return Promise.resolve(gastoFixture())
      if (ruta === '/compras/1') return Promise.resolve(compraFixture({ idProveedor: 1 }))
      return undefined
    })
    let intentos = 0
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/confirmar') return Promise.resolve(compraFixture({ estado: 'Confirmada' }))
      if (ruta === '/gastos/5/vincular-compra') {
        intentos += 1
        return intentos === 1
          ? Promise.reject(new ErrorApi(409, 'gasto_ya_vinculado', 'El gasto ya está vinculado a una compra.'))
          : Promise.resolve(compraFixture())
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    apiPutMock.mockResolvedValue(compraFixture({ idProveedor: 1 }))
    const usuario = userEvent.setup()

    renderEditorEnRuta('/compras/1?desdeGasto=5')
    await screen.findByText('Se vinculará al gasto #5 al confirmar.')

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    // La compra queda confirmada IGUAL — el fallo del vínculo nunca la revierte.
    expect(await screen.findByText('Compra confirmada: el stock y el costo ya se actualizaron.')).toBeInTheDocument()
    expect(await screen.findByText('El gasto ya está vinculado a una compra.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Reintentar vínculo' }))

    await waitFor(() => expect(intentos).toBe(2))
    expect(await screen.findByText('Vinculada al gasto #5.')).toBeInTheDocument()
  })
})

describe('CompraEditor — líneas por concepto', () => {
  async function prepararCompraNueva(usuario: ReturnType<typeof userEvent.setup>) {
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
  }

  it('un Detalle libre con cantidad, importe e IVA se manda como concepto: sin artículo, sin bultos ni lote y sin actualizar costo', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await cargarConcepto(usuario, '1234,56')

    const fila = within(filaDeItems(0))
    expect(fila.getByLabelText(/^Cantidad, línea \d+$/)).toHaveValue(1)
    expect(fila.getByLabelText(/^Importe, línea \d+$/)).toHaveValue('1.234,5600')
    // un concepto es siempre por unidad y no ofrece crear un artículo ya resuelto
    expect(fila.getByLabelText(/^UM, línea \d+$/)).toBeDisabled()
    expect(fila.getByLabelText(/^UM, línea \d+$/)).toHaveValue('Unidad')
    // el mirror suma el concepto: 1234,56 + 21% de IVA
    expect(screen.getAllByText('$ 1.493,82').length).toBeGreaterThan(0)

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.items).toEqual([
      {
        idArticulo: null,
        descripcion: 'Total del comprobante',
        unidades: 1,
        bultos: null,
        unidadesPorBulto: null,
        costoUnitario: 1234.56,
        descuento: 0,
        idAlicuotaIva: 3,
        actualizaCosto: false,
        codigoLote: null,
        fechaVencimiento: null,
        codigoProveedor: null,
      },
    ])
  })

  it('un concepto incompleto (sin cantidad ni IVA) no se guarda hasta tenerlos', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await cargarFila(usuario, 0, { detalle: 'Flete', importe: '500' })
    expect(screen.getByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()

    await cargarFila(usuario, 0, { cantidad: '1' })
    expect(screen.getByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()

    await cargarFila(usuario, 0, { iva: '3' })
    await waitFor(() => expect(screen.queryByText(/línea\(s\) incompleta\(s\)/)).not.toBeInTheDocument())
  })

  it('un borrador con una línea por concepto la reabre como concepto y el PUT la devuelve sin artículo', async () => {
    const concepto = itemFixture({
      orden: 2, idArticulo: null, descripcion: 'Flete', cantidad: 1, costoUnitario: 500, descuento: 0, total: 500,
      actualizaCosto: false, precioSugerido: null,
    })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ items: [itemFixture(), concepto] })) : undefined))
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')

    expect(within(filaDeItems(1)).getByLabelText(/^Detalle, línea \d+$/)).toHaveValue('Flete')
    // el concepto no ofrece bultos; el botón de crear artículo solo se ofrece en filas sin artículo
    expect(within(filaDeItems(1)).getByLabelText(/^UM, línea \d+$/)).toBeDisabled()
    expect(within(filaDeItems(0)).queryByRole('button', { name: /^Crear artículo, línea \d+$/ })).not.toBeInTheDocument()
    expect(within(filaDeItems(1)).getByRole('button', { name: /^Crear artículo, línea \d+$/ })).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, { items: Record<string, unknown>[] }]
    expect(cuerpo.items.map((i) => i.idArticulo)).toEqual([10, null])
    expect(cuerpo.items[1]).toMatchObject({ descripcion: 'Flete', unidades: 1, costoUnitario: 500, actualizaCosto: false })
  })

  it('una compra confirmada muestra el concepto con su etiqueta, sin precio sugerido', async () => {
    const concepto = itemFixture({
      orden: 2, idArticulo: null, descripcion: 'Flete', cantidad: 1, costoUnitario: 500, descuento: 0, total: 500,
      actualizaCosto: false, precioSugerido: null,
    })
    mockearReferencia((ruta) =>
      ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada', items: [concepto] })) : undefined,
    )
    renderEditor()

    const celda = (await screen.findByText('Flete')).closest('td') as HTMLElement
    expect(within(celda).getByText('Concepto')).toBeInTheDocument()
    expect(screen.queryByLabelText(/^Importe, línea \d+$/)).not.toBeInTheDocument()
  })

  it('Guardar una compra solo de conceptos no promete mover stock ni costo, y la confirma', async () => {
    const concepto = itemFixture({
      orden: 1, idArticulo: null, descripcion: 'Ferretería', cantidad: 1, costoUnitario: 500, descuento: 0, total: 500,
      actualizaCosto: false, precioSugerido: null,
    })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ items: [concepto] })) : undefined))
    apiPutMock.mockResolvedValue(compraFixture({ items: [concepto] }))
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/compras/1/confirmar'
        ? Promise.resolve(compraFixture({ estado: 'Confirmada', items: [concepto] }))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('Ferretería')

    const aviso = screen.getByText(/Guardar confirma la compra y es irreversible/)
    expect(aviso).toHaveTextContent('no se puede volver a borrador')
    expect(aviso).not.toHaveTextContent('stock')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Compra confirmada.')).toBeInTheDocument()
  })

  it('con artículos, el aviso de irreversibilidad junto al botón dice que el stock entra y el costo se actualiza', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    renderEditor()
    await screen.findByDisplayValue('00012345')

    expect(screen.getByText(/Guardar confirma la compra y es irreversible/)).toHaveTextContent(
      'el stock entra, el costo del artículo se actualiza y no se puede volver a borrador',
    )
  })
})

describe('CompraEditor — remito y desglose de IVA', () => {
  const tipoFB = () => tipoFixture({ id: 6, codigo: 'C-FB', nombre: 'Factura B de compra', letra: 'B', discriminaIva: false })
  const tipoRM = () =>
    tipoFixture({
      id: 7,
      codigo: 'C-RM',
      nombre: 'Remito / comprobante no fiscal',
      letra: 'X',
      discriminaIva: false,
      registraLibroIva: false,
    })

  async function prepararCompraNueva(usuario: ReturnType<typeof userEvent.setup>, idTipo: string) {
    mockearReferencia(undefined, [tipoFixture(), tipoFB(), tipoRM()])
    renderEditor('nueva')
    // Esperar el DATO (las opciones cargadas), no el select que se renderiza antes del fetch.
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await screen.findByRole('option', { name: /Remito \/ comprobante no fiscal/ })
    await screen.findByRole('option', { name: 'Casa Central' })
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), idTipo)
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)).toBeEnabled())
  }

  it('el remito ofrece el tilde Discrimina IVA y arranca apagado; una factura no lo ofrece', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')

    expect(screen.getByLabelText('Discrimina IVA')).not.toBeChecked()

    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    expect(screen.queryByLabelText('Discrimina IVA')).not.toBeInTheDocument()
  })

  it('un remito sin tocar el tilde no muestra desglose y manda el valor del tipo, sin IVA impreso', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')

    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.idTipoComprobante).toBe(7)
    expect(cuerpo.discriminaIva).toBeNull()
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('tildar Discrimina IVA en un remito muestra el desglose por alícuota y lo manda como discriminaIva true', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')

    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const tabla = screen.getByRole('table', { name: 'Desglose de IVA' })
    const fila = within(tabla).getByText('21%').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(fila).getByLabelText('IVA impreso 21%')).toHaveValue('210,00')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBe(true)
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('corregir el IVA de una alícuota recalcula el total y manda el importe impreso', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210,5')
    await usuario.tab()

    expect(screen.getByText('Calculado: $ 210,00')).toBeInTheDocument()
    expect(screen.getByText('$ 1.210,50')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.ivaImpreso).toEqual([{ idAlicuotaIva: 3, iva: 210.5 }])
  })

  it('un IVA impreso que se pasa de un peso del calculado avisa antes de guardar', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '215')
    await usuario.tab()

    expect(screen.getByText(/Difiere más de \$ 1,00 del calculado/)).toBeInTheDocument()
  })

  it('tipear el mismo IVA que sale del neto no cuenta como override', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210')
    await usuario.tab()
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('cambiar de tipo descarta el IVA impreso y una factura A siempre discrimina', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210,5')
    await usuario.tab()

    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(screen.getByLabelText('IVA impreso 21%')).toHaveValue('210,00')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBeNull()
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('una factura B no discrimina IVA: ni tilde ni desglose', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '6')
    await cargarConcepto(usuario, '1000')

    expect(screen.queryByLabelText('Discrimina IVA')).not.toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()
  })

  it('un borrador de remito que discrimina se reabre con el tilde puesto y el IVA guardado, y el PUT lo conserva', async () => {
    const remito = compraFixture({
      idTipoComprobante: 7,
      discriminaIva: true,
      ivaTotal: 200,
      total: 1150,
      alicuotas: [{ idAlicuotaIva: 3, porcentaje: 21, neto: 950, iva: 200 }],
    })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(remito) : undefined), [tipoFixture(), tipoRM()])
    apiPutMock.mockResolvedValue(remito)
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('00012345')
    await waitFor(() => expect(screen.getByLabelText('Discrimina IVA')).toBeChecked())

    // El IVA guardado (200) difiere del que sale del neto (199,50): es un override y se conserva.
    expect(screen.getByLabelText('IVA impreso 21%')).toHaveValue('200,00')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBe(true)
    expect(cuerpo.ivaImpreso).toEqual([{ idAlicuotaIva: 3, iva: 200 }])
  })

  it('destildar Discrimina IVA después de tildarlo lo manda como false explícito y oculta el desglose', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConcepto(usuario, '1000')

    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    expect(screen.getByRole('table', { name: 'Desglose de IVA' })).toBeInTheDocument()
    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBe(false)
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('pasar de una factura A al remito no hereda el IVA discriminado de la factura', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '5')
    await cargarConcepto(usuario, '1000')
    expect(screen.getByRole('table', { name: 'Desglose de IVA' })).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Tipo'), '7')

    expect(screen.getByLabelText('Discrimina IVA')).not.toBeChecked()
    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()
  })

  it('una compra confirmada muestra el desglose de IVA de solo lectura', async () => {
    mockearReferencia((ruta) =>
      ruta === '/compras/1'
        ? Promise.resolve(
            compraFixture({
              estado: 'Confirmada',
              alicuotas: [
                { idAlicuotaIva: 3, porcentaje: 21, neto: 950, iva: 199.5 },
                { idAlicuotaIva: 4, porcentaje: 0, neto: 50, iva: 0 },
              ],
            }),
          )
        : undefined,
    )
    renderEditor()

    const tabla = await screen.findByRole('table', { name: 'Desglose de IVA' })
    expect(within(tabla).getByText('21%')).toBeInTheDocument()
    expect(within(tabla).getByText('$ 199,50')).toBeInTheDocument()
    expect(within(tabla).queryByLabelText(/IVA impreso/)).not.toBeInTheDocument()
  })
})

describe('CompraEditor — pagar una compra confirmada', () => {
  const medioEfectivo = {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
  }

  function mockearCompra(compra: CompraDetalle) {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compra)
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo])
      return undefined
    })
  }

  it('una confirmada con saldo muestra lo pagado, el saldo pendiente y el botón Pagar', async () => {
    mockearCompra(compraFixture({ estado: 'Confirmada', pagado: 400, saldoPendiente: 749.5 }))
    renderEditor()

    await screen.findByRole('button', { name: 'Pagar' })
    expect(screen.getByText('Pagado').nextElementSibling).toHaveTextContent('$ 400,00')
    expect(screen.getByText('Saldo pendiente').nextElementSibling).toHaveTextContent('$ 749,50')
  })

  it('una confirmada saldada muestra el saldo en cero y no ofrece pagar', async () => {
    mockearCompra(compraFixture({ estado: 'Confirmada', pagado: 1149.5, saldoPendiente: 0 }))
    renderEditor()

    await screen.findByRole('button', { name: 'Anular compra' })
    expect(screen.getByText('Saldo pendiente').nextElementSibling).toHaveTextContent('$ 0,00')
    expect(screen.queryByRole('button', { name: 'Pagar' })).not.toBeInTheDocument()
  })

  it('una compra anulada no ofrece pagar ni muestra el bloque de saldo', async () => {
    mockearCompra(compraFixture({ estado: 'Anulada', pagado: 0, saldoPendiente: 0 }))
    renderEditor()

    await screen.findByText('Fideos 500g')
    expect(screen.queryByRole('button', { name: 'Pagar' })).not.toBeInTheDocument()
    expect(screen.queryByText('Saldo pendiente')).not.toBeInTheDocument()
  })

  it('un rol sin escritura no ve el botón Pagar', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Supervisor, rol: 'Supervisor' })
    mockearCompra(compraFixture({ estado: 'Confirmada', pagado: 0, saldoPendiente: 1149.5 }))
    renderEditor()

    await screen.findByText('Fideos 500g')
    expect(screen.queryByRole('button', { name: 'Pagar' })).not.toBeInTheDocument()
  })

  it('pagar manda el POST de la compra, cierra el modal, avisa y actualiza el saldo sin recargar', async () => {
    mockearCompra(compraFixture({ estado: 'Confirmada', pagado: 0, saldoPendiente: 1149.5 }))
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/pagos') {
        return Promise.resolve({
          gasto: { id: 50, importe: 1149.5 },
          pagado: 1149.5,
          saldoPendiente: 0,
        })
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await usuario.click(await screen.findByRole('button', { name: 'Pagar' }))
    await screen.findByRole('option', { name: 'Efectivo' })
    await usuario.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(await screen.findByText('Pago registrado: $ 1.149,50.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Saldo pendiente').nextElementSibling).toHaveTextContent('$ 0,00')
    expect(screen.queryByRole('button', { name: 'Pagar' })).not.toBeInTheDocument()

    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1/pagos')
    expect(llamadas).toHaveLength(1)
    expect(llamadas[0][1]).toMatchObject({ importe: 1149.5, idMedioPago: 1, concepto: null })
    // El detalle se pidió una sola vez: el pago no dispara una segunda lectura.
    expect(apiGetMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1')).toHaveLength(1)
  })
})

describe('CompraEditor — cantidad según la unidad de venta del artículo', () => {
  it('un borrador reabierto resuelve la unidad: por unidad pide enteros, marca una fracción y la fila queda incompleta', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ unidadVenta: 'Unidad' }))
      return undefined
    })
    renderEditor()
    await screen.findByDisplayValue('00012345')

    const cantidad = within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)
    await waitFor(() => expect(cantidad).toHaveAttribute('step', '1'))
    expect(cantidad).toHaveAttribute('min', '1')

    fireEvent.change(cantidad, { target: { value: '2.5' } })

    expect(cantidad).toHaveClass('is-invalid')
    expect(screen.getByText('Este artículo se vende por unidad: la cantidad tiene que ser entera.')).toBeInTheDocument()
    expect(screen.getByText('1 línea(s) incompleta(s) — complételas o quítelas para poder guardar.')).toBeInTheDocument()
  })

  it('elegir un artículo por unidad en una fila nueva fija el paso en 1, sin consultar el artículo otra vez', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture({ items: [] }))
      if (ruta.startsWith('/articulos?busqueda=')) {
        return Promise.resolve({ items: [articuloFixture({ unidadVenta: 'Unidad' })], total: 1, pagina: 1, tamanio: 25 })
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderEditor()
    await screen.findByDisplayValue('00012345')

    await usuario.type(within(filaDeItems(0)).getByLabelText(/^Detalle, línea \d+$/), 'leche')
    await usuario.click(await screen.findByRole('option', { name: 'ART-20 — Leche en polvo 800g' }))

    const cantidad = within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)
    expect(cantidad).toHaveAttribute('step', '1')
    fireEvent.change(cantidad, { target: { value: '2.5' } })
    expect(cantidad).toHaveClass('is-invalid')
    await act(async () => {})
    expect(apiGetMock).not.toHaveBeenCalledWith('/articulos/20')
  })

  it('un artículo por peso, o uno cuya unidad no se pudo resolver, conserva el paso de 0.001', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ unidadVenta: 'Peso' }))
      return undefined
    })
    renderEditor()
    await screen.findByDisplayValue('00012345')

    const cantidad = within(filaDeItems(0)).getByLabelText(/^Cantidad, línea \d+$/)
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/articulos/10'))
    fireEvent.change(cantidad, { target: { value: '2.5' } })

    expect(cantidad).toHaveAttribute('step', '0.001')
    expect(cantidad).not.toHaveClass('is-invalid')
  })

  it('un borrador reabierto habilita Bulto con las unidades por bulto del artículo, en la misma consulta de la unidad', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ unidadVenta: 'Unidad', unidadesPorBulto: 12 }))
      return undefined
    })
    const usuario = userEvent.setup()
    renderEditor()
    await screen.findByDisplayValue('00012345')

    const um = within(filaDeItems(0)).getByLabelText(/^UM, línea \d+$/)
    await waitFor(() => expect(um).toBeEnabled())
    await usuario.selectOptions(um, 'Bulto')
    expect(within(filaDeItems(0)).getByText('= 120 un. (12 por bulto)')).toBeInTheDocument()
    expect(apiGetMock.mock.calls.filter((c: unknown[]) => c[0] === '/articulos/10')).toHaveLength(1)
  })

  it('una fila con UM Bulto manda bultos, unidades por bulto y el costo por unidad', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ unidadVenta: 'Unidad', unidadesPorBulto: 12, controlaLote: false }))
      return undefined
    })
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()
    renderEditor()
    await screen.findByDisplayValue('00012345')

    const fila = within(filaDeItems(0))
    await waitFor(() => expect(fila.getByLabelText(/^UM, línea \d+$/)).toBeEnabled())
    await usuario.selectOptions(fila.getByLabelText(/^UM, línea \d+$/), 'Bulto')
    await usuario.clear(fila.getByLabelText(/^Cantidad, línea \d+$/))
    await usuario.type(fila.getByLabelText(/^Cantidad, línea \d+$/), '2')
    await usuario.clear(fila.getByLabelText(/^Importe, línea \d+$/))
    await usuario.type(fila.getByLabelText(/^Importe, línea \d+$/), '120')
    // 2 bultos × $120 = $240 bruto; 5 % de descuento = $12; total $228
    expect(fila.getByText('$ 228,00')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPutMock.mock.calls[0] as [string, { items: Record<string, unknown>[] }]
    expect(cuerpo.items[0]).toMatchObject({ unidades: 0, bultos: 2, unidadesPorBulto: 12, costoUnitario: 10, descuento: 12 })
  })
})

describe('CompraEditor — guardar espera a las consultas de código', () => {
  it('Guardar y Guardar borrador no guardan mientras un código del proveedor se está consultando: avisan, y al terminar se puede guardar', async () => {
    let resolver: (r: unknown) => void = () => {}
    mockearReferencia((ruta) =>
      ruta.startsWith('/articulos?busqueda=AB-1') ? new Promise((r) => (resolver = r)) : undefined,
    )
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await cargarFila(usuario, 0, { detalle: 'Flete', cantidad: '1', importe: '500', iva: '3' })
    await usuario.type(within(filaDeItems(0)).getByLabelText(/^Código, línea \d+$/), 'AB-1')
    await usuario.tab()
    await within(filaDeItems(0)).findByText('Buscando…')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByText('Se están buscando códigos de proveedor: espere a que terminen y vuelva a guardar.')).toBeInTheDocument()

    await act(async () => {
      resolver({ items: [], total: 0, pagina: 1, tamanio: 25 })
    })
    await waitFor(() =>
      expect(screen.queryByText('Se están buscando códigos de proveedor: espere a que terminen y vuelva a guardar.')).not.toBeInTheDocument(),
    )

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, { items: Record<string, unknown>[] }]
    expect(cuerpo.items[0]).toMatchObject({ idArticulo: null, descripcion: 'Flete', codigoProveedor: 'AB-1' })
  })
})

describe('CompraEditor — cambiar el proveedor con líneas resueltas por código', () => {
  const pagina = (items: ArticuloListado[]) => Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
  const DEL_UNO = articuloFixture({ id: 20, nombre: 'Del uno', codigoProveedor: 'AB-1', costoLista: 242, descuentoProveedor: 10, idAlicuotaIva: 3 })
  const DEL_DOS = articuloFixture({ id: 51, nombre: 'Del dos', codigoProveedor: 'AB-1', costoLista: 363, descuentoProveedor: 20, idAlicuotaIva: 3 })
  const OTRO_DEL_UNO = articuloFixture({ id: 21, nombre: 'Otro del uno', codigoProveedor: 'CD-2', costoLista: 60.5, idAlicuotaIva: 3 })

  let porBusqueda: Record<string, () => Promise<unknown>> = {}

  const celda = (fila: number, etiqueta: string) => within(filaDeItems(fila)).getByLabelText(new RegExp(`^${etiqueta.replace(/[()]/g, '\\$&')}, línea \\d+$`))
  const busquedasDelProveedorDos = () =>
    apiGetMock.mock.calls.map((c: unknown[]) => String(c[0])).filter((r) => r.startsWith('/articulos?busqueda=') && r.includes('idProveedor=2'))

  async function montarConDosProveedores(
    usuario: ReturnType<typeof userEvent.setup>,
    proveedores = [proveedorFixture(), proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA' })],
  ) {
    mockearReferencia(
      (ruta) => {
        const m = /^\/articulos\?busqueda=([^&]+)&idProveedor=(\d+)/.exec(ruta)
        if (!m) return undefined
        return (porBusqueda[`${decodeURIComponent(m[1])}@${m[2]}`] ?? (() => pagina([])))()
      },
      undefined,
      undefined,
      proveedores,
    )
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await screen.findByRole('option', { name: 'Proveedor Dos SA' })
  }

  async function resolverPorCodigo(usuario: ReturnType<typeof userEvent.setup>, fila: number, codigo: string, detalle: string) {
    await usuario.type(celda(fila, 'Código'), codigo)
    await usuario.tab()
    await waitFor(() => expect(celda(fila, 'Detalle')).toHaveValue(detalle))
  }

  beforeEach(() => {
    porBusqueda = {
      'AB-1@1': () => pagina([DEL_UNO]),
      'CD-2@1': () => pagina([OTRO_DEL_UNO]),
      'AB-1@2': () => pagina([DEL_DOS]),
    }
  })

  it('con una línea resuelta por código pregunta antes de cambiar, en singular; confirmar vuelve a buscarla contra el proveedor nuevo y borra sus precios', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    await usuario.type(celda(0, 'Cantidad'), '3')
    expect(celda(0, 'Importe')).toHaveValue('200,0000')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    const puerta = await screen.findByRole('alertdialog')
    expect(within(puerta).getByText('Cambiar el proveedor vuelve a buscar 1 línea por su código y borra sus precios.')).toBeInTheDocument()
    expect(within(puerta).queryByText(/por bulto/)).not.toBeInTheDocument()
    expect(busquedasDelProveedorDos()).toEqual([])

    await usuario.click(within(puerta).getByRole('button', { name: 'Cambiar proveedor' }))

    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue('Del dos'))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Proveedor')).toHaveValue('2')
    expect(celda(0, 'Código')).toHaveValue('AB-1')
    expect(celda(0, 'Cantidad')).toHaveDisplayValue('3')
    expect(celda(0, 'Importe')).toHaveValue('300,0000')
    expect(celda(0, 'Descuento (%)')).toHaveValue('20,00')
  })

  const CON_BULTO = articuloFixture({ id: 22, nombre: 'Con bulto', codigoProveedor: 'AB-1', costoLista: 242, idAlicuotaIva: 3, unidadesPorBulto: 12 })

  it('una línea resuelta por código en bultos pierde también la cantidad y el aviso lo dice; una en unidades la conserva', async () => {
    porBusqueda['AB-1@1'] = () => pagina([CON_BULTO])
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Con bulto')
    await resolverPorCodigo(usuario, 1, 'CD-2', 'Otro del uno')
    await usuario.type(celda(0, 'Cantidad'), '4')
    await usuario.type(celda(1, 'Cantidad'), '7')
    await usuario.selectOptions(celda(0, 'UM'), 'Bulto')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    const puerta = await screen.findByRole('alertdialog')
    expect(
      within(puerta).getByText(
        'Cambiar el proveedor vuelve a buscar 2 líneas por su código y borra sus precios. Las líneas cargadas por bulto también pierden la cantidad.',
      ),
    ).toBeInTheDocument()
    await usuario.click(within(puerta).getByRole('button', { name: 'Cambiar proveedor' }))

    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue('Del dos'))
    expect(celda(0, 'UM')).toHaveValue('Unidad')
    expect(celda(0, 'Cantidad')).toHaveDisplayValue('')
    expect(celda(1, 'Cantidad')).toHaveDisplayValue('7')
  })

  it('una línea en bultos que no se resolvió por código no hace que el aviso hable de bultos ni pierde su cantidad', async () => {
    porBusqueda['yerba@1'] = () => pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40', costoLista: 80, unidadesPorBulto: 12 })])
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    await usuario.type(celda(1, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))
    await usuario.type(celda(1, 'Cantidad'), '5')
    await usuario.selectOptions(celda(1, 'UM'), 'Bulto')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    const puerta = await screen.findByRole('alertdialog')
    expect(within(puerta).getByText('Cambiar el proveedor vuelve a buscar 1 línea por su código y borra sus precios.')).toBeInTheDocument()
    await usuario.click(within(puerta).getByRole('button', { name: 'Cambiar proveedor' }))
    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue('Del dos'))
    expect(celda(1, 'UM')).toHaveValue('Bulto')
    expect(celda(1, 'Cantidad')).toHaveDisplayValue('5')
  })

  it('el modo de precios del proveedor viejo no se conserva por el importe de líneas que se borran: toma el del nuevo y la precarga lo sigue', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario, [
      proveedorFixture({ preciosIncluyenIva: true }),
      proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA', preciosIncluyenIva: false }),
    ])
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()
    expect(celda(0, 'Importe')).toHaveValue('242,0000')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cambiar proveedor' }))

    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue('Del dos'))
    expect(screen.getByLabelText('Precios con IVA incluido')).not.toBeChecked()
    expect(celda(0, 'Importe')).toHaveValue('300,0000')
  })

  it('con una línea sobreviviente de importe tipeado el modo de precios se conserva', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario, [
      proveedorFixture({ preciosIncluyenIva: true }),
      proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA', preciosIncluyenIva: false }),
    ])
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    await cargarFila(usuario, 1, { detalle: 'Flete', cantidad: '1', importe: '500', iva: '3' })

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cambiar proveedor' }))

    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue('Del dos'))
    expect(screen.getByLabelText('Precios con IVA incluido')).toBeChecked()
    expect(celda(0, 'Importe')).toHaveValue('363,0000')
    expect(celda(1, 'Importe')).toHaveValue('500,0000')
  })

  it('un concepto con Detalle tipeado y un código pendiente no se busca solo al cambiar de proveedor: conserva todo', async () => {
    porBusqueda['ZZ-9@2'] = () => pagina([articuloFixture({ id: 52, nombre: 'Aparece en el dos', codigoProveedor: 'ZZ-9', costoLista: 999 })])
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await cargarFila(usuario, 0, { codigo: 'ZZ-9', detalle: 'Flete', cantidad: '1', importe: '500', descuento: '5', iva: '3' })
    await usuario.tab()
    await act(async () => {})

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')
    await act(async () => {})

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(busquedasDelProveedorDos()).toEqual([])
    expect(celda(0, 'Detalle')).toHaveValue('Flete')
    expect(celda(0, 'Importe')).toHaveValue('500,0000')
    expect(celda(0, 'Descuento (%)')).toHaveValue('5,00')
    expect(celda(0, 'IVA')).toHaveValue('3')
  })

  it('con varias líneas resueltas por código el aviso cuenta cuántas son, en plural', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    await resolverPorCodigo(usuario, 1, 'CD-2', 'Otro del uno')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    const puerta = await screen.findByRole('alertdialog')
    expect(within(puerta).getByText('Cambiar el proveedor vuelve a buscar 2 líneas por su código y borra sus precios.')).toBeInTheDocument()
  })

  it('cancelar deja el proveedor y todas las líneas como estaban y no consulta nada; mientras la puerta está abierta el resto de la pantalla está inerte', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')
    const puerta = await screen.findByRole('alertdialog')
    expect(screen.getByLabelText('Proveedor')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(celda(0, 'Cantidad')).toBeDisabled()

    await usuario.click(within(puerta).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Proveedor')).toHaveValue('1')
    expect(celda(0, 'Detalle')).toHaveValue('Del uno')
    expect(celda(0, 'Código')).toHaveValue('AB-1')
    expect(celda(0, 'Importe')).toHaveValue('200,0000')
    expect(celda(0, 'Descuento (%)')).toHaveValue('10,00')
    await act(async () => {})
    expect(busquedasDelProveedorDos()).toEqual([])
  })

  it('si el proveedor nuevo no tiene el código, la línea queda pendiente de asociar y sin ningún precio', async () => {
    porBusqueda['AB-1@2'] = () => pagina([])
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cambiar proveedor' }))

    await within(filaDeItems(0)).findByText('Pendiente de asociar')
    expect(celda(0, 'Detalle')).toHaveValue('')
    expect(celda(0, 'Importe')).toHaveValue('')
    expect(celda(0, 'Descuento (%)')).toHaveValue('')
    expect(celda(0, 'IVA')).toHaveValue('')
  })

  it('quedarse sin proveedor sigue la misma regla: pregunta, y al confirmar suelta las líneas sin consultar nada', async () => {
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await resolverPorCodigo(usuario, 0, 'AB-1', 'Del uno')
    const antes = apiGetMock.mock.calls.length

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '')
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cambiar proveedor' }))

    await waitFor(() => expect(celda(0, 'Detalle')).toHaveValue(''))
    expect(screen.getByLabelText('Proveedor')).toHaveValue('')
    expect(celda(0, 'Código')).toHaveValue('AB-1')
    expect(celda(0, 'Importe')).toHaveValue('')
    expect(apiGetMock.mock.calls.slice(antes).some((c: unknown[]) => String(c[0]).startsWith('/articulos?busqueda='))).toBe(false)
  })

  it('sin ninguna línea resuelta por código no hay pregunta: un concepto no se toca y un código pendiente se busca de nuevo', async () => {
    porBusqueda['ZZ-9@2'] = () => pagina([articuloFixture({ id: 52, nombre: 'Aparece en el dos', codigoProveedor: 'ZZ-9' })])
    const usuario = userEvent.setup()
    await montarConDosProveedores(usuario)
    await cargarFila(usuario, 0, { detalle: 'Flete', cantidad: '1', importe: '500', iva: '3' })
    await usuario.type(celda(1, 'Código'), 'ZZ-9')
    await usuario.tab()
    await within(filaDeItems(1)).findByText('Pendiente de asociar')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Proveedor')).toHaveValue('2')
    await waitFor(() => expect(celda(1, 'Detalle')).toHaveValue('Aparece en el dos'))
    expect(celda(0, 'Detalle')).toHaveValue('Flete')
    expect(celda(0, 'Importe')).toHaveValue('500,0000')
  })

  it('una línea cuyo artículo se eligió buscando no cuenta: cambiar el proveedor no pregunta ni la toca', async () => {
    mockearReferencia(
      (ruta) => (ruta.startsWith('/articulos?busqueda=yerba') ? pagina([articuloFixture({ id: 40, nombre: 'Yerba 1kg', codigoInterno: 'ART-40', costoLista: 80 })]) : undefined),
      undefined,
      undefined,
      [proveedorFixture(), proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA' })],
    )
    const usuario = userEvent.setup()
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await screen.findByRole('option', { name: 'Proveedor Dos SA' })
    await usuario.type(celda(0, 'Código'), 'ZZ-9')
    await usuario.type(celda(0, 'Detalle'), 'yerba')
    await usuario.click(await screen.findByRole('option', { name: /Yerba 1kg/ }))

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '2')

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(celda(0, 'Detalle')).toHaveValue('Yerba 1kg')
    expect(celda(0, 'Código')).toHaveValue('ZZ-9')
    expect(celda(0, 'Importe')).toHaveValue('66,1157')
  })
})

describe('CompraEditor — la pantalla queda inerte hasta que la navegación la reemplaza', () => {
  it('tras guardar un borrador nuevo, los botones siguen deshabilitados y un clic más no crea una segunda compra', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    navegacion.retener = true
    const usuario = userEvent.setup()
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await cargarConcepto(usuario, '500', 'Flete')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    await waitFor(() => expect(navegacion.llamadas).toHaveLength(1))
    expect(navegacion.llamadas[0][0]).toBe('/compras/99')

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('tras guardar y confirmar una compra nueva sigue inerte hasta navegar', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      if (ruta === '/compras/99/confirmar') return Promise.resolve(compraFixture({ id: 99, estado: 'Confirmada' }))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    navegacion.retener = true
    const usuario = userEvent.setup()
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await usuario.type(screen.getByLabelText('Punto de venta del comprobante'), '3')
    await usuario.type(screen.getByLabelText('Número del comprobante'), '12345')
    await usuario.type(screen.getByLabelText('Fecha del comprobante'), '01/08/2026')
    await usuario.tab()
    await cargarConcepto(usuario, '500', 'Flete')

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(navegacion.llamadas).toHaveLength(1))

    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeDisabled()
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))
    expect(apiPostMock).toHaveBeenCalledTimes(2)
  })

  it('si el guardado falla, la pantalla sí se libera para reintentar', async () => {
    mockearReferencia()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra con ese número.'))
    navegacion.retener = true
    const usuario = userEvent.setup()
    renderEditor('nueva')
    await completarEncabezadoDeCompraNueva(usuario)
    await cargarConcepto(usuario, '500', 'Flete')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await screen.findByText('Ya existe una compra con ese número.')
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeEnabled()
  })
})

describe('CompraEditor — datos del artículo que completa la pantalla (lote e IVA)', () => {
  it('una línea precargada desde una orden completa controlaLote y el IVA del artículo, y muestra el lote', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.resolve(ordenFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ id: 10, controlaLote: true, idAlicuotaIva: 3 }))
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    await screen.findByText(/Vinculada a la orden de compra/)
    expect(await screen.findByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/)).toHaveValue('3'))
  })

  it('una línea de orden cuyo artículo no controla lote no muestra el lote, y el IVA que el operador ya eligió no se pisa', async () => {
    let resolver: (r: unknown) => void = () => {}
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.resolve(ordenFixture())
      if (ruta === '/articulos/10') return new Promise((r) => (resolver = r))
      return undefined
    })
    const usuario = userEvent.setup()
    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')
    await screen.findByText(/Vinculada a la orden de compra/)
    await waitFor(() => expect(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/)).toBeEnabled())
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/articulos/10'))
    await usuario.selectOptions(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/), '3')

    await act(async () => {
      resolver(articuloFixture({ id: 10, controlaLote: false, idAlicuotaIva: 4 }))
    })

    expect(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/)).toHaveValue('3')
    expect(screen.queryByLabelText(/^Fecha de vencimiento, línea \d+$/)).not.toBeInTheDocument()
  })

  it('un borrador reabierto completa controlaLote desde el artículo y conserva la alícuota guardada', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture())
      if (ruta === '/articulos/10') return Promise.resolve(articuloFixture({ id: 10, controlaLote: true, idAlicuotaIva: 4 }))
      return undefined
    })
    renderEditor()
    await screen.findByDisplayValue('00012345')

    expect(await screen.findByLabelText(/^Fecha de vencimiento, línea \d+$/)).toBeInTheDocument()
    expect(within(filaDeItems(0)).getByLabelText(/^IVA, línea \d+$/)).toHaveValue('3')
  })
})
