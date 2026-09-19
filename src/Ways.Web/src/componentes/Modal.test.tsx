import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useRef, useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Modal } from './Modal'

beforeEach(() => {
  document.body.classList.remove('modal-open')
})

describe('Modal — estructura y accesibilidad', () => {
  it('se renderiza como dialog modal, con el título como nombre accesible', () => {
    render(
      <Modal titulo="Nueva marca" onCerrar={() => {}}>
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.getByRole('dialog', { name: 'Nueva marca' })).toHaveAttribute('aria-modal', 'true')
  })

  it('el botón de cerrar dispara onCerrar', async () => {
    const usuario = userEvent.setup()
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Nueva marca" onCerrar={onCerrar}>
        <p>contenido</p>
      </Modal>,
    )

    await usuario.click(screen.getByRole('button', { name: 'Cerrar' }))
    expect(onCerrar).toHaveBeenCalledTimes(1)
  })

  it('clickear el fondo (fuera del diálogo) dispara onCerrar', () => {
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Nueva marca" onCerrar={onCerrar}>
        <p>contenido</p>
      </Modal>,
    )

    fireEvent.click(screen.getByRole('dialog', { name: 'Nueva marca' }))
    expect(onCerrar).toHaveBeenCalledTimes(1)
  })

  it('clickear DENTRO del diálogo no dispara onCerrar (solo el fondo cierra)', async () => {
    const usuario = userEvent.setup()
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Nueva marca" onCerrar={onCerrar}>
        <p>contenido</p>
      </Modal>,
    )

    await usuario.click(screen.getByText('contenido'))
    expect(onCerrar).not.toHaveBeenCalled()
  })
})

/**
 * Cláusula bajo prueba: `pilaDeModales[pilaDeModales.length - 1] !== idPropio` en el listener de
 * Escape de `Modal.tsx`. Sin esa comparación (Escape cerrando cualquier modal montado, no solo el
 * tope), este primer test fallaría con `onCerrarA` también invocado. Evidencia de mutación
 * (mutation-proof-tests): con la condición reemplazada por `false` (Escape actúa siempre), el
 * primer `expect(onCerrarA).not.toHaveBeenCalled()` pasa a fallar — revertido, vuelve a verde.
 */
describe('Modal — pila de apilado (Escape solo cierra el tope)', () => {
  it('con dos modales abiertos, Escape cierra solo el de arriba; cerrado ese, Escape pasa a cerrar el que queda', () => {
    const onCerrarA = vi.fn()
    const onCerrarB = vi.fn()

    const { rerender } = render(
      <>
        <Modal titulo="Modal A" onCerrar={onCerrarA}>
          <button type="button">a</button>
        </Modal>
        <Modal titulo="Modal B" onCerrar={onCerrarB}>
          <button type="button">b</button>
        </Modal>
      </>,
    )

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onCerrarB).toHaveBeenCalledTimes(1)
    expect(onCerrarA).not.toHaveBeenCalled()

    // El padre real reacciona a onCerrar desmontando el modal — se simula acá con un rerender que
    // deja montado solo A, que pasa a ser el tope de la pila.
    rerender(
      <Modal titulo="Modal A" onCerrar={onCerrarA}>
        <button type="button">a</button>
      </Modal>,
    )

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onCerrarA).toHaveBeenCalledTimes(1)
  })
})

