import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CompraEditor } from './CompraEditor'
import { RutaProtegida } from '../auth/RutaProtegida'
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
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: true,
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
    ...sobrescribir,
  }
}

function renderEditor(idCompra: string | number = 1) {
  return render(
    <MemoryRouter initialEntries={[`/compras/${idCompra}`]}>
      <Routes>
        <Route path="/compras/:id" element={<CompraEditor />} />
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
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve(tipos)
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve([alicuotaFixture()])
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
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
  usuarioActual = usuarioFixture()
})

describe('CompraEditor — borrador existente', () => {
  it('carga el header y los items del borrador', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    renderEditor()

    expect(await screen.findByDisplayValue('0003-00012345')).toBeInTheDocument()
    expect(screen.getByText('Elegido: Fideos 500g')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar borrador' })).toBeInTheDocument()
  })

  it('guardar borrador manda un PUT con el replace-set completo (header + items editados)', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockResolvedValue(compraFixture({ observaciones: 'Actualizado' }))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('0003-00012345')

    const costo = screen.getByLabelText('Costo unitario')
    await usuario.clear(costo)
    await usuario.type(costo, '120')

    await usuario.type(screen.getByLabelText('Observaciones'), 'Actualizado')

    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPutMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/compras/1')
    expect(cuerpo.observaciones).toBe('Actualizado')
    expect(cuerpo.items).toHaveLength(1)
    expect((cuerpo.items as Record<string, unknown>[])[0].costoUnitario).toBe(120)
    // replace-set: el header viaja completo, no solo el campo tocado.
    expect(cuerpo.idProveedor).toBe(1)
    expect(cuerpo.idTipoComprobante).toBe(5)
    expect(cuerpo.idPuntoVenta).toBe(2)

    expect(await screen.findByText('Borrador guardado.')).toBeInTheDocument()
  })

  it('un 409 compra_duplicada al guardar se muestra tal cual, sin envolver el mensaje', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra confirmada con ese número.'))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('0003-00012345')
    await usuario.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('Ya existe una compra confirmada con ese número.')).toBeInTheDocument()
  })

  it('confirmar: el checkbox de irreversibilidad bloquea el botón hasta tildarlo, y un doble click manda un solo POST', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    let resolverConfirmar: (valor: CompraDetalle) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras/1/confirmar') return new Promise((resolve) => (resolverConfirmar = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('0003-00012345')
    await usuario.click(screen.getByRole('button', { name: 'Confirmar compra' }))

    const botonConfirmarFinal = screen.getByRole('button', { name: 'Confirmar' })
    expect(botonConfirmarFinal).toBeDisabled()

    await usuario.click(screen.getByLabelText(/Confirmo que quiero confirmar esta compra/))
    expect(botonConfirmarFinal).toBeEnabled()

    // Doble click rápido — el guard de reentrancia de primera línea evita un segundo POST.
    await usuario.click(botonConfirmarFinal)
    await usuario.click(botonConfirmarFinal)

    resolverConfirmar(compraFixture({ estado: 'Confirmada', fechaRecepcion: '2026-08-05T12:00:00Z' }))
    await screen.findByText('Compra confirmada: el stock y el costo ya se actualizaron.')

    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1/confirmar')
    expect(llamadas).toHaveLength(1)
  })

  it('un 400 compra_incompleta_para_confirmar se renderiza de forma accionable en el panel', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
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
    await screen.findByDisplayValue('0003-00012345')
    await usuario.click(screen.getByRole('button', { name: 'Confirmar compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero confirmar esta compra/))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar' }))

    expect(await screen.findByText('La compra necesita número de comprobante y fecha para confirmarse.')).toBeInTheDocument()
  })
})

