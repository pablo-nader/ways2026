import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Pos } from './Pos'
import { ErrorApi } from '../api/cliente'
import type { ArticuloEscaneado, ClienteListado, PaginaDe, PuntoVentaListado, ResultadoDeResolucion } from '../api/tipos'

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
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
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

describe('Pos — checkout stubbed', () => {
  it('el botón Cobrar está deshabilitado (checkout se completa en la próxima entrega)', async () => {
    render(<Pos />)
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
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

  it('tipear "1." conserva el punto decimal visible y no dispara ninguna mutación de cantidad', async () => {
    const input = await agregarLineaCocaCola()
    const llamadasPrevias = apiPostMock.mock.calls.length

    escribirValorCrudo(input, '1.')

    expect(input.value).toBe('1.')
    expect(apiPostMock.mock.calls.length).toBe(llamadasPrevias)
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
