import { render, screen, waitFor } from '@testing-library/react'
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
function mockearReferencia(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [proveedorFixture()], total: 1, pagina: 1, tamanio: 200 })
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve([tipoFixture()])
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
    expect(screen.getByText('$114,95')).toBeInTheDocument() // precio sugerido
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
    expect(screen.getByText('$1.149,50')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: '+ Agregar línea' }))

    // la línea nueva no tiene artículo ni alícuota elegidos — queda incompleta a propósito, pero
    // se le cargan unidades/costo para que, si el mirror la sumara, el total cambiaría.
    const unidades = screen.getAllByLabelText('Unidades')
    await usuario.type(unidades[1], '5')
    const costo = screen.getAllByLabelText('Costo unitario')
    await usuario.type(costo[1], '20')

    expect(await screen.findByText('1 línea(s) incompleta(s) — no se van a guardar.')).toBeInTheDocument()
    expect(screen.getByText('$1.149,50')).toBeInTheDocument()
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
    expect(screen.getByLabelText('Costo unitario')).toBeDisabled()
  })

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