describe('CompraEditor — compra confirmada', () => {
  it('muestra los items de solo lectura con el precio sugerido, sin grilla editable', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ estado: 'Confirmada' })) : undefined))
    renderEditor()

    await screen.findByText('Fideos 500g')
    expect(screen.queryByLabelText('Costo unitario')).not.toBeInTheDocument()
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
    const resultados: ResultadoAplicarPrecio[] = [{ idArticulo: 10, aplicado: true, precio: 114.95, error: null }]
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

    resolverAplicar([{ idArticulo: 10, aplicado: true, precio: 114.95, error: null }])
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

    await usuario.selectOptions(screen.getByLabelText('Lista de precios'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(botonAnularFinal).toBeDisabled()

    resolverAplicar([{ idArticulo: 10, aplicado: true, precio: 114.95, error: null }])
    await waitFor(() => expect(botonAnularFinal).toBeEnabled())

    const llamadasAnular = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/compras/1/anular')
    expect(llamadasAnular).toHaveLength(0)
  })
})

describe('CompraEditor — compra nueva', () => {
  it('crear borrador manda un POST con el header completo y los items completos únicamente', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.resolve(compraFixture({ id: 99 }))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await screen.findByLabelText('Proveedor')

    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/compras')
    expect(cuerpo).toMatchObject({ idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2, items: [] })
  })

  it('un 409 compra_duplicada al crear se muestra sin navegar (sigue en modo nuevo)', async () => {
    mockearReferencia()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/compras') return Promise.reject(new ErrorApi(409, 'compra_duplicada', 'Ya existe una compra con ese número.'))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderEditor('nueva')
    await screen.findByLabelText('Proveedor')
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    expect(await screen.findByText('Ya existe una compra con ese número.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear borrador' })).toBeInTheDocument()
  })
})

describe('CompraEditor — líneas incompletas', () => {
  it('una línea incompleta no entra en el total mostrado, y se avisa con un contador', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('0003-00012345')
    expect(screen.getByText('$ 1.149,50')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: '+ Agregar línea' }))

    // la línea nueva no tiene artículo ni alícuota elegidos — queda incompleta a propósito, pero
    // se le cargan unidades/costo para que, si el mirror la sumara, el total cambiaría.
    const unidades = screen.getAllByLabelText('Unidades')
    await usuario.type(unidades[1], '5')
    const costo = screen.getAllByLabelText('Costo unitario')
    await usuario.type(costo[1], '20')

    expect(await screen.findByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()
    expect(screen.getByText('$ 1.149,50')).toBeInTheDocument()
  })
})

