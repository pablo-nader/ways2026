import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Pos } from './Pos'
import { ErrorApi } from '../api/cliente'
import type {
  ArticuloEscaneado,
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoListado,
  PaginaDe,
  ParametroResuelto,
  PuntoVentaListado,
  ResultadoDeResolucion,
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

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    ...sobrescribir,
  }
}

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 1,
    numero: 1,
    nombre: 'Consumidor Final',
    apellido: null,
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 1,
    nacimiento: null,
    domicilio: null,
    telefono: null,
    celular: null,
    email: null,
    observaciones: null,
    idListaPrecio: 1,
    limiteCredito: 0,
    creditoIlimitado: true,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: true,
    ...sobrescribir,
  }
}

function articuloEscaneadoFixture(sobrescribir: Partial<ArticuloEscaneado> = {}): ArticuloEscaneado {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

const medioEfectivo = medioFixture()
const medioTarjeta = medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true })
const medioCuentaCorriente = medioFixture({ id: 3, nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente', admiteVuelto: false })

function comprobanteEmitidoFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 501,
    numero: 1,
    numeroVisible: '0007-00000001',
    estado: 'Emitido',
    fecha: '2026-08-04T15:30:00Z',
    idPuntoVenta: 7,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 100,
    descuentoTotal: 0,
    total: 100,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Coca Cola 1L',
        codigoBarra: '7790001234567',
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 1,
        precioUnitario: 100,
        descuento: 0,
        total: 100,
      },
    ],
    pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }],
    ...sobrescribir,
  }
}

/**
 * jsdom sanea el `value` de un `<input type="number">` a `""` apenas se le asigna un número
 * incompleto (ej. "1."), a diferencia de un navegador real que preserva el texto tipeado
 * mientras el usuario sigue escribiendo. Este helper sobrescribe la propiedad `value` de la
 * instancia (que blindea al getter del prototipo) antes de disparar el evento, para poder
 * reproducir en el test el mismo estado intermedio que ve un navegador real.
 */
function escribirValorCrudo(input: HTMLInputElement, valor: string) {
  Object.defineProperty(input, 'value', { value: valor, configurable: true })
  act(() => {
    input.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }))
  })
}

const consumidorFinal = clienteFixture()
const otroCliente = clienteFixture({ id: 2, numero: 2, nombre: 'Juan', apellido: 'Pérez', esConsumidorFinal: false })
const puntoVentaCentro = puntoVentaFixture()

function mockearApiGet() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
    if (ruta === '/clientes') {
      const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta.startsWith('/clientes?busqueda=')) {
      const pagina: PaginaDe<ClienteListado> = { items: [otroCliente], total: 1, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta.startsWith('/articulos/escaneo?entrada=')) {
      return Promise.resolve(articuloEscaneadoFixture())
    }
    if (ruta === '/catalogos/medios-pago') {
      return Promise.resolve<MedioPagoListado[]>([medioEfectivo, medioTarjeta, medioCuentaCorriente])
    }
    if (ruta.startsWith('/parametros/tolerancia_pago')) {
      return Promise.resolve<ParametroResuelto>({ clave: 'tolerancia_pago', valor: '10' })
    }
    if (ruta.startsWith('/parametros/vuelto_maximo')) {
      return Promise.resolve<ParametroResuelto>({ clave: 'vuelto_maximo', valor: '20' })
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  mockearApiGet()
  apiPostMock.mockImplementation((ruta: string) => {
    if (ruta === '/ofertas/resolver') {
      const resultados: ResultadoDeResolucion[] = [
        { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
      ]
      return Promise.resolve(resultados)
    }
    if (ruta === '/ventas') {
      return Promise.resolve(comprobanteEmitidoFixture())
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
})

/** Deja el carrito con una línea de Coca Cola ($100, sin descuento) y el panel de pagos listo:
 * medio Efectivo elegido, importe = total. Punto de partida de los tests de checkout. */
async function armarVentaLista() {
  render(<Pos />)
  await screen.findByRole('option', { name: /Consumidor Final/ })

  await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Coca Cola 1L')
  await waitFor(() => expect(screen.getByText('$100,00', { selector: 'strong' })).toBeInTheDocument())

  await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
  const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
  await userEvent.type(importe, '100')

  await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
}

describe('Pos — carga inicial', () => {
  it('selecciona el Consumidor Final por defecto y el único punto de venta disponible', async () => {
    render(<Pos />)

    expect(await screen.findByRole('option', { name: /Consumidor Final/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Cliente')).toHaveValue(String(consumidorFinal.id))
    await waitFor(() => expect(screen.getByLabelText('Punto de venta')).toHaveValue(String(puntoVentaCentro.id)))
  })
})

describe('Pos — escaneo', () => {
  it('escanear un código agrega una línea al carrito', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(await screen.findByText('Coca Cola 1L')).toBeInTheDocument()
    expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(1)
  })

  it('re-escanear el mismo código suma la cantidad en la misma línea, no duplica', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)

    await waitFor(() => expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(2))
    expect(screen.getAllByText('Coca Cola 1L')).toHaveLength(1)
  })

  it('el input de escaneo se limpia después de un escaneo exitoso', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    await screen.findByText('Coca Cola 1L')
    expect(entrada).toHaveValue('')
  })

  it('un código no encontrado muestra un error y no agrega ninguna línea', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/clientes') {
        const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=')) {
        return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No se encontró un artículo activo para el código 999.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '999')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(await screen.findByText('No se encontró un artículo activo para el código 999.')).toBeInTheDocument()
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })
})

describe('Pos — selector de cliente', () => {
  it('buscar y elegir otro cliente lo deja seleccionado', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'perez')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    const opcionJuan = await screen.findByRole('option', { name: /Juan Pérez/ })
    await userEvent.selectOptions(screen.getByLabelText('Cliente'), opcionJuan)

    expect(screen.getByLabelText('Cliente')).toHaveValue(String(otroCliente.id))
  })
})

