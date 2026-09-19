import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Pos } from './Pos'
import { ErrorApi } from '../api/cliente'
import type {
  ArticuloEscaneado,
  ArticuloListado,
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoListado,
  PaginaDe,
  ParametroResuelto,
  PresupuestoParaVenta,
  PuntoVentaListado,
  ResultadoDeResolucion,
  TurnoResumen,
} from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

/** `Pos()` (stage-17-presupuestos-y-remitos, Slice 7) lee `useSearchParams` — necesita un Router
 * para montar, mismo criterio que `OrdenDeCompra.test.tsx`/`CompraEditor.test.tsx`. `ruta` por
 * defecto es la libre (`/pos`, sin `?idPresupuesto=`) para que los tests preexistentes no
 * cambien de comportamiento. El árbol se expone aparte para poder `rerender` el mismo árbol
 * después de mutar el punto de venta de la sesión. */
/** Placeholder de `/caja/cierre` — solo para verificar QUE `Pos.tsx` navegó ahí con el `idTurno`
 * correcto en el query string (el propio `CierreDeCaja` ya tiene sus tests en
 * `CierreDeCaja.test.tsx`). */
function PlaceholderDeCierreDeCaja() {
  const [searchParams] = useSearchParams()
  return <div>Cierre de turno {searchParams.get('idTurno')}</div>
}

function arbolDePos(ruta = '/pos') {
  return (
    <MemoryRouter initialEntries={[ruta]}>
      <Routes>
        <Route path="/pos" element={<Pos />} />
        <Route path="/presupuestos" element={<div>Presupuestos</div>} />
        <Route path="/caja/cierre" element={<PlaceholderDeCierreDeCaja />} />
      </Routes>
    </MemoryRouter>
  )
}

function renderPos(ruta = '/pos') {
  return render(arbolDePos(ruta))
}

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
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
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

function articuloListadoFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 9,
    codigoInterno: 'A0009',
    nombre: 'Fanta 1.5L',
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
    ...sobrescribir,
  }
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
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function turnoAbiertoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 900,
    idPuntoVenta: 7,
    idEmpleadoApertura: 3,
    idEmpleadoCierre: null,
    fechaApertura: '2026-08-04T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 500,
    estado: 'Abierto',
    observaciones: null,
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

/** Simula un código leído por pistola (o tipeado a mano) con el modal "Venta finalizada" abierto
 * — ningún input de texto tiene foco ahí (el foco está en "Aceptar"), así que el buffer del modal
 * (`Pos.tsx`, `VentaFinalizada`) es lo único que puede juntar estos caracteres. Un `keydown` por
 * carácter, sin `Enter` — el llamador lo agrega aparte para poder afirmar el estado justo antes. */
function tipearEnDialogo(dialogo: HTMLElement, texto: string) {
  for (const caracter of texto) {
    fireEvent.keyDown(dialogo, { key: caracter })
  }
}

const consumidorFinal = clienteFixture()
const otroCliente = clienteFixture({ id: 2, numero: 2, nombre: 'Juan', apellido: 'Pérez', esConsumidorFinal: false })
const puntoVentaCentro = puntoVentaFixture()

/** El punto de venta viene de la sesión (`PuertaDePuntoVenta`), no de un fetch de esta pantalla:
 * el hook se mockea con un estado mutable que cada test puede reemplazar antes de montar (o
 * mutar y `rerender` para simular un cambio de punto de venta). */
function estadoDePuntoVentaPorDefecto(): EstadoDePuntoVenta {
  return {
    puntosVenta: [puntoVentaCentro],
    puntoVenta: puntoVentaCentro,
    elegir: vi.fn(),
    recargar: vi.fn(() => Promise.resolve()),
  }
}

let estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
}))

/** Rutas GET comunes a casi todos los tests — devuelve `undefined` (no `Promise`) para una ruta
 * que no reconoce, así un test puede extender la tabla sin duplicarla entera (mismo criterio que
 * `mockearReferencia` en CompraEditor.test.tsx). */
function rutaBaseDePos(ruta: string): Promise<unknown> | undefined {
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
  // stage-pos-turno-y-foco: turno ABIERTO por defecto — así ningún test preexistente (que nunca
  // pensó en el turno) se ve afectado por el nuevo bloqueo de venta libre; los tests dedicados al
  // turno cerrado sobrescriben esta ruta explícitamente.
  if (ruta.startsWith('/caja/turnos/abierto')) {
    return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
  }
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
  estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()
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

/** Deja el carrito con una línea de Coca Cola ($ 100, sin descuento) y el panel de pagos listo:
 * medio Efectivo elegido, importe = total. Punto de partida de los tests de checkout. */
async function armarVentaLista() {
  renderPos()
  await screen.findByRole('option', { name: /Consumidor Final/ })

  await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Coca Cola 1L')
  await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

  await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
  const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
  await userEvent.type(importe, '100')

  await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
}

/** Deja el carrito con una sola línea de Coca Cola, sin pasar por el panel de pagos — punto de
 * partida de los tests de remount por punto de venta. */
async function armarCarritoConUnaLinea() {
  const resultado = renderPos()
  await screen.findByRole('option', { name: /Consumidor Final/ })
  await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
  await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByText('Coca Cola 1L')
  return resultado
}

describe('Pos — formato de moneda negativa (regresión, INFO recurrente desde slice 6)', () => {
  it('un total negativo en el ticket antepone el signo al símbolo ($): "-$ 50,00", nunca "$ -50,00"', async () => {
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

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.getByText(/Total: -\$ 50,00/)).toBeInTheDocument()
  })
})

describe('Pos — carga inicial', () => {
  it('selecciona el Consumidor Final por defecto', async () => {
    renderPos()

    expect(await screen.findByRole('option', { name: /Consumidor Final/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Cliente')).toHaveValue(String(consumidorFinal.id))
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()
  })
})

describe('Pos — franja de caja en la app web (stage-pos-caja-en-cabecera)', () => {
  it('la franja (arriba del carrito) muestra el estado de caja; "Datos de la venta" ya no tiene la línea de punto de venta ni el bloque de turno', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja abierta')

    expect(screen.queryByText('Punto de venta:')).not.toBeInTheDocument()

    const panelDatos = screen.getByText('Datos de la venta').closest('.box') as HTMLElement
    expect(within(panelDatos).queryByText('Caja abierta')).not.toBeInTheDocument()
    expect(within(panelDatos).queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()

    // La franja vive en un contenedor propio, antes del carrito ("Datos de la venta" no la tiene).
    const franja = screen.getByText('Caja abierta').closest('.container-fluid') as HTMLElement
    expect(within(franja).getByRole('button', { name: 'Cerrar caja' })).toBeInTheDocument()
    expect(franja).not.toBe(panelDatos)
  })

  it('con la caja cerrada, la franja muestra "Abrir caja" y el aviso del carrito sigue apareciendo', async () => {
    mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(null) : undefined))
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(await screen.findByText('Caja cerrada')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abrir caja' })).toBeInTheDocument()
    expect(screen.getByText('Caja cerrada: abrí la caja para vender.')).toBeInTheDocument()
  })
})

describe('Pos — punto de venta de sesión (react-async-state regla 8)', () => {
  /**
   * Cláusula bajo prueba: la mitad `:${puntoVenta?.id}` del `key` de `PantallaPos` en `Pos()`.
   * El nombre nuevo se pintaría igual sin ella (la pantalla lee el contexto directo), así que la
   * aserción discriminante es el carrito vacío. Evidencia de mutación (mutation-proof-tests regla
   * 2): con el `key` reducido a `idPresupuesto ?? 'libre'`, este test falla ("Coca Cola 1L" sigue
   * en el carrito); restaurado, vuelve a verde.
   */
  it('un cambio del punto de venta de sesión remonta la pantalla: el carrito se vacía', async () => {
    const puntoVentaNorte = puntoVentaFixture({ id: 8, nombre: 'Sucursal Norte' })
    estadoDePuntoVenta.puntosVenta = [puntoVentaCentro, puntoVentaNorte]
    const { rerender } = await armarCarritoConUnaLinea()
    expect(screen.getByText('Coca Cola 1L')).toBeInTheDocument()

    estadoDePuntoVenta.puntoVenta = puntoVentaNorte
    rerender(arbolDePos())

    expect(screen.queryByText('Coca Cola 1L')).not.toBeInTheDocument()
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()

    // La instancia nueva vuelve a cargar clientes y parámetros contra el punto de venta 8.
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await waitFor(() =>
      expect(apiGetMock).toHaveBeenCalledWith('/parametros/tolerancia_pago?idEmpresa=1&idPuntoVenta=8'),
    )
  })

  it('sin punto de venta en la sesión muestra el aviso y Cobrar queda deshabilitado', async () => {
    estadoDePuntoVenta.puntosVenta = []
    estadoDePuntoVenta.puntoVenta = null
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(screen.getByText('Sin puntos de venta disponibles')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Elegir lote' })).not.toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalledWith(expect.stringContaining('/parametros/'))
  })
})

describe('Pos — escaneo', () => {
  it('escanear un código agrega una línea al carrito', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(await screen.findByText('Coca Cola 1L')).toBeInTheDocument()
    expect(screen.getByLabelText('Cantidad de Coca Cola 1L')).toHaveValue(1)
  })

  it('re-escanear el mismo código suma la cantidad en la misma línea, no duplica', async () => {
    renderPos()
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
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    await screen.findByText('Coca Cola 1L')
    expect(entrada).toHaveValue('')
  })

  it('un código no encontrado muestra un error y no agrega ninguna línea', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes') {
        const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=')) {
        return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No se encontró un artículo activo para el código 999.'))
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '999')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(await screen.findByText('No se encontró un artículo activo para el código 999.')).toBeInTheDocument()
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
  })
})