describe('CompraEditor — líneas con control de lote (stage-12-lotes-vencimientos, Slice 14)', () => {
  it('elegir un artículo que controla lote muestra los inputs de lote y suma al contador hasta cargar la fecha de vencimiento', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1') return Promise.resolve(compraFixture({ items: [] }))
      if (ruta.startsWith('/articulos?busqueda=')) {
        return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
      }
      return undefined
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('0003-00012345')

    await usuario.click(screen.getByRole('button', { name: '+ Agregar línea' }))
    await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'leche')
    await screen.findByText('ART-20 — Leche en polvo 800g')
    await usuario.click(screen.getByText('ART-20 — Leche en polvo 800g'))

    expect(screen.getByLabelText('Fecha de vencimiento')).toBeInTheDocument()
    expect(await screen.findByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Unidades'), '5')
    await usuario.type(screen.getByLabelText('Costo unitario'), '20')
    await usuario.selectOptions(screen.getByLabelText('Alícuota de IVA'), '3')

    // Sigue incompleta: el artículo controla lote y la fecha de vencimiento es obligatoria
    // (espejo del `lote_requerido` server-side) — código de lote NO es obligatorio, se deriva.
    expect(screen.getByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Fecha de vencimiento'), '2026-12-01')

    await waitFor(() => expect(screen.queryByText(/línea\(s\) incompleta\(s\)/)).not.toBeInTheDocument())
  })

  it('un artículo que no controla lote nunca muestra los inputs de lote', async () => {
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))
    renderEditor()

    await screen.findByText('Elegido: Fideos 500g')
    expect(screen.getByText('No controla lote')).toBeInTheDocument()
    expect(screen.queryByLabelText('Fecha de vencimiento')).not.toBeInTheDocument()
  })

  it('re-elegir el artículo de una línea con lote cargado, por uno que no controla lote, no deja codigoLote/fechaVencimiento stale en el payload (judgment-day, MAJOR juez A)', async () => {
    const articuloSinLote = articuloFixture({ id: 30, codigoInterno: 'ART-30', nombre: 'Coca Cola 1.5L', controlaLote: false })
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1')
        return Promise.resolve(compraFixture({ items: [itemFixture({ codigoLote: 'LOTE-A', fechaVencimiento: '2026-06-01' })] }))
      if (ruta.startsWith('/articulos?busqueda=')) return Promise.resolve({ items: [articuloSinLote], total: 1, pagina: 1, tamanio: 25 })
      return undefined
    })
    apiPutMock.mockResolvedValue(compraFixture())
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByText('Elegido: Fideos 500g')
    expect(screen.getByLabelText('Código de lote')).toHaveValue('LOTE-A')
    expect(screen.getByLabelText('Fecha de vencimiento')).toHaveValue('2026-06-01')

    await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'coca')
    await screen.findByText('ART-30 — Coca Cola 1.5L')
    await usuario.click(screen.getByText('ART-30 — Coca Cola 1.5L'))

    // El artículo nuevo no controla lote: los inputs de lote desaparecen — el reset no es solo
    // interno, también es visible (el operador ve que el lote del artículo anterior ya no aplica).
    expect(screen.getByText('No controla lote')).toBeInTheDocument()
    expect(screen.queryByLabelText('Código de lote')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Fecha de vencimiento')).not.toBeInTheDocument()

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
    mockearReferencia((ruta) => {
      if (ruta === '/compras/1')
        return Promise.resolve(compraFixture({ items: [itemFixture({ codigoLote: 'LOTE-A', fechaVencimiento: '2026-06-01' })] }))
      if (ruta.startsWith('/articulos?busqueda=')) return Promise.resolve({ items: [otroArticuloConLote], total: 1, pagina: 1, tamanio: 25 })
      return undefined
    })
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByText('Elegido: Fideos 500g')
    expect(screen.getByLabelText('Código de lote')).toHaveValue('LOTE-A')
    expect(screen.getByLabelText('Fecha de vencimiento')).toHaveValue('2026-06-01')

    await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'yerba')
    await screen.findByText('ART-40 — Yerba 1kg')
    await usuario.click(screen.getByText('ART-40 — Yerba 1kg'))

    expect(screen.getByLabelText('Código de lote')).toHaveValue('')
    expect(screen.getByLabelText('Fecha de vencimiento')).toHaveValue('')
  })
})

