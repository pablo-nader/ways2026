import { act, cleanup, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CatalogoPublico } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
import { claveDelCarrito } from './carrito'
import { CarritoProvider } from './CarritoProvider'
import { useCarrito } from './CarritoContext'
import { articuloPublico, catalogoPublico } from './datosDePrueba'
import { PanelCarrito } from './PanelCarrito'
import { Tienda } from './Tienda'

const obtenerVitrinaMock = vi.fn()
const obtenerCatalogoMock = vi.fn<() => Promise<CatalogoPublico>>()

vi.mock('../api/publico', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/publico')>()),
  clienteDePublico: { obtenerVitrina: () => obtenerVitrinaMock(), obtenerCatalogo: () => obtenerCatalogoMock() },
}))

const vitrina: VitrinaPublica = {
  nombre: 'Ferretería Sur',
  tagline: null,
  descripcion: null,
  colorAcento: null,
  tema: 'claro',
  tieneLogo: false,
  logoVersion: null,
  sucursales: [],
}

const martillo = articuloPublico(1, { nombre: 'Martillo', precio: 1500.5 })
const clavos = articuloPublico(2, { nombre: 'Clavos', precio: 2000, unidadVenta: 'Peso' })

async function verTienda() {
  obtenerVitrinaMock.mockResolvedValue(vitrina)
  obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo, clavos] }))
  const vista = render(<Tienda />)
  await screen.findByRole('heading', { level: 2, name: 'Martillo' })

  return vista
}

async function abrirElCarrito() {
  await userEvent.click(screen.getByRole('button', { name: /^Carrito/ }))

  return screen.getByRole('dialog', { name: 'Carrito' })
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  obtenerCatalogoMock.mockReset()
})