describe('Pos — selector de cliente', () => {
  it('buscar y elegir otro cliente lo deja seleccionado', async () => {
    renderPos()
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
      if (ruta === '/clientes') return montajePendiente
      if (ruta.startsWith('/clientes?busqueda=')) {
        const pagina: PaginaDe<ClienteListado> = { items: [otroCliente], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()

    // `/clientes` (montaje) queda pendiente a propósito — se espera al turno (independiente)
    // en vez de la opción "Consumidor Final" para saber que el input ya está habilitado.
    await waitFor(() => expect(screen.getByLabelText('Buscar cliente')).toBeEnabled())
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

    renderPos()
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
    await waitFor(() => expect(within(fila).getByText('$ 120,00')).toBeInTheDocument())
    expect(within(fila).getByText('$ 100,00')).toBeInTheDocument()
    expect(within(fila).getByText('$ 200,00')).toBeInTheDocument()
    expect(within(fila).getByText('2x1 Gaseosas')).toBeInTheDocument()

    await waitFor(() => expect(screen.getByText('$ 200,00', { selector: 'strong' })).toBeInTheDocument())
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

    renderPos()
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

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    await screen.findByText('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '500')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

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

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const entrada = screen.getByLabelText('Código escaneado')
    const boton = screen.getByRole('button', { name: 'Agregar' })

    await userEvent.type(entrada, '7790001234567')
    await userEvent.click(boton)
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

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
    expect(screen.queryByText('$ 200,00', { selector: 'strong' })).not.toBeInTheDocument()
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

    renderPos()
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
    await waitFor(() => expect(within(fila).getByText('$ 90,00')).toBeInTheDocument())

    await act(async () => {
      resolverPrimera([{ idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }])
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(within(fila).getByText('$ 90,00')).toBeInTheDocument()
  })
})

describe('Pos — panel de pagos: precondiciones', () => {
  it('Cobrar está deshabilitado con el carrito vacío', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })

  it('medios de pago o parámetros que fallan al cargar dejan Cobrar deshabilitado con un aviso legible', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes') {
        const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
      if (ruta === '/catalogos/medios-pago') {
        return Promise.reject(new ErrorApi(500, 'error', 'No se pudieron cargar los medios de pago.'))
      }
      if (ruta.startsWith('/parametros/')) return Promise.resolve<ParametroResuelto>({ clave: 'x', valor: '10' })
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
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

    renderPos()
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

    renderPos()
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
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioEfectivo.nombre })

    expect(screen.queryByRole('option', { name: medioCuentaCorriente.nombre })).not.toBeInTheDocument()
  })

  it('cuenta corriente aparece como opción para un cliente que no es Consumidor Final', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'perez')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
    const opcionJuan = await screen.findByRole('option', { name: /Juan Pérez/ })
    await userEvent.selectOptions(screen.getByLabelText('Cliente'), opcionJuan)

    expect(await screen.findByRole('option', { name: medioCuentaCorriente.nombre })).toBeInTheDocument()
  })

  it('no hay ningún input de vuelto en el panel de pagos (etapa 2: se saca el override manual)', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioEfectivo.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)

    expect(screen.queryByLabelText(/^Vuelto de/)).not.toBeInTheDocument()
  })
})

describe('Pos — panel de pagos: referencia solo para el medio que la requiere', () => {
  it('la referencia queda oculta para un medio que no la requiere (Efectivo)', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioEfectivo.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)

    expect(screen.queryByLabelText(`Referencia de ${medioEfectivo.nombre} (fila 1)`)).not.toBeInTheDocument()
  })

  it('la referencia aparece para el medio que la requiere (Tarjeta), con el placeholder "requerida", y el checkout la envía', async () => {
    await armarCarritoConUnaLinea()
    await screen.findByRole('option', { name: medioTarjeta.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioTarjeta.nombre)
    expect(screen.queryByLabelText(`Referencia de ${medioEfectivo.nombre} (fila 1)`)).not.toBeInTheDocument()

    const referencia = await screen.findByLabelText(`Referencia de ${medioTarjeta.nombre} (fila 1)`)
    expect(referencia).toHaveAttribute('placeholder', 'Referencia (requerida)')
    await userEvent.type(referencia, 'auth-999')

    await userEvent.type(await screen.findByLabelText(`Importe de ${medioTarjeta.nombre} (fila 1)`), '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await waitFor(() => expect(apiPostMock.mock.calls.some((llamada) => llamada[0] === '/ventas')).toBe(true))
    const llamadaVentas = apiPostMock.mock.calls.find((llamada) => llamada[0] === '/ventas')
    const solicitud = llamadaVentas?.[1] as {
      pagos: { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }[]
    }

    expect(solicitud.pagos).toEqual([{ idMedioPago: medioTarjeta.id, importe: 100, referencia: 'auth-999', vuelto: 0 }])
  })

  it('sin cargar la referencia requerida, la validación local rechaza el cobro (Cobrar sigue deshabilitado)', async () => {
    await armarCarritoConUnaLinea()
    await screen.findByRole('option', { name: medioTarjeta.nombre })

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioTarjeta.nombre)
    await userEvent.type(await screen.findByLabelText(`Importe de ${medioTarjeta.nombre} (fila 1)`), '100')

    await waitFor(() =>
      expect(screen.getByText('Este medio de pago requiere una referencia.')).toBeInTheDocument(),
    )
    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })
})