describe('CompraEditor — role gating', () => {
  it('un Vendedor llega a la ruta (decisión 11) y ve el borrador de solo lectura, sin acciones de escritura', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture()) : undefined))

    renderEditorProtegido()
    await screen.findByDisplayValue('0003-00012345')

    // el gate de rol de la ruta lo dejó pasar — no lo mandó a "/".
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar borrador' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar compra' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Proveedor')).toBeDisabled()
    expect(screen.queryByLabelText('Costo unitario')).not.toBeInTheDocument()
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
      await screen.findByDisplayValue('0003-00012345')

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
    await screen.findByDisplayValue('0003-00012345')
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
    expect(await screen.findByDisplayValue('0003-00012345')).toBeInTheDocument()
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
    expect(screen.getByLabelText('Unidades')).toHaveValue(2) // Pendiente, no Pedida
    expect(screen.getByLabelText('Costo unitario')).toHaveValue('100,0000') // CostoEstimado de la cobertura
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
    expect(screen.getAllByLabelText('Unidades')).toHaveLength(1)
    expect(screen.getByLabelText('Unidades')).toHaveValue(3)
  })

  it('un artículo nunca cotizado precarga costo unitario vacío, nunca "0"', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/ordenes-compra/30') return Promise.resolve(ordenFixture({ cobertura: [coberturaFixture({ costoEstimado: null })] }))
      return undefined
    })

    renderEditorEnRuta('/compras/nueva?idOrdenCompra=30')

    await screen.findByText(/Vinculada a la orden de compra/)
    expect(screen.getByLabelText('Costo unitario')).toHaveValue('')
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
    expect(screen.getByLabelText('Fecha del comprobante')).toHaveValue('2026-08-15')
    expect(screen.getByLabelText('Número de comprobante')).toHaveValue('0003-00099999')
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
    const usuario = userEvent.setup()

    renderEditorEnRuta('/compras/1?desdeGasto=5')
    await screen.findByText('Se vinculará al gasto #5 al confirmar.')

    await usuario.click(screen.getByRole('button', { name: 'Confirmar compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero confirmar esta compra/))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar' }))

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
    const usuario = userEvent.setup()

    renderEditorEnRuta('/compras/1?desdeGasto=5')
    await screen.findByText('Se vinculará al gasto #5 al confirmar.')

    await usuario.click(screen.getByRole('button', { name: 'Confirmar compra' }))
    await usuario.click(screen.getByLabelText(/Confirmo que quiero confirmar esta compra/))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar' }))

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
    // Esperar el DATO (las opciones cargadas), no el select que se renderiza antes del fetch.
    await screen.findByRole('option', { name: 'Proveedor Uno SA' })
    await screen.findByRole('option', { name: /C-FA/ })
    await screen.findByRole('option', { name: 'Casa Central' })
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cargar por total' })).toBeEnabled())
  }

  it('Cargar por total agrega UNA línea por concepto de cantidad 1 con el importe tipeado y la manda sin artículo', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Cargar por total' }))
    expect(screen.getByText(/cargá el importe neto, el IVA se suma/)).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Importe total'), '1234,56')
    await usuario.click(screen.getByRole('button', { name: 'Agregar como concepto' }))

    expect(screen.getByText('Concepto')).toBeInTheDocument()
    expect(screen.getByLabelText('Descripción del concepto')).toHaveValue('Total del comprobante')
    expect(screen.getByLabelText('Unidades')).toHaveValue(1)
    expect(screen.getByLabelText('Costo unitario')).toHaveValue('1.234,5600')
    expect(screen.queryByLabelText('Bultos')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Actualiza costo')).not.toBeInTheDocument()
    expect(screen.queryByPlaceholderText('Buscar artículo…')).not.toBeInTheDocument()
    // el panel se cierra al agregar y el mirror suma el concepto: 1234,56 + 21% de IVA
    expect(screen.queryByText('Cargar por total', { selector: 'strong' })).not.toBeInTheDocument()
    expect(screen.getByText('$ 1.493,82')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

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
      },
    ])
  })

  it('Cargar por total no agrega nada mientras el importe sea cero o esté vacío', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Cargar por total' }))
    const agregar = screen.getByRole('button', { name: 'Agregar como concepto' })
    expect(agregar).toBeDisabled()

    await usuario.type(screen.getByLabelText('Importe total'), '0')
    expect(agregar).toBeDisabled()
    expect(screen.queryByLabelText('Descripción del concepto')).not.toBeInTheDocument()
  })

  it('+ Agregar concepto arma una línea por concepto vacía que no se guarda hasta tener descripción, costo y alícuota', async () => {
    mockearReferencia()
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario)

    await usuario.click(screen.getByRole('button', { name: '+ Agregar concepto' }))
    expect(screen.getByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Descripción del concepto'), 'Flete')
    await usuario.type(screen.getByLabelText('Costo unitario'), '500')
    expect(screen.getByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Alícuota de IVA'), '3')
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
    await screen.findByDisplayValue('0003-00012345')

    expect(screen.getByLabelText('Descripción del concepto')).toHaveValue('Flete')
    // solo la línea de artículo ofrece bultos y actualiza costo
    expect(screen.getAllByLabelText('Bultos')).toHaveLength(1)
    expect(screen.getAllByLabelText('Actualiza costo')).toHaveLength(1)

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
    expect(screen.queryByLabelText('Costo unitario')).not.toBeInTheDocument()
  })

  it('confirmar una compra solo de conceptos no promete mover stock ni costo', async () => {
    const concepto = itemFixture({
      orden: 1, idArticulo: null, descripcion: 'Ferretería', cantidad: 1, costoUnitario: 500, descuento: 0, total: 500,
      actualizaCosto: false, precioSugerido: null,
    })
    mockearReferencia((ruta) => (ruta === '/compras/1' ? Promise.resolve(compraFixture({ items: [concepto] })) : undefined))
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/compras/1/confirmar'
        ? Promise.resolve(compraFixture({ estado: 'Confirmada', items: [concepto] }))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    const usuario = userEvent.setup()

    renderEditor()
    await screen.findByDisplayValue('Ferretería')
    await usuario.click(screen.getByRole('button', { name: 'Confirmar compra' }))

    const casilla = screen.getByLabelText(/Confirmo que quiero confirmar esta compra/)
    expect(casilla.closest('div')).not.toHaveTextContent('stock')
    await usuario.click(casilla)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar' }))

    expect(await screen.findByText('Compra confirmada.')).toBeInTheDocument()
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
    await screen.findByRole('option', { name: /C-RM/ })
    await screen.findByRole('option', { name: 'Casa Central' })
    await usuario.selectOptions(screen.getByLabelText('Proveedor'), '1')
    await usuario.selectOptions(screen.getByLabelText('Tipo'), idTipo)
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '2')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cargar por total' })).toBeEnabled())
  }

  async function cargarConceptoPorTotal(usuario: ReturnType<typeof userEvent.setup>, importe: string) {
    await usuario.click(screen.getByRole('button', { name: 'Cargar por total' }))
    await usuario.type(screen.getByLabelText('Importe total'), importe)
    await usuario.click(screen.getByRole('button', { name: 'Agregar como concepto' }))
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
    await cargarConceptoPorTotal(usuario, '1000')

    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

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
    await cargarConceptoPorTotal(usuario, '1000')

    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const tabla = screen.getByRole('table', { name: 'Desglose de IVA' })
    const fila = within(tabla).getByText('21%').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(fila).getByLabelText('IVA impreso 21%')).toHaveValue('210,00')

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBe(true)
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('corregir el IVA de una alícuota recalcula el total y manda el importe impreso', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConceptoPorTotal(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210,5')
    await usuario.tab()

    expect(screen.getByText('Calculado: $ 210,00')).toBeInTheDocument()
    expect(screen.getByText('$ 1.210,50')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.ivaImpreso).toEqual([{ idAlicuotaIva: 3, iva: 210.5 }])
  })

  it('un IVA impreso que se pasa de un peso del calculado avisa antes de guardar', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConceptoPorTotal(usuario, '1000')
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
    await cargarConceptoPorTotal(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))

    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210')
    await usuario.tab()
    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('cambiar de tipo descarta el IVA impreso y una factura A siempre discrimina', async () => {
    apiPostMock.mockResolvedValue(compraFixture({ id: 99 }))
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '7')
    await cargarConceptoPorTotal(usuario, '1000')
    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    const campo = screen.getByLabelText('IVA impreso 21%')
    await usuario.clear(campo)
    await usuario.type(campo, '210,5')
    await usuario.tab()

    await usuario.selectOptions(screen.getByLabelText('Tipo'), '5')

    expect(screen.getByLabelText('IVA impreso 21%')).toHaveValue('210,00')
    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBeNull()
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('una factura B no discrimina IVA: ni tilde ni desglose', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '6')
    await cargarConceptoPorTotal(usuario, '1000')

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
    await screen.findByDisplayValue('0003-00012345')
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
    await cargarConceptoPorTotal(usuario, '1000')

    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    expect(screen.getByRole('table', { name: 'Desglose de IVA' })).toBeInTheDocument()
    await usuario.click(screen.getByLabelText('Discrimina IVA'))
    expect(screen.queryByRole('table', { name: 'Desglose de IVA' })).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    const [, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(cuerpo.discriminaIva).toBe(false)
    expect(cuerpo.ivaImpreso).toBeNull()
  })

  it('pasar de una factura A al remito no hereda el IVA discriminado de la factura', async () => {
    const usuario = userEvent.setup()
    await prepararCompraNueva(usuario, '5')
    await cargarConceptoPorTotal(usuario, '1000')
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