afterEach(() => {
  // Primero se desmonta: el desmontaje vacía los efectos pendientes y uno de ellos puede guardar el
  // carrito. Si el almacenamiento se limpiara antes, esa escritura le llegaría al test siguiente.
  cleanup()
  localStorage.clear()
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.body.style.overflow = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('panel del carrito con artículos', () => {
  async function conDosLineas() {
    await verTienda()
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))

    return abrirElCarrito()
  }

  it('lista cada línea con su precio, cantidad y subtotal, y el total del carrito', async () => {
    const panel = await conDosLineas()

    const lineas = within(panel).getAllByRole('listitem')
    expect(lineas).toHaveLength(2)
    expect(lineas[0]).toHaveTextContent('Martillo')
    expect(lineas[0]).toHaveTextContent('$ 1.500,50')
    expect(lineas[1]).toHaveTextContent('Clavos')
    expect(lineas[1]).toHaveTextContent('$ 2.000,00 por kg')
    expect(within(panel).getByText('Total').closest('p')).toHaveTextContent('$ 3.500,50')
    expect(screen.getByRole('button', { name: 'Carrito, 2 artículos' })).toBeInTheDocument()
  })

  it('cambiar la cantidad en el panel recalcula el subtotal y el total', async () => {
    const panel = await conDosLineas()

    await userEvent.click(within(panel).getByRole('button', { name: 'Agregar medio kilo más de Clavos' }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Agregar uno más de Martillo' }))

    const lineas = within(panel).getAllByRole('listitem')
    expect(lineas[0]).toHaveTextContent('$ 3.001,00')
    expect(lineas[1]).toHaveTextContent('$ 3.000,00')
    expect(within(panel).getByText('Total').closest('p')).toHaveTextContent('$ 6.001,00')
  })

  it('"Quitar" saca solo esa línea y, sin líneas, el panel vuelve al mensaje de carrito vacío', async () => {
    const panel = await conDosLineas()

    await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' }))
    expect(within(panel).getAllByRole('listitem')).toHaveLength(1)
    await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Clavos del carrito' }))

    expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Vaciar carrito' })).not.toBeInTheDocument()
  })

  it('"Finalizar pedido" está deshabilitado y explica que viene pronto', async () => {
    const panel = await conDosLineas()

    const finalizar = within(panel).getByRole('button', { name: 'Finalizar pedido' })
    expect(finalizar).toBeDisabled()
    expect(finalizar).toHaveAccessibleDescription('Muy pronto vas a poder enviar tu pedido desde acá.')
  })

  /**
   * jsdom no aplica el "focus fixup" del navegador (mandar el foco a `<body>` cuando se quita el
   * elemento enfocado): estas pruebas afirman el destino que el código elige a propósito, no
   * reproducen la pérdida de foco real.
   */
  describe('el foco al quitar una línea', () => {
    it('"Quitar" en la primera línea lleva el foco al "Quitar" de la siguiente', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' }))

      expect(within(panel).getAllByRole('listitem')).toHaveLength(1)
      expect(within(panel).getByRole('button', { name: 'Quitar Clavos del carrito' })).toHaveFocus()
    })

    it('"Quitar" en la última línea lleva el foco al "Quitar" de la anterior', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Clavos del carrito' }))

      expect(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' })).toHaveFocus()
    })

    it('"Quitar" en la única línea lleva el foco a "Cerrar"', async () => {
      const panel = await conDosLineas()
      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' }))

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Clavos del carrito' }))

      expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()
    })

    it('"−" desde la cantidad mínima quita la línea y mueve el foco igual que "Quitar"', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar uno de Martillo' }))
      expect(within(panel).getByRole('button', { name: 'Quitar Clavos del carrito' })).toHaveFocus()
      const menosClavos = within(panel).getByRole('button', { name: 'Quitar medio kilo de Clavos' })
      await userEvent.click(menosClavos)
      expect(menosClavos).toHaveFocus()
      await userEvent.click(menosClavos)

      expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()
    })

    it('"−" en la última de dos líneas lleva el foco al "Quitar" de la anterior', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar medio kilo de Clavos' }))
      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar medio kilo de Clavos' }))

      expect(within(panel).getAllByRole('listitem')).toHaveLength(1)
      expect(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' })).toHaveFocus()
    })
  })

  /**
   * Se prueba el panel solo, con un `alCerrar` que no mueve el foco: en la tienda, cerrar lleva el foco al
   * botón del carrito, y eso ya saca el foco del campo por su cuenta y aplicaría la cantidad aunque el
   * panel no lo hiciera.
   */
  it('Escape con una cantidad tipeada y sin aplicar la aplica y cierra', async () => {
    const guardadaDelMartillo = () =>
      (JSON.parse(localStorage.getItem(claveDelCarrito('sur.ways.app')) as string) as { idArticulo: number; cantidad: number }[]).find(
        (l) => l.idArticulo === 1,
      )?.cantidad
    localStorage.setItem(
      claveDelCarrito('sur.ways.app'),
      JSON.stringify([{ idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', cantidad: 1, imagenVersion: null }]),
    )
    const alCerrar = vi.fn()
    render(
      <CarritoProvider host="sur.ways.app">
        <PanelCarrito alCerrar={alCerrar} />
      </CarritoProvider>,
    )
    const campo = screen.getByRole('textbox', { name: 'Cantidad de Martillo' })
    await userEvent.clear(campo)
    await userEvent.type(campo, '3')
    expect(guardadaDelMartillo()).toBe(1)

    await userEvent.keyboard('{Escape}')

    expect(alCerrar).toHaveBeenCalledTimes(1)
    expect(guardadaDelMartillo()).toBe(3)
  })

  describe('vaciar el carrito', () => {
    it('pide confirmación: hasta confirmar no se pierde nada, y Cancelar vuelve al botón con el foco', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Vaciar carrito' }))

      expect(within(panel).getByText('¿Vaciar el carrito?')).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Cancelar' })).toHaveFocus()
      expect(within(panel).getAllByRole('listitem')).toHaveLength(2)
      await userEvent.click(within(panel).getByRole('button', { name: 'Cancelar' }))

      expect(within(panel).queryByText('¿Vaciar el carrito?')).not.toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Vaciar carrito' })).toHaveFocus()
      expect(within(panel).getAllByRole('listitem')).toHaveLength(2)
    })

    it('confirmar deja el carrito vacío, lo anuncia y lleva el foco a Cerrar', async () => {
      const panel = await conDosLineas()

      await userEvent.click(within(panel).getByRole('button', { name: 'Vaciar carrito' }))
      await userEvent.click(within(panel).getByRole('button', { name: 'Sí, vaciar' }))

      expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
      expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()
      expect(screen.getByRole('button', { name: 'Carrito, vacío' })).toBeInTheDocument()
      expect(document.querySelector('.publico-solo-lectura[aria-live="polite"]')).toHaveTextContent('Carrito vaciado')
    })
  })
})

describe('persistencia en el navegador', () => {
  it('el carrito se guarda bajo una clave con el host y sobrevive a recargar la tienda', async () => {
    const primera = await verTienda()
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
    await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))

    expect(localStorage.getItem(claveDelCarrito(window.location.host))).toContain('"cantidad":2')
    primera.unmount()
    await verTienda()

    expect(screen.getByRole('button', { name: 'Carrito, 1 artículo' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Cantidad de Martillo' })).toHaveValue('2')
  })

  it('un almacenamiento corrupto se ignora: la tienda abre con el carrito vacío', async () => {
    localStorage.setItem(claveDelCarrito(window.location.host), '{no es json')

    await verTienda()

    expect(screen.getByRole('button', { name: 'Carrito, vacío' })).toBeInTheDocument()
  })

  it('el carrito de otro host en el mismo navegador no se ve', async () => {
    localStorage.setItem(
      claveDelCarrito('otro.comercio.app'),
      JSON.stringify([{ idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', cantidad: 3, imagenVersion: null }]),
    )

    await verTienda()

    expect(screen.getByRole('button', { name: 'Carrito, vacío' })).toBeInTheDocument()
  })
})

describe('CarritoProvider', () => {
  beforeEach(() => localStorage.clear())

  function Sonda({ etiqueta }: { etiqueta: string }) {
    const { cantidad, agregar } = useCarrito()
    return (
      <div>
        <span data-testid={`cuenta-${etiqueta}`}>{cantidad}</span>
        <button type="button" onClick={() => agregar({ idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', imagenVersion: null })}>
          {`Agregar en ${etiqueta}`}
        </button>
      </div>
    )
  }

  function DosComercios() {
    const [montado, setMontado] = useState<'sur' | 'norte'>('sur')

    return (
      <>
        <button type="button" onClick={() => setMontado(montado === 'sur' ? 'norte' : 'sur')}>
          Cambiar
        </button>
        <CarritoProvider key={montado} host={`${montado}.ways.app`}>
          <Sonda etiqueta={montado} />
        </CarritoProvider>
      </>
    )
  }

  it('cada host tiene su carrito aunque compartan el navegador', async () => {
    render(<DosComercios />)

    await userEvent.click(screen.getByRole('button', { name: 'Agregar en sur' }))
    expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
    await userEvent.click(screen.getByRole('button', { name: 'Cambiar' }))
    expect(screen.getByTestId('cuenta-norte')).toHaveTextContent('0')
    await userEvent.click(screen.getByRole('button', { name: 'Cambiar' }))

    expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
  })

  it('agregar dos veces en el mismo instante suma una sola línea de cantidad 2', async () => {
    render(<CarritoProvider host="sur.ways.app"><Sonda etiqueta="sur" /></CarritoProvider>)
    const boton = screen.getByRole('button', { name: 'Agregar en sur' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(await screen.findByTestId('cuenta-sur')).toHaveTextContent('1')
    expect(JSON.parse(localStorage.getItem(claveDelCarrito('sur.ways.app')) as string)).toEqual([
      { idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', imagenVersion: null, cantidad: 2 },
    ])
  })

  it('si el almacenamiento no está disponible el carrito sigue funcionando en memoria', async () => {
    render(<CarritoProvider host="sur.ways.app" almacen={null}><Sonda etiqueta="sur" /></CarritoProvider>)

    await userEvent.click(screen.getByRole('button', { name: 'Agregar en sur' }))

    expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
  })

  describe('cambios desde otra pestaña (evento storage)', () => {
    const clave = claveDelCarrito('sur.ways.app')
    const martilloGuardado = { idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', cantidad: 3, imagenVersion: null }
    const clavosGuardados = { idArticulo: 2, nombre: 'Clavos', precio: 20, unidadVenta: 'Peso', cantidad: 1.5, imagenVersion: null }

    function avisarDeOtraPestana(key: string | null, storageArea: Storage = localStorage) {
      act(() => {
        window.dispatchEvent(new StorageEvent('storage', { key, storageArea }))
      })
    }

    function verCarrito() {
      return render(<CarritoProvider host="sur.ways.app"><Sonda etiqueta="sur" /></CarritoProvider>)
    }

    it('adopta el carrito que la otra pestaña guardó', () => {
      verCarrito()
      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('0')

      localStorage.setItem(clave, JSON.stringify([martilloGuardado, clavosGuardados]))
      avisarDeOtraPestana(clave)

      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('2')
    })

    it('lo que adopta está validado: se descartan las líneas inválidas', () => {
      verCarrito()

      localStorage.setItem(clave, JSON.stringify([martilloGuardado, { ...clavosGuardados, cantidad: -1 }]))
      avisarDeOtraPestana(clave)

      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
    })

    it('si la otra pestaña vació el carrito o limpió el almacenamiento, este también queda vacío', async () => {
      verCarrito()
      await userEvent.click(screen.getByRole('button', { name: 'Agregar en sur' }))
      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')

      localStorage.removeItem(clave)
      avisarDeOtraPestana(clave)
      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('0')

      await userEvent.click(screen.getByRole('button', { name: 'Agregar en sur' }))
      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
      localStorage.clear()
      avisarDeOtraPestana(null)
      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('0')
    })

    it('no escribe de vuelta lo que acaba de adoptar: sin eco entre pestañas', () => {
      verCarrito()
      localStorage.setItem(clave, JSON.stringify([martilloGuardado]))
      const escritura = vi.spyOn(Storage.prototype, 'setItem')

      avisarDeOtraPestana(clave)

      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('1')
      expect(escritura).not.toHaveBeenCalled()
      escritura.mockRestore()
    })

    it.each([
      ['la clave de otro comercio', claveDelCarrito('norte.ways.app'), undefined],
      ['otra clave cualquiera', 'otra.cosa', undefined],
      ['otro almacenamiento (sessionStorage)', clave, sessionStorage],
    ])('ignora un aviso de %s', (_nombre, key, area) => {
      verCarrito()
      localStorage.setItem(clave, JSON.stringify([martilloGuardado]))

      avisarDeOtraPestana(key, area)

      expect(screen.getByTestId('cuenta-sur')).toHaveTextContent('0')
    })

    it('al desmontar deja de escuchar', () => {
      const quitar = vi.spyOn(window, 'removeEventListener')
      const vista = verCarrito()

      vista.unmount()

      expect(quitar).toHaveBeenCalledWith('storage', expect.any(Function))
      quitar.mockRestore()
    })
  })
})