describe('Pos — checkout', () => {
  it('un cobro exitoso resetea el carrito de inmediato y muestra el modal "Venta finalizada" con el total — sin esperar ningún click', async () => {
    await armarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.getByText('Venta 0007-00000001')).toBeInTheDocument()
    expect(modal.getByText('Total: $ 100,00')).toBeInTheDocument()

    // El carrito ya se reseteó ANTES de que el cajero cierre el modal — a diferencia de la vieja
    // pantalla de resumen, no hace falta ningún "Nueva venta" para volver a vender.
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()

    await userEvent.click(modal.getByRole('button', { name: 'Aceptar' }))
    expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
  })

  it('el modal "Venta finalizada" muestra cada medio de pago aplicado con su importe y el vuelto a entregar — desde la respuesta del servidor, nunca de lo que el cajero tipeó localmente', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') {
        // A propósito, distinto de lo que arma `armarVentaLista()` en el panel de pagos local
        // (una sola fila de Efectivo por $ 100) — si el modal leyera de `pagosConVuelto` local en
        // vez de `emitido.pagos`, este test lo detectaría.
        return Promise.resolve(
          comprobanteEmitidoFixture({
            total: 150,
            pagos: [
              { idMedioPago: medioEfectivo.id, importe: 100, referencia: null, vuelto: 20 },
              { idMedioPago: medioTarjeta.id, importe: 70, referencia: 'AUT123', vuelto: 0 },
            ],
          }),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.getByText('Total: $ 150,00')).toBeInTheDocument()
    expect(modal.getByText('Efectivo: $ 100,00')).toBeInTheDocument()
    expect(modal.getByText('Tarjeta: $ 70,00')).toBeInTheDocument()
    expect(modal.getByText('Vuelto: $ 20,00')).toBeInTheDocument()
  })

  it('el modal "Venta finalizada" cierra con F9 y devuelve el foco al input de código', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

    fireEvent.keyDown(document, { key: 'F9' })

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
    expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: `evento.repeat` en la rama de `ventaFinalizada` del listener de F9 — un
   * F9 sostenido desde la propia confirmación de cobro (F9 que abrió "¿Finalizar venta?" y sigue
   * físicamente apretado) no debe cerrar este modal por accidente apenas aparece. Mutación
   * aplicada manualmente: sacar el `if (evento.repeat) return` de esa rama en `Pos.tsx` → este
   * test pasa a rojo (el modal se cierra con el keydown repetido). Revertido, vuelve a verde —
   * evidencia registrada en el informe de la tarea.
   */
  it('un F9 con repeat:true no cierra el modal "Venta finalizada"', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

    fireEvent.keyDown(document, { key: 'F9', repeat: true })

    expect(screen.getByRole('dialog', { name: 'Venta finalizada' })).toBeInTheDocument()
  })

  it('mientras el modal "Venta finalizada" está abierto, F2 no abre el buscador y los controles de venta quedan deshabilitados', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

    fireEvent.keyDown(document, { key: 'F2' })
    expect(screen.queryByRole('dialog', { name: 'Buscar artículo' })).not.toBeInTheDocument()

    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })

  it('mientras el modal "Venta finalizada" está abierto, Tab no escapa del botón "Aceptar" (trampa de foco)', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
    const aceptar = within(dialogo).getByRole('button', { name: 'Aceptar' })

    expect(aceptar).toHaveFocus()
    fireEvent.keyDown(dialogo, { key: 'Tab' })
    expect(aceptar).toHaveFocus()
  })

  describe('buffer de escaneo mientras el modal "Venta finalizada" está abierto (judgment-day ronda 0)', () => {
    it('Enter con el buffer vacío simplemente cierra el modal y devuelve el foco al input de código — sin disparar ningún escaneo', async () => {
      await armarVentaLista()
      await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
      // `armarVentaLista()` ya disparó su propio `GET /articulos/escaneo` para armar el carrito —
      // se limpia acá para que la aserción de abajo mida solo lo que pasa DESPUÉS de este punto.
      apiGetMock.mockClear()

      fireEvent.keyDown(dialogo, { key: 'Enter' })

      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
      expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
      expect(apiGetMock.mock.calls.some(([ruta]) => String(ruta).startsWith('/articulos/escaneo'))).toBe(false)
    })

    /**
     * Cláusula bajo prueba: `if (!modoPresupuesto && codigo) onEscanearCodigo(codigo)` en la rama
     * de Enter de `manejarTeclado` (`Pos.tsx`, `VentaFinalizada`) — un código tipeado/escaneado
     * con el modal abierto debe terminar escaneado a la venta nueva, nunca perdido. Mutación
     * aplicada manualmente: comentar ese `if` (dejar solo `onCerrar()`) → este test pasa a rojo
     * (cierra el modal pero no llega ningún `GET /articulos/escaneo`, "Coca Cola 1L" nunca
     * aparece en el carrito). Revertido, vuelve a verde — evidencia registrada en el informe de
     * la tarea.
     */
    it('tipear/escanear un código con el modal abierto y apretar Enter cierra el modal y escanea ese código en la venta nueva — una sola request', async () => {
      await armarVentaLista()
      await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
      apiGetMock.mockClear()

      tipearEnDialogo(dialogo, '7790001234567')
      fireEvent.keyDown(dialogo, { key: 'Enter' })

      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
      await screen.findByText('Coca Cola 1L')
      expect(apiGetMock).toHaveBeenCalledWith('/articulos/escaneo?entrada=7790001234567')
      expect(apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/articulos/escaneo'))).toHaveLength(1)
    })

    it('un código con formato N*codigo tipeado en el buffer viaja tal cual (sin interpretarlo acá)', async () => {
      await armarVentaLista()
      await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })

      tipearEnDialogo(dialogo, '3*7790001234567')
      fireEvent.keyDown(dialogo, { key: 'Enter' })

      await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/articulos/escaneo?entrada=3*7790001234567'))
    })

    it('F9 con un código a medio tipear cierra el modal SIN escanearlo — el texto queda esperando en el input de código', async () => {
      await armarVentaLista()
      await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
      apiGetMock.mockClear()

      tipearEnDialogo(dialogo, '77900')
      fireEvent.keyDown(dialogo, { key: 'F9' })

      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
      expect(screen.getByLabelText('Código escaneado')).toHaveValue('77900')
      expect(apiGetMock.mock.calls.some(([ruta]) => String(ruta).startsWith('/articulos/escaneo'))).toBe(false)
    })

    it('clic en "Aceptar" con un código a medio tipear también lo deja esperando en el input de código, sin escanearlo', async () => {
      await armarVentaLista()
      await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
      const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
      apiGetMock.mockClear()

      tipearEnDialogo(dialogo, '123')
      await userEvent.click(within(dialogo).getByRole('button', { name: 'Aceptar' }))

      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
      expect(screen.getByLabelText('Código escaneado')).toHaveValue('123')
      expect(apiGetMock.mock.calls.some(([ruta]) => String(ruta).startsWith('/articulos/escaneo'))).toBe(false)
    })
  })

  /**
   * Cubre el guard `confirmandoCobro && !ventaFinalizada` de `Pos.tsx` (judgment-day ronda 0: sin
   * él, `confirmarYcobrar` deja una ventana — entre que `cobrar()` setea `ventaFinalizada` y que
   * la propia continuación de `confirmarYcobrar` vuelve `confirmandoCobro` a `false` — en la que
   * ambos estados están en `true` a la vez). Intentado (mutation-proof-tests regla 2): sacar el
   * `!ventaFinalizada` del guard NO tiñe este test de rojo — el batching automático de React 18
   * colapsa ambas actualizaciones de estado en un único commit antes de cualquier punto
   * observable en jsdom, así que la ventana transitoria que el guard previene no llega a
   * manifestarse acá (confound, regla 3: no hay forma encontrada de esquivarlo con un test
   * montado). Se conserva como cobertura estructural real del invariante (nunca coexisten en
   * ningún punto observado) en vez de una prueba de mutación — mismo criterio que el test de
   * `/para-venta` más abajo, que documenta explícitamente qué SÍ prueba.
   */
  it('la confirmación "¿Finalizar venta?" nunca coexiste en pantalla con el modal "Venta finalizada" (judgment-day ronda 0)', async () => {
    let resolverCheckout: (comprobante: ComprobanteEmitido) => void = () => {}
    const checkoutPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverCheckout = resolve
    })

    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return checkoutPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    fireEvent.keyDown(document, { key: 'F9' })
    await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })
    fireEvent.keyDown(document, { key: 'F9' })

    await act(async () => {
      resolverCheckout(comprobanteEmitidoFixture())
    })

    const hayConfirmacion = screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' }) !== null
    const hayVentaFinalizada = screen.queryByRole('dialog', { name: 'Venta finalizada' }) !== null
    // Nunca los dos a la vez, sea cual sea el estado en el que quedó esta corrida.
    expect(hayConfirmacion && hayVentaFinalizada).toBe(false)

    await waitFor(() => expect(screen.getByRole('dialog', { name: 'Venta finalizada' })).toBeInTheDocument())
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('el diálogo "¿Finalizar venta?" (F9) es un modal fuera del panel "Datos de la venta", no un bloque inline que lo agranda', async () => {
    await armarVentaLista()
    const panelDatosDeLaVenta = screen.getByText('Datos de la venta').closest('.box') as HTMLElement

    fireEvent.keyDown(document, { key: 'F9' })
    const dialogo = await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })

    expect(panelDatosDeLaVenta.contains(dialogo)).toBe(false)
    expect(within(panelDatosDeLaVenta).queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('un texto de escaneo sin confirmar no sobrevive al cobro — el carrito se resetea de inmediato, sin esperar el cierre del modal', async () => {
    await armarVentaLista()
    await userEvent.type(screen.getByLabelText('Código escaneado'), '111222333')
    await userEvent.type(screen.getByLabelText('Buscar cliente'), 'texto sin buscar')

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

    expect(screen.getByLabelText('Código escaneado')).toHaveValue('')
    expect(screen.getByLabelText('Buscar cliente')).toHaveValue('')
  })

  it('un cobro exitoso limpia el error de escaneo que haya quedado de antes de cobrar', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
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
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '999')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    expect(await screen.findByText('No se encontró un artículo activo para el código 999.')).toBeInTheDocument()

    await userEvent.clear(screen.getByLabelText('Código escaneado'))
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

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

  it('un fondo inicial negativo en el panel del gate es inalcanzable — CampoImporte descarta el signo "-" sin admiteNegativos', async () => {
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
    expect(screen.getByLabelText('Fondo inicial')).toHaveValue('10')
  })

  it('un fondo inicial vacío en el panel del gate se rechaza localmente, sin disparar el POST de apertura', async () => {
    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('No hay un turno abierto')

    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    expect(await screen.findByText('El fondo inicial es obligatorio.')).toBeInTheDocument()
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

  it('el 409 también refresca el badge de caja de la franja/header (deja de mostrar "Caja abierta" y vuelve a mostrarlo tras reabrir)', async () => {
    await armarVentaLista()
    expect(screen.getByText('Caja abierta')).toBeInTheDocument()

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') {
        return Promise.reject(new ErrorApi(409, 'turno_no_abierto', 'No hay un turno abierto en este punto de venta.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('No hay un turno abierto')

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return Promise.resolve(turnoAbiertoFixture({ id: 999 }))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    await screen.findByRole('button', { name: /Cobrar/ })
    expect(screen.getByText('Caja abierta')).toBeInTheDocument()
  })
})

describe('Pos — estado del turno del punto de venta (stage-pos-turno-y-foco)', () => {
  function mockearTurnoCerrado(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
    mockearApiGet((ruta) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(null)
      return sobrescribir?.(ruta)
    })
  }

  it('turno confirmado abierto: badge "Caja abierta" y acción "Cerrar caja"', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(await screen.findByText('Caja abierta')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cerrar caja' })).toBeInTheDocument()
    expect(screen.queryByText('Caja cerrada: abrí la caja para vender.')).not.toBeInTheDocument()
  })

  it('"Cerrar caja" navega a /caja/cierre con el idTurno real', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja abierta')

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

    // `Pos()` no expone el router acá: se verifica indirectamente por la ausencia de la pantalla
    // de venta y la aparición de la ruta declarada en `arbolDePos` para `/caja/cierre`.
    expect(await screen.findByText(`Cierre de turno ${turnoAbiertoFixture().id}`)).toBeInTheDocument()
  })

  it('turno confirmado cerrado: badge "Caja cerrada", aviso claro y solo la búsqueda de artículos queda operable', async () => {
    mockearTurnoCerrado()
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(await screen.findByText('Caja cerrada')).toBeInTheDocument()
    expect(screen.getByText('Caja cerrada: abrí la caja para vender.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abrir caja' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()

    // Escaneo/agregar y edición de carrito: deshabilitados.
    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    // Búsqueda de artículos: sigue operable (spec: "solo búsqueda/consulta de precio").
    expect(screen.getByRole('button', { name: 'Buscar artículo' })).toBeEnabled()
    // Cliente: deshabilitado.
    expect(screen.getByLabelText('Cliente')).toBeDisabled()
    expect(screen.getByLabelText('Buscar cliente')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    // Pagos: deshabilitados.
    expect(screen.getByLabelText('Medio de pago')).toBeDisabled()
    expect(screen.getByRole('button', { name: '+ Agregar medio de pago' })).toBeDisabled()
    // Cobrar: deshabilitado (ya lo estaría por precondiciones, pero el gate lo hace explícito).
    expect(screen.getByRole('button', { name: /Cobrar/ })).toBeDisabled()
  })

  it('con turno cerrado, el buscador de artículos sigue abriendo y mostrando precio, pero "Agregar" queda deshabilitado por fila', async () => {
    const fanta = articuloListadoFixture()
    mockearTurnoCerrado((ruta) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [fanta], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta)
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 9, idListaPrecio: 1, precioOriginal: 250, precioFinal: 200, descuentoUnitario: 50, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja cerrada')

    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Buscar artículo' }))
    fireEvent.change(dialogo.getByLabelText('Buscar artículo por nombre'), { target: { value: 'fa' } })
    fireEvent.keyDown(dialogo.getByLabelText('Buscar artículo por nombre'), { key: 'Enter' })

    expect(await dialogo.findByText('$ 200,00')).toBeInTheDocument()
    const botonAgregar = dialogo.getByRole('button', { name: 'Agregar' })
    expect(botonAgregar).toBeDisabled()

    fireEvent.click(botonAgregar)
    // El click en un botón deshabilitado no dispara el handler: el carrito sigue vacío (el modal
    // sigue mostrando "Fanta 1.5L" en su propia tabla de resultados, eso es esperado) y el modal
    // sigue abierto (nunca se cierra como sí pasaría con un "Agregar" real).
    expect(screen.getByText('Escaneá o tipeá un código para empezar la venta.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Buscar artículo' })).toBeInTheDocument()
  })

  it('"Abrir caja" (reutiliza PanelGateTurno) abre el turno y habilita todo sin recargar la página, con el input de código enfocado', async () => {
    mockearTurnoCerrado()
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja cerrada')
    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()

    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos') return Promise.resolve(turnoAbiertoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await userEvent.click(screen.getByRole('button', { name: 'Abrir caja' }))
    await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

    await screen.findByText('Caja abierta')
    expect(screen.queryByText('Caja cerrada: abrí la caja para vender.')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText('Código escaneado')).toBeEnabled())
    expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(screen.getByLabelText('Cliente')).toBeEnabled()
    expect(screen.getByLabelText('Medio de pago')).toBeEnabled()
  })

  /**
   * Cláusula bajo prueba: el conjunct `bloqueadoPorTurno` del guard del efecto de foco pendiente
   * (`if (escaneando || cobrando || buscadorAbierto || bloqueadoPorTurno) return`). Encontrado
   * reproduciendo en un navegador real (no en este test): la consulta de turno resuelve casi al
   * instante tras el mount, así que sin este conjunct el efecto consumía el pedido de foco inicial
   * (`focoPendienteRef.current = false`) contra un input TODAVÍA deshabilitado (turno recién
   * arrancando su consulta) — un `.focus()` sobre un elemento deshabilitado es un no-op, y como el
   * pedido ya se había marcado consumido, nunca se reintentaba cuando el turno terminaba de
   * resolver. Evidencia de mutación (mutation-proof-tests regla 2): sacando `bloqueadoPorTurno` de
   * esa condición (y de las dependencias del efecto), este test falla — el input queda habilitado
   * pero SIN foco; restaurado, vuelve a verde.
   */
  it('el turno resuelto en vuelo no deja perdido el pedido de foco inicial: el input de código se enfoca apenas se habilita', async () => {
    let resolverTurno: (t: TurnoResumen | null) => void = () => {}
    const turnoPendiente = new Promise<TurnoResumen | null>((resolve) => {
      resolverTurno = resolve
    })
    mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? turnoPendiente : undefined))

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
    expect(screen.getByLabelText('Código escaneado')).not.toHaveFocus()

    await act(async () => {
      resolverTurno(turnoAbiertoFixture())
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(screen.getByLabelText('Código escaneado')).toBeEnabled()
    expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el conjunct `bloqueadoPorTurno` del `disabled` del input de código
   * (spec: "mientras el turno está cerrado, solo búsqueda/consulta de precio"). Evidencia de
   * mutación (mutation-proof-tests regla 2): sacando `bloqueadoPorTurno` de ese `disabled`, este
   * test falla (el input queda habilitado con el turno confirmado cerrado); restaurado, vuelve a
   * verde.
   */
  it('con el turno confirmado cerrado, el input de código queda deshabilitado (nunca solo por escaneando/cobrando)', async () => {
    mockearTurnoCerrado()
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja cerrada')

    expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
  })

  /**
   * judgment-day ronda 1 — T3 (WARNING). Cláusula bajo prueba: el guard de
   * `document.activeElement` en el efecto de "foco neutral" — solo enfoca el input de código si
   * el foco actual es neutral (`body`/`null`) o ya es el propio input, nunca le saca el foco a un
   * control que el cajero eligió a propósito mientras el turno todavía cargaba. Evidencia de
   * mutación (mutation-proof-tests regla 2): sacando ese guard (dejando el efecto enfocar
   * incondicionalmente cuando se desbloquea), este test falla — el foco se mueve al input de
   * código en vez de quedarse en "Buscar artículo"; restaurado, vuelve a verde.
   */
  it('si el cajero ya enfocó otro control (ej. "Buscar artículo") mientras el turno cargaba, el desbloqueo NO le saca el foco', async () => {
    let resolverTurno: (t: TurnoResumen | null) => void = () => {}
    const turnoPendiente = new Promise<TurnoResumen | null>((resolve) => {
      resolverTurno = resolve
    })
    mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? turnoPendiente : undefined))

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    const botonBuscar = screen.getByRole('button', { name: 'Buscar artículo' })
    botonBuscar.focus()
    expect(botonBuscar).toHaveFocus()

    await act(async () => {
      resolverTurno(turnoAbiertoFixture())
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(screen.getByLabelText('Código escaneado')).toBeEnabled()
    expect(botonBuscar).toHaveFocus()
    expect(screen.getByLabelText('Código escaneado')).not.toHaveFocus()
  })

  it('una respuesta desactualizada de /caja/turnos/abierto no pisa una más reciente (generación, react-async-state regla 2)', async () => {
    let resolverPrimera: (t: TurnoResumen | null) => void = () => {}
    const primeraPendiente = new Promise<TurnoResumen | null>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeConsultas = 0
    mockearApiGet((ruta) => {
      if (ruta.startsWith('/caja/turnos/abierto')) {
        cantidadDeConsultas += 1
        if (cantidadDeConsultas === 1) return primeraPendiente
        return Promise.resolve(turnoAbiertoFixture({ id: 777 }))
      }
      return undefined
    })

    const { rerender } = renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await waitFor(() => expect(cantidadDeConsultas).toBe(1))

    // Un `recargar()` de punto de venta produce un objeto NUEVO con el mismo id — el efecto de
    // turno vuelve a correr (su dependencia es la referencia del punto de venta, no solo el id)
    // sin que `Pos()` remonte la pantalla (la key sigue siendo la misma, id 7).
    estadoDePuntoVenta.puntoVenta = puntoVentaFixture()
    rerender(arbolDePos())
    await waitFor(() => expect(cantidadDeConsultas).toBe(2))
    await screen.findByText('Caja abierta')

    await act(async () => {
      resolverPrimera(null)
      await Promise.resolve()
      await Promise.resolve()
    })

    // La respuesta vieja ("turno cerrado") no debe pisar el turno ya confirmado por la corrida
    // más reciente.
    expect(screen.getByText('Caja abierta')).toBeInTheDocument()
    expect(screen.queryByText('Caja cerrada: abrí la caja para vender.')).not.toBeInTheDocument()
  })

  describe('judgment-day ronda 1 — T1 (CRITICAL): "Reintentar" cuando falla la consulta del turno', () => {
    it('un error al consultar el turno muestra "Reintentar" junto al aviso (nunca deja la venta bloqueada sin salida)', async () => {
      mockearApiGet((ruta) =>
        ruta.startsWith('/caja/turnos/abierto') ? Promise.reject(new Error('network error')) : undefined,
      )
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })

      expect(await screen.findByText('No se pudo consultar el turno abierto de este punto de venta.')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Reintentar' })).toBeInTheDocument()
      // Sin esto, ni "Abrir caja" ni "Cerrar caja" existen y `bloqueadoPorTurno` queda en `true`
      // para siempre — el propio ternario del badge solo renderiza el aviso de error.
      expect(screen.queryByRole('button', { name: 'Abrir caja' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Cerrar caja' })).not.toBeInTheDocument()
      expect(screen.getByLabelText('Código escaneado')).toBeDisabled()
    })

    it('"Reintentar" con éxito habilita la venta (badge "Caja abierta", input de código habilitado)', async () => {
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        if (cantidadDeConsultas === 1) return Promise.reject(new Error('network error'))
        return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByRole('button', { name: 'Reintentar' })

      await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

      await screen.findByText('Caja abierta')
      expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()
      await waitFor(() => expect(screen.getByLabelText('Código escaneado')).toBeEnabled())
    })

    /**
     * Cláusula bajo prueba: la guarda de reentrancia por `ref` de `reintentarTurno`
     * (`reintentandoTurnoRef`), regla 11 de react-async-state. Evidencia de mutación
     * (mutation-proof-tests regla 2): reemplazando esa guarda por `if (cargandoTurno) return`
     * (leer el estado en vez del ref), este test falla — dos clicks sincrónicos en el mismo tick
     * disparan dos consultas; restaurada la guarda por ref, vuelve a verde.
     */
    it('doble click en "Reintentar" en el mismo tick dispara una única consulta nueva', async () => {
      let cantidadDeConsultas = 0
      let resolverSegunda: (t: TurnoResumen | null) => void = () => {}
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        if (cantidadDeConsultas === 1) return Promise.reject(new Error('network error'))
        return new Promise<TurnoResumen | null>((resolve) => {
          resolverSegunda = resolve
        })
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      const boton = await screen.findByRole('button', { name: 'Reintentar' })

      // Dos `.click()` sincrónicos dentro de un mismo `act`: React todavía no re-renderizó entre
      // uno y otro, así que ambas invocaciones del handler leen el MISMO `cargandoTurno` (todavía
      // `false`) de la clausura vieja — un guard basado en ese estado dejaría pasar los dos
      // (mutation-proof-tests regla 2: `fireEvent.click` por separado no discrimina esto, cada
      // llamada flushea su propio `act()` y ya ve el estado actualizado).
      act(() => {
        boton.click()
        boton.click()
      })

      await waitFor(() => expect(cantidadDeConsultas).toBe(2))
      await act(async () => {
        resolverSegunda(turnoAbiertoFixture())
        await Promise.resolve()
      })
      await screen.findByText('Caja abierta')
      // Un solo reintento nuevo además de la consulta original del mount (que fue la que falló).
      expect(cantidadDeConsultas).toBe(2)
    })
  })

  describe('judgment-day ronda 1 — T2 (WARNING): "Cerrar caja" vuelve a consultar el turno antes de navegar', () => {
    it('navega con el idTurno FRESCO de la consulta del click, no con el que ya tenía el estado', async () => {
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        // El mount trae el turno 900 (fixture default); el click trae uno FRESCO con otro id —
        // simula que el turno original se cerró y se abrió uno nuevo entre medio.
        return Promise.resolve<TurnoResumen>(cantidadDeConsultas === 1 ? turnoAbiertoFixture() : turnoAbiertoFixture({ id: 4242 }))
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

      expect(await screen.findByText('Cierre de turno 4242')).toBeInTheDocument()
      expect(cantidadDeConsultas).toBe(2)
    })

    it('el botón queda "Verificando…" y deshabilitado mientras la consulta fresca está en vuelo', async () => {
      let resolverClick: (t: TurnoResumen | null) => void = () => {}
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        if (cantidadDeConsultas === 1) return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
        return new Promise<TurnoResumen | null>((resolve) => {
          resolverClick = resolve
        })
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

      expect(await screen.findByRole('button', { name: 'Verificando…' })).toBeDisabled()

      await act(async () => {
        resolverClick(turnoAbiertoFixture())
        await Promise.resolve()
      })
    })

    it('si el turno ya no está abierto (cerrado por otra pestaña/cajero), actualiza el badge a "Turno cerrado" con el aviso y NO navega', async () => {
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        return cantidadDeConsultas === 1 ? Promise.resolve<TurnoResumen>(turnoAbiertoFixture()) : Promise.resolve(null)
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

      expect(await screen.findByText('El turno ya fue cerrado.')).toBeInTheDocument()
      expect(await screen.findByText('Caja cerrada')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Abrir caja' })).toBeInTheDocument()
      expect(screen.queryByText(/Cierre de turno/)).not.toBeInTheDocument()
    })

    it('si falla la consulta fresca, muestra un aviso propio sin tocar el badge ni navegar', async () => {
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        return cantidadDeConsultas === 1
          ? Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
          : Promise.reject(new Error('network error'))
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

      expect(await screen.findByText('No se pudo verificar el turno abierto.')).toBeInTheDocument()
      // El badge de turno sigue "abierto" (esta consulta es propia del click, no del mount/reintentar).
      expect(screen.getByText('Caja abierta')).toBeInTheDocument()
      expect(screen.queryByText(/Cierre de turno/)).not.toBeInTheDocument()
    })

    it('doble click en "Cerrar caja" en el mismo tick dispara una única consulta fresca', async () => {
      let cantidadDeConsultas = 0
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        cantidadDeConsultas += 1
        return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')
      const boton = screen.getByRole('button', { name: 'Cerrar caja' })

      fireEvent.click(boton)
      fireEvent.click(boton)

      await screen.findByText(`Cierre de turno ${turnoAbiertoFixture().id}`)
      // 1 consulta del mount + 1 sola consulta del click (nunca 2, aunque hubo dos clicks).
      expect(cantidadDeConsultas).toBe(2)
    })
  })

  describe('re-judgment ronda 2 — warnings', () => {
    it('"El turno ya fue cerrado." desaparece apenas se abre un turno de nuevo (Abrir caja)', async () => {
      mockearApiGet((ruta) => {
        if (!ruta.startsWith('/caja/turnos/abierto')) return undefined
        return Promise.resolve<TurnoResumen>(turnoAbiertoFixture())
      })
      renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      // "Cerrar caja" encuentra que ya no hay turno (cerrado por otra pestaña mientras tanto).
      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(null) : undefined))
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))
      expect(await screen.findByText('El turno ya fue cerrado.')).toBeInTheDocument()
      await screen.findByText('Caja cerrada')

      apiPostMock.mockImplementation((ruta: string) => {
        if (ruta === '/caja/turnos') return Promise.resolve(turnoAbiertoFixture({ id: 8080 }))
        return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
      })
      await userEvent.click(screen.getByRole('button', { name: 'Abrir caja' }))
      await userEvent.type(screen.getByLabelText('Fondo inicial'), '500')
      await userEvent.click(screen.getByRole('button', { name: 'Abrir turno' }))

      await screen.findByText('Caja abierta')
      expect(screen.queryByText('El turno ya fue cerrado.')).not.toBeInTheDocument()
    })

    /**
     * Mismo aviso, camino distinto: en vez de "Abrir turno" (test de arriba), una consulta
     * exitosa posterior de MONTAJE (ej. el punto de venta se recarga con el mismo id — mismo
     * disparador que el test de "generación" más arriba, sin remontar `PantallaPos`) confirma el
     * turno abierto de nuevo y debe limpiar igual el aviso viejo de "Cerrar caja".
     */
    it('"El turno ya fue cerrado." también desaparece con una consulta exitosa posterior del mount', async () => {
      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(turnoAbiertoFixture()) : undefined))
      const { rerender } = renderPos()
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(null) : undefined))
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))
      expect(await screen.findByText('El turno ya fue cerrado.')).toBeInTheDocument()

      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(turnoAbiertoFixture({ id: 321 })) : undefined))
      estadoDePuntoVenta.puntoVenta = puntoVentaFixture()
      rerender(arbolDePos())

      await waitFor(() => expect(screen.queryByText('El turno ya fue cerrado.')).not.toBeInTheDocument())
      expect(screen.getByText('Caja abierta')).toBeInTheDocument()
    })

    /**
     * Cláusula bajo prueba: el guard `montadoRef.current` de `irACerrarCaja`, chequeado
     * INMEDIATAMENTE después del `await` — antes de tocar el turno/navegar. Sin él, un cajero que
     * navega fuera de la pantalla de venta mientras la consulta fresca de "Cerrar caja" sigue en
     * vuelo terminaría navegando IGUAL cuando esa respuesta llegara tarde, contra una pantalla que
     * ya no está.
     */
    it('si la pantalla se desmonta mientras "Cerrar caja" está verificando el turno, no navega al resolver', async () => {
      let resolverConsulta: (t: TurnoResumen | null) => void = () => {}
      const consultaPendiente = new Promise<TurnoResumen | null>((resolve) => {
        resolverConsulta = resolve
      })
      const alIrACerrarCaja = vi.fn()
      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? Promise.resolve(turnoAbiertoFixture()) : undefined))

      const { unmount } = render(
        <MemoryRouter initialEntries={['/pos']}>
          <Routes>
            <Route path="/pos" element={<Pos alIrACerrarCaja={alIrACerrarCaja} />} />
          </Routes>
        </MemoryRouter>,
      )
      await screen.findByRole('option', { name: /Consumidor Final/ })
      await screen.findByText('Caja abierta')

      mockearApiGet((ruta) => (ruta.startsWith('/caja/turnos/abierto') ? consultaPendiente : undefined))
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar caja' }))

      unmount()

      await act(async () => {
        resolverConsulta(turnoAbiertoFixture())
        await Promise.resolve()
        await Promise.resolve()
      })

      expect(alIrACerrarCaja).not.toHaveBeenCalled()
    })
  })
})