describe('Pos — regresión: carga inicial de clientes vs. selección del usuario', () => {
  it('una respuesta tardía del fetch de montaje no pisa una selección hecha durante una búsqueda posterior', async () => {
    let resolverMontaje: (pagina: PaginaDe<ClienteListado>) => void = () => {}
    const montajePendiente = new Promise<PaginaDe<ClienteListado>>((resolve) => {
      resolverMontaje = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/clientes') return montajePendiente
      if (ruta.startsWith('/clientes?busqueda=')) {
        const pagina: PaginaDe<ClienteListado> = { items: [otroCliente], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)

    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'perez')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    const opcionJuan = await screen.findByRole('option', { name: /Juan Pérez/ })
    await userEvent.selectOptions(screen.getByLabelText('Cliente'), opcionJuan)
    expect(screen.getByLabelText('Cliente')).toHaveValue(String(otroCliente.id))

    await act(async () => {
      resolverMontaje({ items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 })
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(screen.getByLabelText('Cliente')).toHaveValue(String(otroCliente.id))
  })
})

describe('Pos — vista previa de precios', () => {
  it('una resolución exitosa muestra el precio unitario, el original tachado, el total de línea y el subtotal previo', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          {
            idArticulo: 1,
            idListaPrecio: 1,
            precioOriginal: 120,
            precioFinal: 100,
            descuentoUnitario: 20,
            aplicadas: [{ idOferta: 9, nombre: '2x1 Gaseosas', descuentoUnitario: 20 }],
          },
        ]
        return Promise.resolve(resultados)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await waitFor(() => expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(2))

    const fila = screen.getByText('Coca Cola 1L').closest('tr') as HTMLElement
    await waitFor(() => expect(within(fila).getByText('$120,00')).toBeInTheDocument())
    expect(within(fila).getByText('$100,00')).toBeInTheDocument()
    expect(within(fila).getByText('$200,00')).toBeInTheDocument()
    expect(within(fila).getByText('2x1 Gaseosas')).toBeInTheDocument()

    await waitFor(() => expect(screen.getByText('$200,00', { selector: 'strong' })).toBeInTheDocument())
  })

  it('una resolución rechazada muestra el aviso no bloqueante y el carrito sigue usable', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.reject(new Error('falló la resolución'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    expect(
      await screen.findByText('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.'),
    ).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })

  it('regresión: una respuesta desactualizada del resolver no pisa una más reciente (fuera de orden)', async () => {
    let resolverPrimera: (resultados: ResultadoDeResolucion[]) => void = () => {}
    const primeraPendiente = new Promise<ResultadoDeResolucion[]>((resolve) => {
      resolverPrimera = resolve
    })

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta !== '/ofertas/resolver') return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
      if (apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ofertas/resolver').length === 1) {
        return primeraPendiente
      }
      const resultados: ResultadoDeResolucion[] = [
        { idArticulo: 1, idListaPrecio: 1, precioOriginal: 90, precioFinal: 90, descuentoUnitario: 0, aplicadas: [] },
      ]
      return Promise.resolve(resultados)
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await waitFor(() => expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(2))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))

    const fila = screen.getByText('Coca Cola 1L').closest('tr') as HTMLElement
    await waitFor(() => expect(within(fila).getByText('$90,00')).toBeInTheDocument())

    await act(async () => {
      resolverPrimera([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(within(fila).getByText('$90,00')).toBeInTheDocument()
  })
})

describe('Pos — panel de pagos: precondiciones', () => {
  it('Cobrar está deshabilitado con el carrito vacío', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })

  it('medios de pago o parámetros que fallan al cargar dejan Cobrar deshabilitado con un aviso legible', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/clientes') {
        const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
      if (ruta === '/catalogos/medios-pago') {
        return Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar los medios de pago.'))
      }
      if (ruta.startsWith('/parametros/')) return Promise.resolve<ParametroResuelto>({ clave: 'x', valor: '10' })
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    expect(await screen.findByText('No se pudieron cargar los medios de pago.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })

  it('Cobrar permanece deshabilitado mientras la primera resolución de precios está pendiente, aunque el resto de las precondiciones ya esté listo', async () => {
    let resolverPrimera: (resultados: ResultadoDeResolucion[]) => void = () => {}
    const primeraPendiente = new Promise<ResultadoDeResolucion[]>((resolve) => {
      resolverPrimera = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return primeraPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '100')

    expect(screen.getByText('Calculando…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()

    await act(async () => {
      resolverPrimera([
        { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
      ])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
  })
})

describe('Pos — regresión: "resolviendo" no queda huérfano en `true`', () => {
  it('vaciar el carrito mientras una resolución de precios está en vuelo no deja "Calculando…" para siempre, ni aunque la fetch huérfana se asiente después', async () => {
    let resolverPrimera: (resultados: ResultadoDeResolucion[]) => void = () => {}
    const primeraPendiente = new Promise<ResultadoDeResolucion[]>((resolve) => {
      resolverPrimera = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return primeraPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    expect(screen.getByText('Calculando…')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
    expect(screen.queryByText('Calculando…')).not.toBeInTheDocument()

    await act(async () => {
      resolverPrimera([
        { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
      ])
      await Promise.resolve()
    })

    expect(screen.queryByText('Calculando…')).not.toBeInTheDocument()
  })
})

describe('Pos — panel de pagos: cuenta corriente y vuelto', () => {
  it('cuenta corriente no aparece como opción para el Consumidor Final', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioEfectivo.nombre })

    expect(screen.queryByRole('option', { name: medioCuentaCorriente.nombre })).not.toBeInTheDocument()
  })

  it('cuenta corriente aparece como opción para un cliente que no es Consumidor Final', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'perez')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    const opcionJuan = await screen.findByRole('option', { name: /Juan Pérez/ })
    await userEvent.selectOptions(screen.getByLabelText('Cliente'), opcionJuan)

    expect(await screen.findByRole('option', { name: medioCuentaCorriente.nombre })).toBeInTheDocument()
  })

  it('el input de vuelto está deshabilitado para un medio sin AdmiteVuelto', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioTarjeta.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioTarjeta.nombre)

    expect(screen.getByLabelText(`Vuelto de ${medioTarjeta.nombre} (fila 1)`)).toBeDisabled()
  })

  it('el input de vuelto queda habilitado para un medio con AdmiteVuelto', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioEfectivo.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)

    expect(screen.getByLabelText(`Vuelto de ${medioEfectivo.nombre} (fila 1)`)).toBeEnabled()
  })
})

describe('Pos — checkout', () => {
  it('un cobro exitoso muestra el ticket con el número emitido y resetea el carrito', async () => {
    await armarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    expect(screen.getByText('Coca Cola 1L')).toBeInTheDocument()
    expect(screen.getByText(/Total: \$100,00/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva venta' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })

  it('nuevaVenta limpia el error de escaneo que haya quedado de antes de cobrar', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/clientes') {
        const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=999')) {
        return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No se encontró un artículo activo para el código 999.'))
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
      if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo, medioTarjeta, medioCuentaCorriente])
      if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve<ParametroResuelto>({ clave: 'tolerancia_pago', valor: '10' })
      if (ruta.startsWith('/parametros/vuelto_maximo')) return Promise.resolve<ParametroResuelto>({ clave: 'vuelto_maximo', valor: '20' })
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '999')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    expect(await screen.findByText('No se encontró un artículo activo para el código 999.')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Código escaneado'))
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva venta' }))
    expect(screen.queryByText('No se encontró un artículo activo para el código 999.')).not.toBeInTheDocument()
  })

  it('doble click en Cobrar dispara exactamente un POST', async () => {
    let resolverCheckout: (comprobante: ComprobanteEmitido) => void = () => {}
    const checkoutPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverCheckout = resolve
    })

    await armarVentaLista()

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return checkoutPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const boton = screen.getByRole('button', { name: /Cobrar/ })
    await userEvent.click(boton)
    await userEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Cobrando…' })).toBeDisabled()

    await act(async () => {
      resolverCheckout(comprobanteEmitidoFixture())
      await Promise.resolve()
    })
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
  })

  it('mientras el cobro está en curso, el escaneo y la edición del carrito quedan inertes', async () => {
    let resolverCheckout: (comprobante: ComprobanteEmitido) => void = () => {}
    const checkoutPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverCheckout = resolve
    })

    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return checkoutPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
    expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar' })).toBeDisabled()
    expect(screen.getByLabelText('Cliente')).toBeDisabled()
    expect(screen.getByLabelText('Medio de pago')).toBeDisabled()

    await act(async () => {
      resolverCheckout(comprobanteEmitidoFixture())
      await Promise.resolve()
    })
  })

  it('un checkout rechazado muestra el mensaje del servidor y no resetea el carrito ni el panel de pagos', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(
          new ErrorApi(400, 'tolerancia_de_pago_superada', 'El pago ingresado no cubre el total, ni siquiera con la tolerancia.'),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('El pago ingresado no cubre el total, ni siquiera con la tolerancia.')).toBeInTheDocument()
    expect(screen.getByText('Coca Cola 1L')).toBeInTheDocument()
    expect(screen.queryByText(/^Venta /)).not.toBeInTheDocument()
  })
})

describe('Pos — checkout: split de pago con el mismo medio', () => {
  it('dos filas de Efectivo (split de pago) no colapsan: cada una envía su propio importe y vuelto', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '60')

    await userEvent.click(screen.getByRole('button', { name: '+ Agregar medio de pago' }))
    await userEvent.selectOptions(screen.getAllByLabelText('Medio de pago')[1], medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 2)'), '50')

    // Con ambos importes cargados (60 + 50 = 110 sobre un total de 100), el excedente es 10 y el
    // sugerido por defecto se lo lleva íntegro la fila 1 (la primera que admite vuelto) — recién
    // acá, contra un valor sugerido ya no-cero, la sobreescritura manual de la fila 1 a "0" es un
    // cambio real de valor (dispara el evento); si se sobrescribiera antes, con el sugerido
    // todavía en 0, el input ya mostraría "0" y React no dispararía `onChange` por no detectar
    // una diferencia real.
    fireEvent.change(screen.getByLabelText('Vuelto de Efectivo (fila 1)'), { target: { value: '0' } })
    fireEvent.change(screen.getByLabelText('Vuelto de Efectivo (fila 2)'), { target: { value: '10' } })

    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await waitFor(() => expect(apiPostMock.mock.calls.some((llamada) => llamada[0] === '/ventas')).toBe(true))
    const llamadaVentas = apiPostMock.mock.calls.find((llamada) => llamada[0] === '/ventas')
    const solicitud = llamadaVentas?.[1] as {
      pagos: { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }[]
    }

    expect(solicitud.pagos).toEqual([
      { idMedioPago: medioEfectivo.id, importe: 60, referencia: null, vuelto: 0 },
      { idMedioPago: medioEfectivo.id, importe: 50, referencia: null, vuelto: 10 },
    ])
  })
})

describe('Pos — badge de ofertas apiladas', () => {
  it('con dos ofertas aplicadas muestra el primer nombre y un contador de las restantes', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          {
            idArticulo: 1,
            idListaPrecio: 1,
            precioOriginal: 150,
            precioFinal: 100,
            descuentoUnitario: 50,
            aplicadas: [
              { idOferta: 9, nombre: '2x1 Gaseosas', descuentoUnitario: 30 },
              { idOferta: 10, nombre: 'Descuento Efectivo', descuentoUnitario: 20 },
            ],
          },
        ]
        return Promise.resolve(resultados)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    const fila = screen.getByText('Coca Cola 1L').closest('tr') as HTMLElement
    const badge = await within(fila).findByText('2x1 Gaseosas +1')
    expect(badge).toHaveAttribute('title', '2x1 Gaseosas, Descuento Efectivo')
  })
})

