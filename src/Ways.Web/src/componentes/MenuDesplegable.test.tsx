import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { MenuDesplegable } from './MenuDesplegable'
import type { GrupoDeMenu } from './menu'

const administracion: GrupoDeMenu = {
  tipo: 'grupo',
  etiqueta: 'Administración',
  secciones: [
    {
      titulo: 'Catálogo',
      enlaces: [
        { tipo: 'enlace', etiqueta: 'Artículos', a: '/articulos' },
        { tipo: 'enlace', etiqueta: 'Marcas', a: '/catalogos/marcas' },
      ],
    },
    { titulo: 'Terceros', enlaces: [{ tipo: 'enlace', etiqueta: 'Clientes', a: '/clientes' }] },
  ],
}

/** Anfitrión con el estado de apertura levantado, como lo va a tener `Layout` (`grupoAbierto`);
 * el botón "Afuera" es el destino de foco y de pointerdown externo. */
function Anfitrion({ activo = false, alCerrar = () => {} }: { activo?: boolean; alCerrar?: () => void }) {
  const [abierto, setAbierto] = useState(false)

  return (
    <>
      <ul className="navbar-nav">
        <MenuDesplegable
          grupo={administracion}
          abierto={abierto}
          activo={activo}
          alAlternar={() => setAbierto((previo) => !previo)}
          alCerrar={() => {
            setAbierto(false)
            alCerrar()
          }}
        />
      </ul>
      <button type="button">Afuera</button>
    </>
  )
}

function renderMenu({ ruta = '/', activo = false, alCerrar }: { ruta?: string; activo?: boolean; alCerrar?: () => void } = {}) {
  return render(
    <MemoryRouter initialEntries={[ruta]}>
      <Anfitrion activo={activo} alCerrar={alCerrar} />
    </MemoryRouter>,
  )
}

const boton = () => screen.getByRole('button', { name: 'Administración' })
const enlace = (nombre: string) => screen.getByRole('link', { name: nombre })

describe('MenuDesplegable', () => {
  it('arranca cerrado: aria-expanded=false y los ítems no son alcanzables', () => {
    renderMenu()

    expect(boton()).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('link', { name: 'Artículos' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Catálogo' })).not.toBeInTheDocument()
  })

  it('un clic abre la lista que el botón controla y otro clic la cierra', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())

    expect(boton()).toHaveAttribute('aria-expanded', 'true')
    const lista = document.getElementById(boton().getAttribute('aria-controls') ?? '')
    expect(lista).not.toBeNull()
    expect(within(lista as HTMLElement).getByRole('link', { name: 'Artículos' })).toBeInTheDocument()

    await usuario.click(boton())

    expect(boton()).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('link', { name: 'Artículos' })).not.toBeInTheDocument()
  })

  it('abierto, muestra los encabezados de sección y un separador entre secciones', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())

    expect(screen.getByRole('heading', { name: 'Catálogo', level: 6 })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Terceros', level: 6 })).toBeInTheDocument()
    expect(screen.getAllByRole('separator')).toHaveLength(1)
  })

  it('Escape cierra y devuelve el foco al botón, aunque el foco estuviera en un ítem', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())
    await usuario.keyboard('{ArrowDown}')
    expect(enlace('Artículos')).toHaveFocus()

    await usuario.keyboard('{Escape}')

    expect(boton()).toHaveAttribute('aria-expanded', 'false')
    expect(boton()).toHaveFocus()
    expect(screen.queryByRole('link', { name: 'Artículos' })).not.toBeInTheDocument()
  })

  it('elegir un ítem avisa alCerrar', async () => {
    const usuario = userEvent.setup()
    const alCerrar = vi.fn()
    renderMenu({ alCerrar })

    await usuario.click(boton())
    await usuario.click(enlace('Artículos'))

    expect(alCerrar).toHaveBeenCalledTimes(1)
    expect(boton()).toHaveAttribute('aria-expanded', 'false')
  })

  /**
   * jsdom deja enfocar un elemento aunque esté dentro de un `hidden`, así que acá no se puede
   * probar que el foco se mueve DESPUÉS del commit que destapa la lista (lo que en un navegador
   * real es la diferencia entre enfocar y no enfocar nada); lo observable es el estado final.
   */
  it('ArrowDown desde el botón abre y enfoca el primer ítem', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.tab()
    expect(boton()).toHaveFocus()

    await usuario.keyboard('{ArrowDown}')

    expect(boton()).toHaveAttribute('aria-expanded', 'true')
    expect(enlace('Artículos')).toHaveFocus()
  })

  it('ArrowDown/ArrowUp recorren los ítems con vuelta y Home/End van a los extremos', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())
    await usuario.keyboard('{ArrowDown}')
    expect(enlace('Artículos')).toHaveFocus()

    await usuario.keyboard('{ArrowUp}')
    expect(enlace('Clientes')).toHaveFocus()

    await usuario.keyboard('{ArrowDown}')
    expect(enlace('Artículos')).toHaveFocus()

    await usuario.keyboard('{End}')
    expect(enlace('Clientes')).toHaveFocus()

    await usuario.keyboard('{Home}')
    expect(enlace('Artículos')).toHaveFocus()
    expect(boton()).toHaveAttribute('aria-expanded', 'true')
  })

  it('un pointerdown afuera cierra; uno adentro no', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())
    fireEvent.pointerDown(boton())
    expect(boton()).toHaveAttribute('aria-expanded', 'true')

    fireEvent.pointerDown(screen.getByRole('button', { name: 'Afuera' }))

    expect(boton()).toHaveAttribute('aria-expanded', 'false')
  })

  it('cuando el foco sale del menú con Tab, se cierra', async () => {
    const usuario = userEvent.setup()
    renderMenu()

    await usuario.click(boton())
    await usuario.tab()
    await usuario.tab()
    await usuario.tab()
    expect(enlace('Clientes')).toHaveFocus()
    expect(boton()).toHaveAttribute('aria-expanded', 'true')

    await usuario.tab()

    expect(screen.getByRole('button', { name: 'Afuera' })).toHaveFocus()
    expect(boton()).toHaveAttribute('aria-expanded', 'false')
  })

  it('el ítem de la ruta actual lleva aria-current="page" y los demás no', async () => {
    const usuario = userEvent.setup()
    renderMenu({ ruta: '/catalogos/marcas' })

    await usuario.click(boton())

    expect(enlace('Marcas')).toHaveAttribute('aria-current', 'page')
    expect(enlace('Artículos')).not.toHaveAttribute('aria-current')
    expect(enlace('Clientes')).not.toHaveAttribute('aria-current')
  })

  it('con activo, el botón se marca como la entrada activa de la barra', () => {
    renderMenu({ activo: true })

    expect(boton()).toHaveClass('active')
  })
})