describe('Pos — medio de pago por defecto: Efectivo (stage-pos-turno-y-foco)', () => {
  it('la fila inicial de pago preselecciona Efectivo apenas cargan los medios', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await waitFor(() => expect(screen.getByLabelText('Medio de pago')).toHaveValue(String(medioEfectivo.id)))
  })

  it('tras completar una venta, la fila de pago vuelve a preseleccionar Efectivo de inmediato (sin esperar el cierre del modal)', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByRole('dialog', { name: 'Venta finalizada' })

    await waitFor(() => expect(screen.getByLabelText('Medio de pago')).toHaveValue(String(medioEfectivo.id)))
  })

  it('sin ningún medio Efectivo configurado, la fila queda sin preseleccionar (comportamiento sin cambios)', async () => {
    mockearApiGet((ruta) => (ruta === '/catalogos/medios-pago' ? Promise.resolve([medioTarjeta]) : undefined))
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByRole('option', { name: medioTarjeta.nombre })

    expect(screen.getByLabelText('Medio de pago')).toHaveValue('')
  })
})

describe('Pos — checkout: split de pago con el mismo medio', () => {
  it('dos filas de Efectivo (split de pago) no colapsan: cada una envía su propio importe, el vuelto auto-calculado se lo lleva la primera', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '60')

    await userEvent.click(screen.getByRole('button', { name: '+ Agregar medio de pago' }))
    await userEvent.selectOptions(screen.getAllByLabelText('Medio de pago')[1], medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 2)'), '50')

    // Con ambos importes cargados (60 + 50 = 110 sobre un total de 100), el excedente es 10 — sin
    // ningún override manual (la etapa 2 sacó ese input), el sugerido se lo lleva íntegro la fila
    // 1 (la primera que admite vuelto, `calcularPagosConVuelto`), nunca la fila 2.
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await waitFor(() => expect(apiPostMock.mock.calls.some((llamada) => llamada[0] === '/ventas')).toBe(true))
    const llamadaVentas = apiPostMock.mock.calls.find((llamada) => llamada[0] === '/ventas')
    const solicitud = llamadaVentas?.[1] as {
      pagos: { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }[]
    }

    expect(solicitud.pagos).toEqual([
      { idMedioPago: medioEfectivo.id, importe: 60, referencia: null, vuelto: 10 },
      { idMedioPago: medioEfectivo.id, importe: 50, referencia: null, vuelto: 0 },
    ])
  })
})