describe('Pos — edición de cantidad', () => {
  async function agregarLineaCocaCola() {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    return screen.getByLabelText('Cantidad de Coca Cola 1L') as HTMLInputElement
  }

  it('tipear "1." sobre una línea con cantidad 1 conserva el punto decimal visible y no dispara una resolución redundante (mismo valor comprometido), pero completar a "1.5" sí dispara una resolución', async () => {
    const input = await agregarLineaCocaCola()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    vi.useFakeTimers()
    try {
      escribirValorCrudo(input, '1.')
      expect(input.value).toBe('1.')
      await vi.advanceTimersByTimeAsync(300)
      expect(apiPostMock).toHaveBeenCalledTimes(1)
      expect(input.value).toBe('1.')

      escribirValorCrudo(input, '1.5')
      await vi.advanceTimersByTimeAsync(300)
      expect(apiPostMock).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('poner la cantidad en "0" y perder el foco hace que el input vuelva a mostrar la cantidad confirmada', async () => {
    const input = await agregarLineaCocaCola()

    fireEvent.change(input, { target: { value: '0' } })
    expect(input.value).toBe('0')

    fireEvent.blur(input)

    expect(input.value).toBe('1')
  })

  it('el guard de cantidad mínima rechaza valores por debajo de 0.001, el mismo piso declarado en min/step', async () => {
    const input = await agregarLineaCocaCola()
    expect(input).toHaveAttribute('min', '0.001')
    expect(input).toHaveAttribute('step', '0.001')

    fireEvent.change(input, { target: { value: '0.0001' } })
    expect(input.value).toBe('0.0001')

    fireEvent.blur(input)

    expect(input.value).toBe('1')
  })
})

describe('Pos — debounce de la resolución de precios', () => {
  it('una edición debounce ~250ms y una segunda edición dentro de la ventana reemplaza a la primera; un escaneo resuelve sin demora', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    vi.useFakeTimers()
    try {
      const input = screen.getByLabelText('Cantidad de Coca Cola 1L')

      fireEvent.change(input, { target: { value: '2' } })
      await vi.advanceTimersByTimeAsync(100)
      expect(apiPostMock).toHaveBeenCalledTimes(1)

      fireEvent.change(input, { target: { value: '3' } })
      await vi.advanceTimersByTimeAsync(200)
      expect(apiPostMock).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(100)
      expect(apiPostMock).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('un cambio de cliente inmediatamente después de una edición de cantidad no hereda la demora de esa edición', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))

    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'perez')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    await screen.findByRole('option', { name: /Juan Pérez/ })

    vi.useFakeTimers()
    try {
      const input = screen.getByLabelText('Cantidad de Coca Cola 1L')
      fireEvent.change(input, { target: { value: '2' } })

      fireEvent.change(screen.getByLabelText('Cliente'), { target: { value: String(otroCliente.id) } })
      await vi.advanceTimersByTimeAsync(50)

      expect(apiPostMock).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('una edición hecha mientras el cliente todavía carga no queda pendiente: cuando el cliente carga, la corrida no relacionada resuelve sin heredar la demora de esa edición', async () => {
    let resolverClientes: (pagina: PaginaDe<ClienteListado>) => void = () => {}
    const clientesPendientes = new Promise<PaginaDe<ClienteListado>>((resolve) => {
      resolverClientes = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/clientes') return clientesPendientes
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)

    const entrada = screen.getByLabelText('Código escaneado')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    expect(apiPostMock).not.toHaveBeenCalled()

    vi.useFakeTimers()
    try {
      const input = screen.getByLabelText('Cantidad de Coca Cola 1L')
      fireEvent.change(input, { target: { value: '2' } })
      expect(apiPostMock).not.toHaveBeenCalled()

      await act(async () => {
        resolverClientes({ items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 })
        await Promise.resolve()
        await Promise.resolve()
      })

      await vi.advanceTimersByTimeAsync(50)
      expect(apiPostMock).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('Pos — limpieza de ediciones en curso', () => {
  it('quitar una línea con una edición pendiente no deja un override fantasma para un artículo agregado de nuevo', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    const input = screen.getByLabelText('Cantidad de Coca Cola 1L') as HTMLInputElement
    fireEvent.change(input, { target: { value: '0.0001' } })
    expect(input.value).toBe('0.0001')

    await userEvent.click(screen.getByRole('button', { name: 'Quitar' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(1)
  })

  it('vaciar el carrito con una edición pendiente no deja overrides fantasma para las líneas siguientes', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    const input = screen.getByLabelText('Cantidad de Coca Cola 1L') as HTMLInputElement
    fireEvent.change(input, { target: { value: '0.0001' } })
    expect(input.value).toBe('0.0001')

    await userEvent.click(screen.getByRole('button', { name: 'Vaciar carrito' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(1)
  })

  it('un escaneo que suma sobre una línea existente descarta el override de edición pendiente de esa fila', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')

    const input = screen.getByLabelText('Cantidad de Coca Cola 1L') as HTMLInputElement
    fireEvent.change(input, { target: { value: '0.0001' } })
    expect(input.value).toBe('0.0001')

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)

    await waitFor(() => expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(2))
  })
})
