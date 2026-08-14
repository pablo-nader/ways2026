import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Pos } from './Pos'
import { ErrorApi } from '../api/cliente'
import type {
  ArticuloEscaneado,
  ClienteListado,
  ComprobanteEmitido,
  LoteListado,
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
        idLote: null,
        codigoLote: null,
        loteVencido: false,
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

/** Rutas GET comunes a casi todos los tests — devuelve `undefined` (no `Promise`) para una ruta
 * que no reconoce, así un test puede extender la tabla sin duplicarla entera (mismo criterio que
 * `mockearReferencia` en CompraEditor.test.tsx). */
function rutaBaseDePos(ruta: string, puntosVenta: PuntoVentaListado[] = [puntoVentaCentro]): Promise<unknown> | undefined {
  if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>(puntosVenta)
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
  // stage-12-lotes-vencimientos (Slice 14): sin lotes por defecto — el camino feliz de la
  // mayoría de los tests nunca abre el picker, pero cualquier click accidental no debe romper.
  if (ruta.startsWith('/stock/lotes?')) return Promise.resolve<LoteListado[]>([])
  return undefined
}

function mockearApiGet(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta) ?? rutaBaseDePos(ruta)
    if (propia) return propia
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

describe('Pos — formato de moneda negativa (regresión, INFO recurrente desde slice 6)', () => {
  it('un total negativo en el ticket antepone el signo al símbolo ($): "-$50,00", nunca "$-50,00"', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') {
        return Promise.resolve(
          comprobanteEmitidoFixture({ subtotal: -50, descuentoTotal: 0, total: -50 }),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText(/Total: -\$50,00/)).toBeInTheDocument()
  })
})

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
    let cantidadDeResoluciones = 0
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        cantidadDeResoluciones += 1
        return Promise.reject(new Error('falló la resolución'))
      }
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

    // decisión de diseño 3: el servidor es la autoridad final del total — una vista previa
    // fallida no bloquea el checkout (solo la resolución en vuelo lo hace), alcanza con la
    // sanidad mínima de tener una fila de pago con importe.
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

    const llamadasPrevias = cantidadDeResoluciones
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))
    await waitFor(() => expect(cantidadDeResoluciones).toBe(llamadasPrevias + 1))

    await userEvent.click(screen.getByRole('button', { name: 'Quitar' }))
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })

  it('con la vista previa fallida, Cobrar envía el importe tipeado y vuelto 0 (nunca el importe tendido, judgment-day R3 CRITICAL)', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.reject(new Error('falló la resolución'))
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    await screen.findByText('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '500')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

    // Sin total confiable, el vuelto sugerido no puede ser el importe tendido completo — el
    // cajero no tocó el campo de vuelto, tiene que seguir mostrando 0.
    expect(screen.getByLabelText('Vuelto de Efectivo (fila 1)')).toHaveValue(0)

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await waitFor(() => expect(apiPostMock.mock.calls.some((llamada) => llamada[0] === '/ventas')).toBe(true))
    const llamadaVentas = apiPostMock.mock.calls.find((llamada) => llamada[0] === '/ventas')
    const solicitud = llamadaVentas?.[1] as {
      lineas: { idArticulo: number; cantidad: number; codigoBarra: string | null; idLote: number | null }[]
      pagos: { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }[]
    }

    expect(solicitud.lineas).toEqual([{ idArticulo: 1, cantidad: 1, codigoBarra: '7790001234567', idLote: null }])
    expect(solicitud.pagos).toEqual([{ idMedioPago: medioEfectivo.id, importe: 500, referencia: null, vuelto: 0 }])
  })

  it('regresión: una resolución exitosa seguida de una que rechaza no deja precios stale en el subtotal (judgment-day R4, purga de `precios` en el catch)', async () => {
    let cantidadDeResoluciones = 0
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        cantidadDeResoluciones += 1
        if (cantidadDeResoluciones === 1) {
          const resultados: ResultadoDeResolucion[] = [
            { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
          ]
          return Promise.resolve(resultados)
        }
        return Promise.reject(new Error('falló la resolución'))
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
    await waitFor(() => expect(screen.getByText('$100,00', { selector: 'strong' })).toBeInTheDocument())

    // Segunda mutación (re-escaneo, suma cantidad sobre la misma línea): dispara una segunda
    // resolución que esta vez rechaza.
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await waitFor(() => expect(cantidadDeResoluciones).toBe(2))

    expect(
      await screen.findByText('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.'),
    ).toBeInTheDocument()

    const filaTotalPrevio = screen.getByText('Total previo').closest('div') as HTMLElement
    await waitFor(() => expect(within(filaTotalPrevio).getByText('—')).toBeInTheDocument())
    expect(screen.queryByText('$200,00', { selector: 'strong' })).not.toBeInTheDocument()
    expect(screen.getAllByText('se confirma al cobrar')).toHaveLength(2)
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

  it('un texto de escaneo sin confirmar no sobrevive a la venta siguiente', async () => {
    await armarVentaLista()
    await userEvent.type(screen.getByLabelText('Código escaneado'), '111222333')
    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'texto sin buscar')

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nueva venta' }))
    expect(screen.getByLabelText('Código escaneado')).toHaveValue('')
    expect(screen.getByLabelText('Buscar cliente')).toHaveValue('')
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

describe('Pos — gate seam de turno de caja (stage-6-turnos-caja, Slice 7)', () => {
  it('un 409 turno_no_abierto reemplaza el panel de cobro por la oferta de abrir turno, y tras abrirlo la venta NO se reintenta sola', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('No hay un turno abierto')).toBeInTheDocument()
    expect(screen.queryByText('No hay un turno abierto en este punto de venta.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Cobrar/ })).not.toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ventas')).toHaveLength(1)

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') {
        return Promise.resolve({
          id: 900,
          idPuntoVenta: 7,
          idEmpleadoApertura: 3,
          idEmpleadoCierre: null,
          fechaApertura: '2026-08-04T12:00:00Z',
          fechaCierre: null,
          fondoInicial: 500,
          estado: 'Abierto',
          observaciones: null,
        })
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    // El carrito y el panel de pagos quedan tal cual: el cajero vuelve a apretar "Cobrar" a
    // mano, ningún checkout NUEVO se dispara automáticamente al volver (sigue habiendo un único
    // POST /ventas acumulado, el del intento original que rebotó con el 409).
    await screen.findByRole('button', { name: /Cobrar/ })
    expect(screen.getByText('Coca Cola 1L')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ventas')).toHaveLength(1)
  })

  it('un fondo inicial negativo en el panel del gate se rechaza localmente, sin disparar el POST de apertura', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('No hay un turno abierto')

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '-10')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    expect(await screen.findByText('El fondo inicial tiene que ser un número mayor o igual a 0.')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/caja/turnos')).toHaveLength(0)
  })

  it('un 409 con otro código sigue mostrando el error normal, sin activar el gate', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'stock_insuficiente', 'No hay stock suficiente.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('No hay stock suficiente.')).toBeInTheDocument()
    expect(screen.queryByText('No hay un turno abierto')).not.toBeInTheDocument()
  })

  it('el panel del gate se autocura si la apertura rechaza con turno_ya_abierto (otra pestaña/cajero ganó la carrera): el gate se cierra, el carrito queda intacto y "Cobrar" no se reintenta solo', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('No hay un turno abierto')
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ventas')).toHaveLength(1)

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') {
        return Promise.reject(new ErrorApi(409, 'turno_ya_abierto', 'Ya hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    // El gate se cierra igual que en el camino feliz (mismo `onAbierto`): el carrito sigue
    // intacto y NINGÚN checkout nuevo se dispara solo — sigue habiendo un único POST /ventas
    // acumulado, el del intento original que rebotó con el 409.
    await screen.findByRole('button', { name: /Cobrar/ })
    expect(screen.getByText('Coca Cola 1L')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/ventas')).toHaveLength(1)
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

describe('Pos — picker de lote (stage-12-lotes-vencimientos, Slice 14, design decisión 19)', () => {
  function loteFixture(sobrescribir: Partial<LoteListado> = {}): LoteListado {
    return {
      idLote: 1,
      idArticulo: 1,
      codigo: '2026-09-01',
      fechaVencimiento: '2026-09-01',
      esSinIdentificar: false,
      cantidad: 5,
      estado: 'Vigente',
      sugerido: false,
      ...sobrescribir,
    }
  }

  /** Deja el carrito con una sola línea de Coca Cola, sin pasar por el panel de pagos — punto de
   * partida de los tests del picker. */
  async function armarCarritoConUnaLinea() {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
  }

  it('el camino feliz nunca pide los lotes — el botón "Elegir lote" no dispara ningún fetch solo por aparecer', async () => {
    await armarCarritoConUnaLinea()

    expect(screen.getByRole('button', { name: 'Elegir lote' })).toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((call: unknown[]) => (call[0] as string).startsWith('/stock/lotes?'))).toBe(false)
  })

  it('preselecciona (resalta) el lote sugerido que manda el servidor — nunca lo recalcula', async () => {
    // El sugerido va en el MEDIO de la lista, ni primero ni último: si "elegir el sugerido" y
    // "elegir el último/primero" fueran indistinguibles, esta aserción no lo detectaría (judgment-day
    // slice 14, MAJOR 2a — mutante "elegir el último" debe quedar RED contra este fixture).
    mockearApiGet((ruta) =>
      ruta.startsWith('/stock/lotes?')
        ? Promise.resolve<LoteListado[]>([
            loteFixture({ idLote: 1, codigo: '2026-09-01' }),
            loteFixture({ idLote: 2, codigo: '2026-10-01', sugerido: true }),
            loteFixture({ idLote: 3, codigo: '2026-11-01' }),
          ])
        : undefined,
    )
    await armarCarritoConUnaLinea()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir lote' }))

    expect(await screen.findByLabelText('Lote de Coca Cola 1L')).toHaveValue('2')
  })

  it('una lista de lotes vacía muestra el aviso de FEFO automático, sin picker', async () => {
    mockearApiGet((ruta) => (ruta.startsWith('/stock/lotes?') ? Promise.resolve<LoteListado[]>([]) : undefined))
    await armarCarritoConUnaLinea()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir lote' }))

    expect(await screen.findByText('Sin lotes registrados — FEFO automático.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Lote de Coca Cola 1L')).not.toBeInTheDocument()
  })

  it('doble click en "Elegir lote" dispara exactamente un fetch (el `disabled` nativo del botón es la defensa, no un ref)', async () => {
    let resolverLotes: (valor: LoteListado[]) => void = () => {}
    mockearApiGet((ruta) =>
      ruta.startsWith('/stock/lotes?') ? new Promise((resolve) => (resolverLotes = resolve)) : undefined,
    )
    await armarCarritoConUnaLinea()

    const boton = screen.getByRole('button', { name: 'Elegir lote' })
    // `fireEvent.click` (a diferencia de `userEvent.click` con `await` de por medio) dispara los
    // dos clicks en el mismo tick — pero el segundo no hace nada: React ya marcó el botón
    // `disabled` en el primer render posterior al `setCargando(true)`, y ni JSDOM ni un navegador
    // real despachan `click` sobre un elemento disabled. No hay guard de reentrancia por ref: el
    // único fetch que se prueba acá es consecuencia del atributo nativo.
    fireEvent.click(boton)
    fireEvent.click(boton)

    resolverLotes([loteFixture()])
    await screen.findByLabelText('Lote de Coca Cola 1L')

    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/stock/lotes?'))
    expect(llamadas).toHaveLength(1)
  })

  it('una respuesta stale que llega DESPUÉS de cambiar de punto de venta no pinta el picker (mutation-proof-tests regla 7)', async () => {
    const puntoVentaSucursal = puntoVentaFixture({ id: 8, nombre: 'Sucursal Norte' })
    let resolverLotesPv1: (valor: LoteListado[]) => void = () => {}
    let promesaLotesPv1: Promise<LoteListado[]> = Promise.resolve([])
    mockearApiGet((ruta) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro, puntoVentaSucursal])
      if (ruta.startsWith('/stock/lotes?') && ruta.includes(`idPuntoVenta=${puntoVentaCentro.id}`)) {
        promesaLotesPv1 = new Promise((resolve) => (resolverLotesPv1 = resolve))
        return promesaLotesPv1
      }
      if (ruta.startsWith('/stock/lotes?')) return Promise.resolve<LoteListado[]>([])
      return undefined
    })
    await armarCarritoConUnaLinea()
    await waitFor(() => expect(screen.getByLabelText('Punto de venta')).toHaveValue(String(puntoVentaCentro.id)))

    await userEvent.click(screen.getByRole('button', { name: 'Elegir lote' }))
    // el fetch de PV1 queda en vuelo — `resolverLotesPv1` todavía no se llamó.

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), String(puntoVentaSucursal.id))

    // regla 7: el flush del microtask va DENTRO de `act` — un `waitFor` pasaría en su primer
    // tick, antes de que el `.then` stale aterrice, y saldría verde sin probar nada.
    await act(async () => {
      resolverLotesPv1([loteFixture({ idLote: 99, codigo: 'STALE' })])
      await promesaLotesPv1
    })

    expect(screen.getByRole('button', { name: 'Elegir lote' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Lote de Coca Cola 1L')).not.toBeInTheDocument()
  })

  it('un fetch de lotes rechazado muestra "No se pudieron cargar los lotes."', async () => {
    mockearApiGet((ruta) => (ruta.startsWith('/stock/lotes?') ? Promise.reject(new Error('boom')) : undefined))
    await armarCarritoConUnaLinea()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir lote' }))

    expect(await screen.findByText('No se pudieron cargar los lotes.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Lote de Coca Cola 1L')).not.toBeInTheDocument()
  })
})

describe('Pos — ticket: warning de lote vencido (design decisión 12: "Expired Lot Sale Warns, Never Blocks")', () => {
  it('un item emitido con loteVencido: true muestra el warning "⚠ Lote vencido" en el ticket', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') {
        return Promise.resolve(
          comprobanteEmitidoFixture({
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
                idLote: 2,
                codigoLote: '2026-01-01',
                loteVencido: true,
              },
            ],
          }),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    const fila = screen.getByText('Coca Cola 1L').closest('tr') as HTMLElement
    expect(within(fila).getByText('⚠ Lote vencido')).toBeInTheDocument()
  })

  it('un item emitido con loteVencido: false no muestra el warning', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    const fila = screen.getByText('Coca Cola 1L').closest('tr') as HTMLElement
    expect(within(fila).queryByText('⚠ Lote vencido')).not.toBeInTheDocument()
  })
})