describe('Pos — CampoImporte en el panel de pagos: separador de miles y decimales con coma', () => {
  it('tipear "10.000" (con separador de miles) en el importe se interpreta como 10000 — la vista se actualiza y el checkout envía el número correcto', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText('Importe de Efectivo (fila 1)')
    await userEvent.type(importe, '10.000')
    importe.blur()

    await waitFor(() => expect(importe).toHaveValue('10.000,00'))
    // Excedente = 10000 − 100 = 9900, con separador de miles en la vista previa de "Vuelto".
    await waitFor(() => expect(screen.getByText('$ 9.900,00')).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await waitFor(() => expect(apiPostMock.mock.calls.some((llamada) => llamada[0] === '/ventas')).toBe(true))
    const llamadaVentas = apiPostMock.mock.calls.find((llamada) => llamada[0] === '/ventas')
    const solicitud = llamadaVentas?.[1] as { pagos: { importe: number; vuelto: number }[] }
    expect(solicitud.pagos[0].importe).toBe(10000)
    expect(typeof solicitud.pagos[0].importe).toBe('number')
  })

  it('tipear "10000" (sin puntos) y "10000,5" (con decimales) en el importe también se interpretan correctamente', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText('Importe de Efectivo (fila 1)')

    await userEvent.type(importe, '10000')
    importe.blur()
    await waitFor(() => expect(importe).toHaveValue('10.000,00'))

    await userEvent.clear(importe)
    await userEvent.type(importe, '10000,5')
    importe.blur()
    await waitFor(() => expect(importe).toHaveValue('10.000,50'))
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

    renderPos()
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
    renderPos()
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
    renderPos()
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
    renderPos()
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
      if (ruta === '/clientes') return clientesPendientes
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()

    const entrada = screen.getByLabelText('Código escaneado')
    // `/clientes` queda pendiente a propósito (nunca resuelve en este test) — no se puede esperar
    // la opción "Consumidor Final" como en el resto de los tests; se espera en cambio a que el
    // turno (independiente de clientes) resuelva y habilite el input.
    await waitFor(() => expect(entrada).toBeEnabled())
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
    renderPos()
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
    renderPos()
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
    renderPos()
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

describe('Pos — sin selector de lote en el detalle del carrito (stage-pos-buscador-articulos)', () => {
  /**
   * Cláusula bajo prueba: el picker de lote inline ya no se renderiza en las líneas del carrito.
   * Nunca fue exigido acá para que el checkout se acepte (`ServicioDeVentas` solo rechaza un
   * `idLote` ausente en una línea de DEVOLUCIÓN, tipo NCX, signo -1 — esta pantalla emite
   * siempre `codigoTipoComprobante: 'TX'`, signo +1, así que el camino feliz de FEFO automático
   * de design decisión 19 cubre el 100% de sus ventas): quitar la columna nunca puede romper un
   * checkout real. `SelectorDeLote` (y su columna "Lote") siguen existiendo — los usa
   * `Remito.tsx` — este test prueba que `Pos.tsx` dejó de montarlo, no que el componente
   * desapareció del repo.
   */
  it('la fila del carrito no muestra "Lote" ni el botón "Elegir lote", y el checkout igual se acepta', async () => {
    await armarVentaLista()

    expect(screen.queryByText('Lote')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Elegir lote' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/^Lote de /)).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    expect(await screen.findByText(/Venta 0007-00000001/)).toBeInTheDocument()
    const solicitud = apiPostMock.mock.calls.find((call: unknown[]) => call[0] === '/ventas')?.[1] as
      | { lineas: { idLote: number | null }[] }
      | undefined
    expect(solicitud?.lineas.every((l) => l.idLote === null)).toBe(true)
  })
})

describe('Pos — modal "Venta finalizada": aviso de lote vencido (design decisión 12: "Expired Lot Sale Warns, Never Blocks")', () => {
  /**
   * Cláusula bajo prueba: el predicado `item.loteVencido` en el `.filter()` que arma
   * `itemsVencidos` (`Pos.tsx`, dentro de `cobrar()`). Mutación aplicada manualmente: cambiar el
   * filtro a `.filter(() => false)` → este test pasa a rojo (el aviso no aparece con un item
   * vencido en la respuesta). Revertido, vuelve a verde — evidencia registrada en el informe de
   * la tarea.
   */
  it('un item emitido con loteVencido: true muestra el aviso "⚠ Se vendió un lote vencido" en el modal "Venta finalizada", con la descripción y el código de lote', async () => {
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

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.getByText('⚠ Se vendió un lote vencido')).toBeInTheDocument()
    expect(modal.getByText(/Coca Cola 1L.*Lote 2026-01-01/)).toBeInTheDocument()
  })

  it('con todos los items sin loteVencido, el modal "Venta finalizada" no muestra ningún aviso de lote vencido', async () => {
    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.queryByText('⚠ Se vendió un lote vencido')).not.toBeInTheDocument()
  })
})

describe('Pos — conversión de presupuesto (stage-17-presupuestos-y-remitos, Slice 7, design: Web composition)', () => {
  function presupuestoParaVentaFixture(sobrescribir: Partial<PresupuestoParaVenta> = {}): PresupuestoParaVenta {
    return {
      idPresupuesto: 1,
      numero: 1,
      idPuntoVenta: 7,
      idCliente: 2,
      vencimiento: '2026-09-30',
      vencido: false,
      convertible: true,
      subtotal: 200,
      descuentoTotal: 0,
      total: 200,
      items: [
        {
          orden: 1,
          idArticulo: 1,
          descripcion: 'Coca Cola 1L',
          cantidad: 2,
          precioUnitario: 100,
          descuento: 0,
          total: 200,
          idListaPrecio: 1,
          idOferta: null,
          idAlicuotaIva: 1,
          porcentajeIva: 21,
        },
      ],
      ...sobrescribir,
    }
  }

  const puntoVentaDelPresupuesto = puntoVentaFixture({ id: 7 })
  const puntoVentaDeLaSesion = puntoVentaFixture({ id: 8, nombre: 'Sucursal Norte' })

  /** El presupuesto fija el punto de venta 7; la sesión está parada en otro (8) a propósito, así
   * que "Local Centro" en pantalla solo puede salir del presupuesto, nunca de la sesión. */
  beforeEach(() => {
    estadoDePuntoVenta.puntosVenta = [puntoVentaDelPresupuesto, puntoVentaDeLaSesion]
    estadoDePuntoVenta.puntoVenta = puntoVentaDeLaSesion
  })

  function mockearApiGetPresupuesto(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/presupuestos/1/para-venta') return Promise.resolve(presupuestoParaVentaFixture())
      if (ruta === '/clientes/2') return Promise.resolve(otroCliente)
      const propia = sobrescribir?.(ruta) ?? rutaBaseDePos(ruta)
      if (propia) return propia
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
  }

  it('renderiza el banner, hidrata el carrito congelado de solo lectura, fija punto de venta/cliente y NUNCA dispara la resolución de precio (tarea 7.7)', async () => {
    mockearApiGetPresupuesto()
    renderPos('/pos?idPresupuesto=1')

    expect(await screen.findByText(/Esta venta viene del presupuesto N° 1/)).toBeInTheDocument()
    expect(screen.getByText(/vence el/)).toBeInTheDocument()
    expect(await screen.findByText('Coca Cola 1L')).toBeInTheDocument()

    // El input de escaneo y "Quitar" NUNCA se renderizan bajo este modo — el carrito congelado
    // no admite mutación (design: "disable scan/quantity/removal").
    expect(screen.queryByLabelText('Código escaneado')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Quitar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Vaciar carrito' })).not.toBeInTheDocument()

    // El cliente lo trae el presupuesto por id, queda deshabilitado (solo lectura).
    await waitFor(() => expect(screen.getByLabelText('Cliente')).toHaveValue(String(otroCliente.id)))
    expect(screen.getByLabelText('Cliente')).toBeDisabled()

    // El total mostrado es el del presupuesto congelado, nunca uno recalculado por resolución.
    expect(screen.getByText('$ 200,00', { selector: 'strong' })).toBeInTheDocument()

    // Regla central de la tarea: ninguna resolución de precio bajo este modo.
    expect(apiPostMock).not.toHaveBeenCalledWith('/ofertas/resolver', expect.anything())
  })

  it('cobrar postea la SolicitudDeVenta con idPresupuestoOrigen, sin idCliente ni lineas (dto-contract-honesty)', async () => {
    mockearApiGetPresupuesto()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture({ idPresupuestoOrigen: 1, total: 200, subtotal: 200 }))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos('/pos?idPresupuesto=1')
    await screen.findByText('Coca Cola 1L')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    await userEvent.type(importe, '200')

    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/ventas', expect.objectContaining({ idPresupuestoOrigen: 1 })))
    const llamada = apiPostMock.mock.calls.find(([ruta]) => ruta === '/ventas') as [string, Record<string, unknown>]
    expect(llamada[1]).not.toHaveProperty('idCliente')
    expect(llamada[1]).not.toHaveProperty('lineas')
  })

  it('"Aceptar" del modal "Venta finalizada" bajo `?idPresupuesto=` navega a `/pos` y remonta la pantalla entera: el ticket de la venta convertida NO queda pegado (judgment-day slice-7 ronda 1 juez B, MAJOR)', async () => {
    mockearApiGetPresupuesto()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture({ idPresupuestoOrigen: 1, total: 200, subtotal: 200 }))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos('/pos?idPresupuesto=1')
    await screen.findByText('Coca Cola 1L')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    await userEvent.type(importe, '200')

    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    const modal = within(await screen.findByRole('dialog', { name: 'Venta finalizada' }))
    expect(modal.getByText('Venta 0007-00000001')).toBeInTheDocument()

    await userEvent.click(modal.getByRole('button', { name: 'Aceptar' }))

    // `cerrarVentaFinalizada()` bajo `modoPresupuesto` navega a `/pos` en vez de volver
    // `ventaFinalizada` a `null` localmente (react-async-state regla 8): la ruta pierde
    // `?idPresupuesto=`, el `key` de `Pos()` pasa de `1` a `'libre'` y `PantallaPos` se remonta
    // entera — sin ese remount, el ticket de la venta ya convertida quedaría pegado en pantalla
    // para siempre.
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
    expect(await screen.findByLabelText('Código escaneado')).toBeInTheDocument()
  })

  it('bajo `?idPresupuesto=`, un código tipeado con el modal "Venta finalizada" abierto se descarta al cerrar — la pantalla ya navegó afuera, nunca se escanea', async () => {
    mockearApiGetPresupuesto()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture({ idPresupuestoOrigen: 1, total: 200, subtotal: 200 }))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos('/pos?idPresupuesto=1')
    await screen.findByText('Coca Cola 1L')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    await userEvent.type(importe, '200')

    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))

    const dialogo = await screen.findByRole('dialog', { name: 'Venta finalizada' })
    tipearEnDialogo(dialogo, '7790001234567')
    fireEvent.keyDown(dialogo, { key: 'Enter' })

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Venta finalizada' })).not.toBeInTheDocument())
    expect(await screen.findByLabelText('Código escaneado')).toHaveValue('')
    expect(apiGetMock.mock.calls.some(([ruta]) => String(ruta).startsWith('/articulos/escaneo'))).toBe(false)
  })

  /**
   * CORRECCIÓN REGISTRADA (judgment-day slice-7 ronda 1 juez B, WARNING — mutation-proof-tests
   * regla 2/3): este test se documentaba como prueba del guard `tokenPresupuestoRef.current !==
   * miToken` del `.then()`/`.catch()`/`.finally()` de la carga de `/para-venta` — no lo es.
   * React 19 volvió a un `setState` post-desmontaje un no-op silencioso (sin el warning de
   * `console.error` que React ≤18 emitía), así que `expect(errorSpy).not.toHaveBeenCalled()` da
   * verde exista o no CUALQUIERA de los dos guards (`vigente` o el token) — no discrimina nada
   * bajo esta versión de React.
   *
   * El chequeo de `vigente` sigue siendo necesario y observable en otro sentido (evita el
   * `setState` en sí, no solo su warning), pero el chequeo de TOKEN específicamente resultó, tras
   * intentarlo, estructuralmente imposible de discriminar con un test montado: el efecto que lo
   * usa depende solo de `[modoPresupuesto, idPresupuesto]`, y `Pos()` remonta `PantallaPos`
   * entera por `key={idPresupuesto ?? 'libre'}` en cuanto ese id cambia (react-async-state regla
   * 8) — dentro de la vida de UNA instancia montada, `idPresupuesto` nunca cambia, así que este
   * efecto corre exactamente una vez (la única excepción real, el doble-invoke de `StrictMode` en
   * desarrollo, tampoco sirve: la limpieza del primer run se ejecuta de forma síncrona, antes de
   * que CUALQUIER promesa tenga oportunidad de resolver, así que `vigente` ya blinda ese caso por
   * sí solo). No existe una re-carga real de `/para-venta` con la MISMA instancia montada que
   * compita contra un token distinto — cualquier "segunda carga" observable en producción es, en
   * los hechos, una instancia nueva (mismo patrón que el confound del `40P01` en la tarea 1.32:
   * confirmado empíricamente, no razonado, y registrado como tal en vez de inflar la cobertura
   * reclamada). El test se conserva sin cambios (prueba real, aunque no discriminante, de que un
   * resolve tardío post-desmontaje no revienta el proceso) — solo se corrige la afirmación de qué
   * prueba.
   */
  it('una respuesta tardía de /para-venta tras desmontar la pantalla no dispara ningún error (mutation-proof-tests regla 7: resuelta dentro de act — NO discrimina el guard de token, ver comentario arriba)', async () => {
    let resolverParaVenta: (p: PresupuestoParaVenta) => void = () => {}
    const pendiente = new Promise<PresupuestoParaVenta>((resolve) => {
      resolverParaVenta = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/presupuestos/1/para-venta') return pendiente
      if (ruta === '/clientes/2') return Promise.resolve(otroCliente)
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const { unmount } = renderPos('/pos?idPresupuesto=1')
    await screen.findByText('Cargando…')

    unmount()

    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    await act(async () => {
      resolverParaVenta(presupuestoParaVentaFixture())
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(errorSpy).not.toHaveBeenCalled()
    errorSpy.mockRestore()
  })
})

describe('Pos — seam alEmitir (stage-desktop-pos)', () => {
  function renderPosConAlEmitir(alEmitir: (comprobante: ComprobanteEmitido, cliente: ClienteListado, medios: MedioPagoListado[]) => void) {
    return render(
      <MemoryRouter initialEntries={['/pos']}>
        <Routes>
          <Route path="/pos" element={<Pos alEmitir={alEmitir} />} />
          <Route path="/presupuestos" element={<div>Presupuestos</div>} />
        </Routes>
      </MemoryRouter>,
    )
  }

  async function completarVentaLista() {
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`), '100')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())
  }

  it('una venta exitosa invoca alEmitir con el comprobante, el cliente y la lista de medios de pago', async () => {
    const alEmitir = vi.fn()
    renderPosConAlEmitir(alEmitir)
    await completarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('Venta 0007-00000001')

    expect(alEmitir).toHaveBeenCalledTimes(1)
    expect(alEmitir).toHaveBeenCalledWith(comprobanteEmitidoFixture(), consumidorFinal, [medioEfectivo, medioTarjeta, medioCuentaCorriente])
  })

  it('sin alEmitir (app web normal) la venta funciona exactamente igual, sin lanzar', async () => {
    renderPos()
    await completarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
  })

  it('un checkout que falla no invoca alEmitir', async () => {
    mockearApiGet()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') return Promise.reject(new ErrorApi(400, 'error', 'No se pudo registrar la venta.'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    const alEmitir = vi.fn()
    renderPosConAlEmitir(alEmitir)
    await completarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await screen.findByText('No se pudo registrar la venta.')

    expect(alEmitir).not.toHaveBeenCalled()
  })
})

describe('Pos — foco del input de código (stage-pos-buscador-articulos)', () => {
  it('el input de código tiene autofocus al entrar a la pantalla', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
  })

  it('el foco vuelve al input de código después de agregar por código', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    const inputCodigo = screen.getByLabelText('Código escaneado')

    await userEvent.type(inputCodigo, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')

    expect(inputCodigo).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el conjunct `cobrando` del guard del efecto de foco pendiente (`if
   * (escaneando || cobrando || buscadorAbierto) return`). Un click en "Cobrar" mientras un
   * escaneo sigue en vuelo (regla 9 de react-async-state: nada puede quedar operable mientras el
   * checkout está en curso) no debe dejar el input habilitado ni enfocado — recién cuando el
   * checkout termina (acá, falla) el efecto vuelve a correr y recién ahí consume el pedido de
   * foco pendiente. Evidencia de mutación (mutation-proof-tests regla 2): sacando `cobrando` de
   * esa condición, el efecto consume el pedido de foco apenas el escaneo resuelve (con el
   * checkout todavía en vuelo) — la aserción final falla, porque ya no queda ningún pedido
   * pendiente para cuando el checkout realmente termina. Restaurada, vuelve a verde.
   */
  it('un escaneo en vuelo + click en "Cobrar": el input queda deshabilitado y sin foco durante el checkout, y recupera el foco recién cuando termina', async () => {
    let resolverEscaneo: (articulo: ArticuloEscaneado) => void = () => {}
    const escaneoPendiente = new Promise<ArticuloEscaneado>((resolve) => {
      resolverEscaneo = resolve
    })
    let rechazarVenta: (error: unknown) => void = () => {}
    const ventaPendiente = new Promise((_resolve, reject) => {
      rechazarVenta = reject
    })

    await armarVentaLista()

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return escaneoPendiente
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') return ventaPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const inputCodigo = screen.getByLabelText('Código escaneado')
    await userEvent.type(inputCodigo, '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrando/ })).toBeInTheDocument())

    await act(async () => {
      resolverEscaneo(articuloEscaneadoFixture())
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(inputCodigo).toBeDisabled()
    expect(inputCodigo).not.toHaveFocus()

    await act(async () => {
      rechazarVenta(new ErrorApi(400, 'error', 'No se pudo registrar la venta.'))
      await Promise.resolve()
      await Promise.resolve()
    })
    await screen.findByText('No se pudo registrar la venta.')

    expect(inputCodigo).not.toBeDisabled()
    expect(inputCodigo).toHaveFocus()
  })
})

describe('Pos — abrir/cerrar el buscador de artículos (stage-pos-buscador-articulos)', () => {
  it('el botón "Buscar artículo" abre el modal', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))

    expect(await screen.findByRole('dialog', { name: 'Buscar artículo' })).toBeInTheDocument()
  })

  it('F2 abre el modal mientras la venta libre está activa', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    fireEvent.keyDown(document, { key: 'F2' })

    expect(await screen.findByRole('dialog', { name: 'Buscar artículo' })).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el conjunct `cobrando` del guard de F2 (`if (modoPresupuesto ||
   * cobrando || buscadorAbierto || gateTurno || ventaFinalizada) return`). Con el checkout en
   * vuelo, F2 no debe abrir nada — reabrir el buscador ahí violaría la regla 9 de
   * react-async-state (todo lo que podría superponerse al checkout queda inerte).
   */
  it('F2 no abre el modal mientras el checkout está en vuelo', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      if (ruta === '/ventas') return new Promise(() => {})
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await armarVentaLista()
    await userEvent.click(screen.getByRole('button', { name: /Cobrar/ }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrando/ })).toBeInTheDocument())

    fireEvent.keyDown(document, { key: 'F2' })

    expect(screen.queryByRole('dialog', { name: 'Buscar artículo' })).not.toBeInTheDocument()
  })

  it('Escape cierra el modal y devuelve el foco al input de código', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    const inputCodigo = screen.getByLabelText('Código escaneado')

    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    await screen.findByRole('dialog', { name: 'Buscar artículo' })

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('dialog', { name: 'Buscar artículo' })).not.toBeInTheDocument()
    expect(inputCodigo).toHaveFocus()
  })

  it('el botón "Cerrar" (X) cierra el modal y devuelve el foco al input de código', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    const inputCodigo = screen.getByLabelText('Código escaneado')

    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Buscar artículo' }))

    await userEvent.click(dialogo.getByRole('button', { name: 'Cerrar' }))

    expect(screen.queryByRole('dialog', { name: 'Buscar artículo' })).not.toBeInTheDocument()
    expect(inputCodigo).toHaveFocus()
  })
})

describe('Pos — búsqueda de artículos por nombre en el modal (stage-pos-buscador-articulos)', () => {
  async function abrirBuscador() {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    return within(await screen.findByRole('dialog', { name: 'Buscar artículo' }))
  }

  it('con menos de 2 caracteres no dispara ninguna búsqueda', async () => {
    const dialogo = await abrirBuscador()

    fireEvent.change(dialogo.getByLabelText('Buscar artículo por nombre'), { target: { value: 'f' } })
    fireEvent.keyDown(dialogo.getByLabelText('Buscar artículo por nombre'), { key: 'Enter' })
    await Promise.resolve()

    expect(apiGetMock).not.toHaveBeenCalledWith(expect.stringContaining('/articulos?busqueda='))
  })

  it('tipear 2+ caracteres busca por nombre tras el debounce (~300ms) y renderiza Código/Nombre/Precio', async () => {
    const fanta = articuloListadoFixture()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [fanta], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 9, idListaPrecio: 1, precioOriginal: 250, precioFinal: 200, descuentoUnitario: 50, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const dialogo = await abrirBuscador()
    const input = dialogo.getByLabelText('Buscar artículo por nombre')

    vi.useFakeTimers()
    try {
      fireEvent.change(input, { target: { value: 'fa' } })
      expect(apiGetMock).not.toHaveBeenCalledWith(expect.stringContaining('/articulos?busqueda=fa'))
      await vi.advanceTimersByTimeAsync(300)
    } finally {
      vi.useRealTimers()
    }

    expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('/articulos?busqueda=fa'))
    expect(await dialogo.findByText('A0009')).toBeInTheDocument()
    expect(dialogo.getByText('Fanta 1.5L')).toBeInTheDocument()
    expect(dialogo.getByText('$ 200,00')).toBeInTheDocument()
  })

  it('Enter dispara la búsqueda de inmediato, sin esperar el debounce', async () => {
    const fanta = articuloListadoFixture()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [fanta], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const dialogo = await abrirBuscador()
    const input = dialogo.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'fa' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await dialogo.findByText('Fanta 1.5L')).toBeInTheDocument()
  })

  it('sin resultados muestra "Sin resultados"', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [], total: 0, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const dialogo = await abrirBuscador()
    const input = dialogo.getByLabelText('Buscar artículo por nombre')
    fireEvent.change(input, { target: { value: 'zzz' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await dialogo.findByText('Sin resultados')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el guard `generacionRef.current !== generacion` que sigue a CADA
   * `await` de `buscar()` (mutation-proof-tests regla 7). La primera búsqueda ("co") queda
   * pendiente adrede; la segunda ("sp") resuelve antes y se muestra; recién ahí se libera la
   * respuesta vieja — si el guard no existiera, pisaría los resultados ya mostrados. Evidencia de
   * mutación: con el guard comentado, este test falla ("Fanta" aparece encima de "Sprite");
   * restaurado, vuelve a verde — ver el reporte de la tarea.
   */
  it('una respuesta tardía de una búsqueda anterior no pisa los resultados de una búsqueda posterior (stale-response gating)', async () => {
    let resolverPrimera: (pagina: PaginaDe<ArticuloListado>) => void = () => {}
    const primeraPendiente = new Promise<PaginaDe<ArticuloListado>>((resolve) => {
      resolverPrimera = resolve
    })
    const fanta = articuloListadoFixture({ id: 9, codigoInterno: 'A0009', nombre: 'Fanta 1.5L' })
    const sprite = articuloListadoFixture({ id: 10, codigoInterno: 'A0010', nombre: 'Sprite 1.5L' })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=co')) return primeraPendiente
      if (ruta.startsWith('/articulos?busqueda=sp')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [sprite], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const dialogo = await abrirBuscador()
    const input = dialogo.getByLabelText('Buscar artículo por nombre')

    fireEvent.change(input, { target: { value: 'co' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    fireEvent.change(input, { target: { value: 'sp' } })
    fireEvent.keyDown(input, { key: 'Enter' })

    expect(await dialogo.findByText('Sprite 1.5L')).toBeInTheDocument()

    await act(async () => {
      resolverPrimera({ items: [fanta], total: 1, pagina: 1, tamanio: 25 })
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(dialogo.getByText('Sprite 1.5L')).toBeInTheDocument()
    expect(dialogo.queryByText('Fanta 1.5L')).not.toBeInTheDocument()
  })

  it('"Agregar" de una fila agrega la línea al carrito una sola vez, cierra el modal y devuelve el foco al input de código', async () => {
    const fanta = articuloListadoFixture()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?busqueda=fa')) {
        const pagina: PaginaDe<ArticuloListado> = { items: [fanta], total: 1, pagina: 1, tamanio: 25 }
        return Promise.resolve(pagina)
      }
      return rutaBaseDePos(ruta) ?? Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        const resultados: ResultadoDeResolucion[] = [
          { idArticulo: 9, idListaPrecio: 1, precioOriginal: 250, precioFinal: 200, descuentoUnitario: 50, aplicadas: [] },
        ]
        return Promise.resolve(resultados)
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    const inputCodigo = screen.getByLabelText('Código escaneado')

    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Buscar artículo' }))
    fireEvent.change(dialogo.getByLabelText('Buscar artículo por nombre'), { target: { value: 'fa' } })
    fireEvent.keyDown(dialogo.getByLabelText('Buscar artículo por nombre'), { key: 'Enter' })
    const filaAgregar = await dialogo.findByRole('button', { name: 'Agregar' })

    // react-async-state regla 11 (reentrancia por ref, no por estado): dos clicks sincrónicos en
    // el mismo tick — el segundo no debe agregar una segunda línea antes de que React desmonte
    // el modal.
    fireEvent.click(filaAgregar)
    fireEvent.click(filaAgregar)

    expect(screen.queryByRole('dialog', { name: 'Buscar artículo' })).not.toBeInTheDocument()
    expect(await screen.findByText('Fanta 1.5L')).toBeInTheDocument()
    expect(screen.getByLabelText('Cantidad de Fanta 1.5L')).toHaveValue(1)
    expect(inputCodigo).toHaveFocus()
  })
})

describe('Pos — atajo de teclado F9 para cobrar (stage-pos-atajos-cobro)', () => {
  it('"Cobrar" muestra (F9) como superíndice decorativo y expone el atajo con aria-keyshortcuts, sin tocar el nombre accesible', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const boton = screen.getByRole('button', { name: 'Cobrar' })
    expect(boton).toHaveAttribute('aria-keyshortcuts', 'F9')
    expect(boton).toHaveTextContent('(F9)')
  })

  it('"Buscar artículo" muestra (F2) como superíndice decorativo y expone el atajo con aria-keyshortcuts, sin tocar el nombre accesible', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    const boton = screen.getByRole('button', { name: 'Buscar artículo' })
    expect(boton).toHaveAttribute('aria-keyshortcuts', 'F2')
    expect(boton).toHaveTextContent('(F2)')
  })

  it('F9 sin ningún importe cargado enfoca el importe de la primera fila, sin abrir el diálogo', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)

    fireEvent.keyDown(document, { key: 'F9' })

    expect(importe).toHaveFocus()
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: `pagosCubrenElTotal` (`faltante <= toleranciaPago`) — con
   * `tolerancia_pago = 10` (mock base) y un importe de $ 50 contra un total de $ 100, falta $ 50,
   * muy por encima de la tolerancia. Mutación aplicada manualmente: forzar la rama de "cubre el
   * total" a tomarse siempre (saltear el chequeo) → este test pasa a rojo (abre el diálogo en vez
   * de enfocar). Revertido, vuelve a verde — evidencia registrada en el informe de la tarea.
   */
  it('F9 con importe insuficiente (por debajo de la tolerancia) enfoca el importe, sin abrir el diálogo', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    const importe = await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    await userEvent.type(importe, '50')
    // Saca el foco del importe ANTES del F9 (mutation-proof-tests regla 3: sin esto, el propio
    // `userEvent.type` ya lo habría dejado enfocado y la aserción de foco de abajo no probaría
    // nada — pasaría igual aunque `enfocarPrimeraFilaDePagoPendiente` nunca se llamara).
    screen.getByLabelText('Código escaneado').focus()

    fireEvent.keyDown(document, { key: 'F9' })

    expect(importe).toHaveFocus()
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('F9 con el pago cubriendo el total abre "¿Finalizar venta?" con total, pagado y vuelto', async () => {
    await armarVentaLista()

    fireEvent.keyDown(document, { key: 'F9' })

    const dialogo = within(await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' }))
    expect(dialogo.getByText('Total: $ 100,00')).toBeInTheDocument()
    expect(dialogo.getByText('Pagado: $ 100,00')).toBeInTheDocument()
    expect(dialogo.getByText('Vuelto: $ 0,00')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la guarda de reentrancia de `cobrar()` (`cobrandoRef`), que es la que
   * hace segura una segunda F9 mientras el diálogo sigue abierto (el propio `confirmarYcobrar`
   * repite la misma guarda de primera línea, pero la protección real contra un segundo POST real
   * es esta). Mutación aplicada manualmente: comentar el `if (cobrandoRef.current) return` al
   * inicio de `cobrar()` → este test pasa a rojo (dos POSTs a `/ventas`). Revertido, vuelve a
   * verde — evidencia registrada en el informe de la tarea.
   */
  it('una segunda F9 con el diálogo ya abierto confirma la venta exactamente una vez', async () => {
    let resolverCheckout: (comprobante: ComprobanteEmitido) => void = () => {}
    const checkoutPendiente = new Promise<ComprobanteEmitido>((resolve) => {
      resolverCheckout = resolve
    })

    await armarVentaLista()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ventas') return checkoutPendiente
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    fireEvent.keyDown(document, { key: 'F9' })
    await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })

    // react-async-state regla 11 (reentrancia por ref, no por estado): las DOS F9 se disparan
    // dentro del MISMO `act` síncrono a propósito — envueltas por separado, cada `fireEvent`
    // fuerza su propio flush y la segunda ya vería `cobrando` actualizado por estado; acá adentro
    // ninguna de las dos ve un re-render intermedio, así que solo el `ref` (mutado de forma
    // síncrona, ANTES del primer `await` de `cobrar()`) puede evitar el segundo POST.
    act(() => {
      fireEvent.keyDown(document, { key: 'F9' })
      fireEvent.keyDown(document, { key: 'F9' })
    })

    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(1)

    // K2 (judgment-day ronda 1, CRITICAL): la invocación redundante de `confirmarYcobrar` no debe
    // alcanzar a cerrar el diálogo antes de que el POST real (todavía pendiente acá) resuelva —
    // sin `finalizandoDesdeDialogoRef`, el llamado redundante corría `setConfirmandoCobro(false)`
    // de inmediato (su propio `await cobrar()` resuelve rápido, sin llegar a la red, así que su
    // continuación queda lista en la cola de microtasks). Se le da chance de correr ANTES de
    // afirmar — sin este `await` a un microtask, la aserción corre en el mismo tick síncrono y
    // "pasaría" igual aunque el cierre prematuro estuviera en camino (falso negativo).
    await act(async () => {
      await Promise.resolve()
    })
    expect(screen.getByRole('alertdialog', { name: '¿Finalizar venta?' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Finalizando…' })).toBeInTheDocument()

    await act(async () => {
      resolverCheckout(comprobanteEmitidoFixture())
      await Promise.resolve()
    })
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('clic en "Finalizar (F9)" del diálogo cobra exactamente una vez', async () => {
    await armarVentaLista()

    fireEvent.keyDown(document, { key: 'F9' })
    const dialogo = within(await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' }))

    await userEvent.click(dialogo.getByRole('button', { name: /Finalizar/ }))

    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(1)
  })

  it('F10 cierra el diálogo sin cobrar y devuelve el foco al importe', async () => {
    await armarVentaLista()
    const importe = screen.getByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    importe.focus()

    fireEvent.keyDown(document, { key: 'F9' })
    await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })

    fireEvent.keyDown(document, { key: 'F10' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(0)
    expect(importe).toHaveFocus()
  })

  it('Escape cierra el diálogo sin cobrar y devuelve el foco al importe', async () => {
    await armarVentaLista()
    const importe = screen.getByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    importe.focus()

    fireEvent.keyDown(document, { key: 'F9' })
    await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(0)
    expect(importe).toHaveFocus()
  })

  it('clic en "Cancelar (F10)" cierra el diálogo sin cobrar y devuelve el foco al importe', async () => {
    await armarVentaLista()
    const importe = screen.getByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)
    importe.focus()

    fireEvent.keyDown(document, { key: 'F9' })
    const dialogo = within(await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' }))

    await userEvent.click(dialogo.getByRole('button', { name: /Cancelar/ }))

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(0)
    expect(importe).toHaveFocus()
  })

  it('clic directo en "Cobrar" cobra de inmediato, sin pasar por el diálogo de confirmación', async () => {
    await armarVentaLista()

    await userEvent.click(screen.getByRole('button', { name: 'Cobrar' }))

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(await screen.findByText('Venta 0007-00000001')).toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(1)
  })

  it('F9 no hace nada con el carrito vacío (Cobrar deshabilitado por otro motivo)', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })

    fireEvent.keyDown(document, { key: 'F9' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('F9 no hace nada con el turno cerrado', async () => {
    mockearApiGet((ruta) => {
      if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve(null)
      return undefined
    })
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await screen.findByText('Caja cerrada')

    fireEvent.keyDown(document, { key: 'F9' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('F9 no hace nada mientras el cobro ya está en curso', async () => {
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
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cobrando…' })).toBeInTheDocument())

    fireEvent.keyDown(document, { key: 'F9' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(apiPostMock.mock.calls.filter((llamada) => llamada[0] === '/ventas')).toHaveLength(1)

    await act(async () => {
      resolverCheckout(comprobanteEmitidoFixture())
      await Promise.resolve()
    })
  })

  it('F9 no abre nada mientras el buscador de artículos está abierto', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.click(screen.getByRole('button', { name: 'Buscar artículo' }))
    await screen.findByRole('dialog', { name: 'Buscar artículo' })

    fireEvent.keyDown(document, { key: 'F9' })

    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Buscar artículo' })).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba (K1, judgment-day ronda 1, CRITICAL): el listener de F9 lee sus valores
   * SIEMPRE frescos vía `f9Ref` — no una closure vieja capturada la última vez que el efecto se
   * re-suscribió. Mutación aplicada manualmente: volver a cerrar el listener sobre `filasPago`
   * directamente (deps `[..., puedeCobrar]`, sin `f9Ref`) → este test pasa a rojo (enfoca la fila 1
   * vieja en vez de la fila 2 recién agregada). Revertido, vuelve a verde.
   */
  it('K1: agregar una fila de pago vacía después de que el listener de F9 quedó registrado — F9 sigue enfocando la fila correcta', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`), '50')

    // Agregar la fila 2 (vacía) NO cambia `pagosCubrenElTotal` ni `puedeCobrar` (siguen en
    // `false`) — antes del fix, ningún dep de la lista vieja cambiaba, así que el efecto no se
    // volvía a suscribir y F9 seguía closureando `filasPago` de cuando había una sola fila.
    await userEvent.click(screen.getByRole('button', { name: '+ Agregar medio de pago' }))
    const importeFila2 = document.getElementById('pos-fila-pago-importe-2')
    expect(importeFila2).not.toBeNull()

    fireEvent.keyDown(document, { key: 'F9' })

    expect(importeFila2).toHaveFocus()
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('K1: quitar una fila de pago no deja a F9 apuntando a una fila que ya no existe', async () => {
    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await waitFor(() => expect(screen.getByText('$ 100,00', { selector: 'strong' })).toBeInTheDocument())
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`), '50')
    await userEvent.click(screen.getByRole('button', { name: '+ Agregar medio de pago' }))

    const botonesQuitar = screen.getAllByRole('button', { name: 'Quitar medio de pago' })
    await userEvent.click(botonesQuitar[botonesQuitar.length - 1])
    expect(document.getElementById('pos-fila-pago-importe-2')).toBeNull()

    fireEvent.keyDown(document, { key: 'F9' })

    expect(screen.getByLabelText(`Importe de ${medioEfectivo.nombre} (fila 1)`)).toHaveFocus()
    expect(screen.queryByRole('alertdialog', { name: '¿Finalizar venta?' })).not.toBeInTheDocument()
  })

  it('K3: mientras el diálogo está abierto, Tab/Shift+Tab quedan atrapados entre sus dos botones', async () => {
    await armarVentaLista()
    fireEvent.keyDown(document, { key: 'F9' })
    const dialogo = await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })
    const finalizar = within(dialogo).getByRole('button', { name: /Finalizar/ })
    const cancelar = within(dialogo).getByRole('button', { name: /Cancelar/ })

    expect(cancelar).toHaveFocus()

    fireEvent.keyDown(dialogo, { key: 'Tab' })
    expect(finalizar).toHaveFocus()

    fireEvent.keyDown(dialogo, { key: 'Tab' })
    expect(cancelar).toHaveFocus()

    fireEvent.keyDown(dialogo, { key: 'Tab', shiftKey: true })
    expect(finalizar).toHaveFocus()
  })

  it('K3: mientras el diálogo está abierto, "Cerrar caja" queda inerte', async () => {
    await armarVentaLista()
    fireEvent.keyDown(document, { key: 'F9' })
    await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' })

    expect(screen.getByRole('button', { name: 'Cerrar caja' })).toBeDisabled()
  })

  it('K4: con la vista previa de precios fallida, el diálogo no inventa "$ 0,00" — muestra el aviso de total no disponible', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.reject(new Error('falló la resolución'))
      if (ruta === '/ventas') return Promise.resolve(comprobanteEmitidoFixture())
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    renderPos()
    await screen.findByRole('option', { name: /Consumidor Final/ })
    await userEvent.type(screen.getByLabelText('Código escaneado'), '7790001234567')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByText('Coca Cola 1L')
    await screen.findByText('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')

    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medioEfectivo.nombre)
    await userEvent.type(await screen.findByLabelText('Importe de Efectivo (fila 1)'), '500')
    await waitFor(() => expect(screen.getByRole('button', { name: /Cobrar/ })).toBeEnabled())

    fireEvent.keyDown(document, { key: 'F9' })

    const dialogo = within(await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' }))
    expect(dialogo.getByText(/Total no disponible/)).toBeInTheDocument()
    expect(dialogo.queryByText(/^Total: /)).not.toBeInTheDocument()
    expect(dialogo.queryByText('$ 0,00')).not.toBeInTheDocument()
  })

  it('K5: los botones del diálogo exponen su atajo con aria-keyshortcuts', async () => {
    await armarVentaLista()
    fireEvent.keyDown(document, { key: 'F9' })
    const dialogo = within(await screen.findByRole('alertdialog', { name: '¿Finalizar venta?' }))

    expect(dialogo.getByRole('button', { name: /Finalizar/ })).toHaveAttribute('aria-keyshortcuts', 'F9')
    expect(dialogo.getByRole('button', { name: /Cancelar/ })).toHaveAttribute('aria-keyshortcuts', 'F10')
  })
})