describe('Modal — foco', () => {
  it('al abrir, enfoca el primer control focusable del contenido', () => {
    render(
      <Modal titulo="Nueva marca" onCerrar={() => {}}>
        <input aria-label="Nombre" />
        <button type="button">Guardar</button>
      </Modal>,
    )

    // El primer focusable de todo el modal es el botón "Cerrar" del header (antes que "Nombre").
    expect(screen.getByRole('button', { name: 'Cerrar' })).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el "foco previo" se captura con el inicializador perezoso de `useState`
   * (durante el RENDER), no dentro del `useLayoutEffect` de montaje. Bug real encontrado armando
   * este componente: un consumidor enfoca a mano OTRO control (p. ej. un select) antes de abrir el
   * modal, pero si el CONTENIDO del modal tiene un campo con `autoFocus`, React se lo enfoca
   * durante la fase de mutación del mismo commit — ANTES que cualquier `useLayoutEffect` llegue a
   * correr. Capturar el foco previo en un `useLayoutEffect` (en vez de durante el render) lo hace
   * leer el propio campo `autoFocus` del modal como "foco anterior", y el select nunca lo
   * recupera al cerrar. Mutation-proof-tests: cambiar `useState(() => document.activeElement)`
   * por un `useLayoutEffect` que hace la misma asignación reproduce esta falla (el segundo
   * `expect` de este test pasa a fallar, devolviendo el foco al input en vez de al select).
   */
  it('con un select enfocado a mano antes de abrir, y un autoFocus DENTRO del modal, el cierre devuelve el foco al select — no al campo autoFocus', () => {
    function Arnes() {
      const [abierto, setAbierto] = useState(false)
      const refSelect = useRef<HTMLSelectElement>(null)
      return (
        <>
          <select ref={refSelect} aria-label="externo">
            <option value="a">a</option>
          </select>
          <button
            type="button"
            onClick={() => {
              refSelect.current?.focus()
              setAbierto(true)
            }}
          >
            Abrir
          </button>
          {abierto && (
            <Modal titulo="Nueva marca" onCerrar={() => setAbierto(false)}>
              <input aria-label="Nombre" autoFocus />
            </Modal>
          )}
        </>
      )
    }

    render(<Arnes />)
    fireEvent.click(screen.getByRole('button', { name: 'Abrir' }))

    // El autoFocus del contenido gana la apertura (comportamiento esperado, ver el comentario de
    // Modal.tsx) — la prueba real es qué pasa al CERRAR.
    expect(screen.getByLabelText('Nombre')).toHaveFocus()

    fireEvent.click(screen.getByRole('button', { name: 'Cerrar' }))
    expect(screen.getByLabelText('externo')).toHaveFocus()
  })

  it('devuelve el foco al elemento que lo tenía antes de abrir, al cerrar', () => {
    function Arnes() {
      const [abierto, setAbierto] = useState(false)
      return (
        <>
          <button type="button" onClick={() => setAbierto(true)}>
            Abrir
          </button>
          {abierto && (
            <Modal titulo="Nueva marca" onCerrar={() => setAbierto(false)}>
              <button type="button">Guardar</button>
            </Modal>
          )}
        </>
      )
    }

    render(<Arnes />)
    const disparador = screen.getByRole('button', { name: 'Abrir' })
    disparador.focus()
    fireEvent.click(disparador)

    fireEvent.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(disparador).toHaveFocus()
  })
})

/**
 * Cláusula bajo prueba: las dos ramas de `atraparTab` (envolver hacia adelante en el último
 * control, hacia atrás en el primero). Mutation-proof-tests: comentar el bloque `if (evento.
 * shiftKey) {...}` hace fallar el segundo `expect` de este test (Shift+Tab deja de volver a
 * "Guardar"); comentar la rama `else if` hace fallar el primero — ambos revertidos, verdes.
 */
describe('Modal — trampa de foco', () => {
  it('Tab en el último control vuelve al primero, y Shift+Tab en el primero va al último', () => {
    render(
      <Modal titulo="Nueva marca" onCerrar={() => {}}>
        <input aria-label="Nombre" />
        <button type="button">Guardar</button>
      </Modal>,
    )

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    const cerrar = screen.getByRole('button', { name: 'Cerrar' })

    guardar.focus()
    fireEvent.keyDown(guardar, { key: 'Tab' })
    expect(cerrar).toHaveFocus()

    cerrar.focus()
    fireEvent.keyDown(cerrar, { key: 'Tab', shiftKey: true })
    expect(guardar).toHaveFocus()
  })
})

describe('Modal — inerte mientras ocupado', () => {
  it('con ocupado, el botón de cerrar queda deshabilitado y ni Escape ni el click en el fondo cierran nada', () => {
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Nueva marca" ocupado onCerrar={onCerrar}>
        <p>Guardando…</p>
      </Modal>,
    )

    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()

    fireEvent.keyDown(document, { key: 'Escape' })
    fireEvent.click(screen.getByRole('dialog', { name: 'Nueva marca' }))

    expect(onCerrar).not.toHaveBeenCalled()
  })
})

describe('Modal — clase modal-open en <body>', () => {
  it('se agrega al abrir el primer modal y se quita recién al cerrar el último', () => {
    function Arnes() {
      const [abiertaA, setAbiertaA] = useState(true)
      const [abiertaB, setAbiertaB] = useState(false)
      return (
        <>
          {abiertaA && (
            <Modal titulo="A" onCerrar={() => setAbiertaA(false)}>
              <button type="button" onClick={() => setAbiertaB(true)}>
                Abrir B
              </button>
            </Modal>
          )}
          {abiertaB && (
            <Modal titulo="B" onCerrar={() => setAbiertaB(false)}>
              <p>b</p>
            </Modal>
          )}
        </>
      )
    }

    render(<Arnes />)
    expect(document.body).toHaveClass('modal-open')

    fireEvent.click(screen.getByRole('button', { name: 'Abrir B' }))
    expect(document.body).toHaveClass('modal-open')

    const botonesCerrar = () => screen.getAllByRole('button', { name: 'Cerrar' })
    fireEvent.click(botonesCerrar()[1])
    expect(document.body).toHaveClass('modal-open')

    fireEvent.click(botonesCerrar()[0])
    expect(document.body).not.toHaveClass('modal-open')
  })
})
