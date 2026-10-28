import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect, useRef, useState } from 'react'
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

  it('etiquetaCerrar reemplaza el nombre accesible del botón de cerrar (sin chocar con un "Cerrar" del pie)', () => {
    render(
      <Modal
        titulo="Detalle"
        etiquetaCerrar="Cerrar detalle"
        pie={
          <button type="button" onClick={() => {}}>
            Cerrar
          </button>
        }
        onCerrar={() => {}}
      >
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.getAllByRole('button', { name: 'Cerrar' })).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Cerrar detalle' })).toBeInTheDocument()
  })

  it('la × del header es un SVG en línea con currentColor, sin depender del fondo data: de btn-close (bloqueado por la CSP del escritorio)', () => {
    render(
      <Modal titulo="Nueva marca" onCerrar={() => {}}>
        <p>contenido</p>
      </Modal>,
    )

    const boton = screen.getByRole('button', { name: 'Cerrar' })
    expect(boton).not.toHaveClass('btn-close')
    const icono = boton.querySelector('svg')
    expect(icono).not.toBeNull()
    expect(icono).toHaveAttribute('stroke', 'currentColor')
    expect(icono).toHaveAttribute('aria-hidden', 'true')
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

/**
 * Cláusula bajo prueba: `evento.defaultPrevented` en el listener de Escape de `Modal.tsx`. Un Escape que
 * otro control ya atendió no cierra el modal. El atendedor de la prueba solo llama `preventDefault` y NO corta
 * la propagación, así que el único freno es esa comprobación. Mutación: quitar la línea hace fallar el test
 * (`onCerrar` se invoca).
 */
describe('Modal — Escape ya atendido por otro control', () => {
  it('un Escape con defaultPrevented no cierra el modal; uno sin atender sí', () => {
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Editor" onCerrar={onCerrar}>
        <button type="button">dentro</button>
      </Modal>,
    )
    const atender = (evento: KeyboardEvent) => evento.preventDefault()
    document.addEventListener('keydown', atender, true)

    fireEvent.keyDown(screen.getByRole('button', { name: 'dentro' }), { key: 'Escape' })
    document.removeEventListener('keydown', atender, true)

    expect(onCerrar).not.toHaveBeenCalled()

    fireEvent.keyDown(screen.getByRole('button', { name: 'dentro' }), { key: 'Escape' })

    expect(onCerrar).toHaveBeenCalledTimes(1)
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

  /**
   * Cláusula bajo prueba: `focoPrevio !== document.body` en la condición del cleanup de
   * `Modal.tsx`. Sin ese descarte explícito, `esAlcanzable(document.body)` es `true` (body está
   * conectado, no está deshabilitado) y el cleanup restaura el foco ahí en vez de usar
   * `focoDeReserva` — mutation-proof-tests: reemplazar la condición por solo
   * `esAlcanzable(focoPrevio)` hace fallar el `expect` de abajo (el de reserva nunca recibe foco).
   */
  it('abierto sin disparador (URL directa / pestaña nueva), al cerrar devuelve el foco al destino de reserva, no a body', () => {
    function Arnes() {
      // Arranca cerrado y se abre solo vía efecto (nunca por un click) — igual que una URL
      // directa a /articulos/edit/5 abre el modal sin que nada lo haya clickeado: `document.
      // activeElement` sigue siendo `body`. `focoDeReserva` recibe el `RefObject` (no `.current`
      // ya resuelto) justamente para no depender de en qué commit exacto se adjuntó el ref.
      const [abierto, setAbierto] = useState(false)
      const refReserva = useRef<HTMLButtonElement>(null)
      useEffect(() => {
        setAbierto(true)
      }, [])
      return (
        <>
          <button ref={refReserva} type="button">
            Nuevo
          </button>
          {abierto && (
            <Modal titulo="Nueva marca" focoDeReserva={refReserva} onCerrar={() => setAbierto(false)}>
              <button type="button">Guardar</button>
            </Modal>
          )}
        </>
      )
    }

    render(<Arnes />)

    fireEvent.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(screen.getByRole('button', { name: 'Nuevo' })).toHaveFocus()
  })

  it('con el disparador ya fuera del documento al cerrar (se re-renderizó afuera), devuelve el foco al destino de reserva', () => {
    function Arnes() {
      const [abierto, setAbierto] = useState(false)
      const [disparadorPresente, setDisparadorPresente] = useState(true)
      const refReserva = useRef<HTMLButtonElement>(null)
      return (
        <>
          <button ref={refReserva} type="button">
            Nuevo
          </button>
          {disparadorPresente && (
            <button
              type="button"
              onClick={(e) => {
                e.currentTarget.focus()
                setAbierto(true)
              }}
            >
              Editar
            </button>
          )}
          {abierto && (
            <Modal titulo="Nueva marca" focoDeReserva={refReserva} onCerrar={() => setAbierto(false)}>
              <button type="button" onClick={() => setDisparadorPresente(false)}>
                Quitar disparador
              </button>
            </Modal>
          )}
        </>
      )
    }

    render(<Arnes />)
    fireEvent.click(screen.getByRole('button', { name: 'Editar' }))
    // Simula que la fila que tenía el disparador desapareció de la grilla (p. ej. un refresco en
    // curso) mientras el modal seguía abierto.
    fireEvent.click(screen.getByRole('button', { name: 'Quitar disparador' }))
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(screen.getByRole('button', { name: 'Nuevo' })).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: la guarda `if (!restaurarFoco) return` del cleanup de `Modal.tsx`, antes
   * de cualquier restauración. Mutation-proof-tests: sacándola, el disparador recupera el foco al
   * cerrar y falla el primer `expect`; dejándola cubrir solo `focoPrevio` (sin el destino de
   * reserva), el foco termina en "Nuevo" y falla el segundo.
   */
  it('con restaurarFoco={false}, el cierre no toca el foco: ni el disparador ni el destino de reserva', () => {
    function Arnes() {
      const [abierto, setAbierto] = useState(false)
      const refReserva = useRef<HTMLButtonElement>(null)
      return (
        <>
          <button ref={refReserva} type="button">
            Nuevo
          </button>
          <button type="button" onClick={() => setAbierto(true)}>
            Abrir
          </button>
          {abierto && (
            <Modal
              titulo="Nueva marca"
              restaurarFoco={false}
              focoDeReserva={refReserva}
              onCerrar={() => setAbierto(false)}
            >
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

    expect(disparador).not.toHaveFocus()
    expect(screen.getByRole('button', { name: 'Nuevo' })).not.toHaveFocus()
    expect(document.body).toHaveFocus()
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

  /**
   * Cláusula bajo prueba: el chequeo `currentTarget.contains(target)` de `atraparTab`. Un modal
   * hijo en el árbol de React burbuja su Tab al `onKeyDown` del padre aunque viva en otro portal;
   * sin el chequeo el padre se llevaba el foco al primer control detrás del hijo.
   */
  it('con un modal apilado, el Tab del de arriba no lo atrapa el de abajo', () => {
    render(
      <Modal titulo="Editor" onCerrar={() => {}}>
        <button type="button">Control del editor</button>
        <Modal titulo="Confirmación" onCerrar={() => {}}>
          <button type="button">Aceptar</button>
        </Modal>
      </Modal>,
    )

    const aceptar = screen.getByRole('button', { name: 'Aceptar' })
    const cerrarInterno = within(screen.getByRole('dialog', { name: 'Confirmación' })).getByRole('button', {
      name: 'Cerrar',
    })
    const editor = screen.getByRole('button', { name: 'Control del editor' })

    aceptar.focus()
    fireEvent.keyDown(aceptar, { key: 'Tab' })

    expect(cerrarInterno).toHaveFocus()
    expect(editor).not.toHaveFocus()
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

/**
 * Cláusulas bajo prueba (mutation-proof-tests): la × del header condicionada a `!noDescartable`,
 * la rama `noDescartable` del handler de Escape y la del click en el fondo.
 */
describe('Modal — no descartable', () => {
  it('no renderiza la × de cerrar', () => {
    render(
      <Modal titulo="Monto retirado" noDescartable>
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.queryByRole('button', { name: 'Cerrar' })).not.toBeInTheDocument()
  })

  it('Escape se consume (preventDefault) y el modal sigue montado', () => {
    render(
      <Modal titulo="Monto retirado" noDescartable>
        <p>contenido</p>
      </Modal>,
    )

    const evento = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true, bubbles: true })
    document.dispatchEvent(evento)

    expect(evento.defaultPrevented).toBe(true)
    expect(screen.getByRole('dialog', { name: 'Monto retirado' })).toBeInTheDocument()
  })

  it('con un descartable debajo, Escape no cierra al de abajo: lo consume el tope no descartable', () => {
    const alCerrarDeAbajo = vi.fn()
    render(
      <>
        <Modal titulo="Abajo" onCerrar={alCerrarDeAbajo}>
          <p>abajo</p>
        </Modal>
        <Modal titulo="Arriba" noDescartable>
          <p>arriba</p>
        </Modal>
      </>,
    )

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(alCerrarDeAbajo).not.toHaveBeenCalled()
  })

  it('un modal descartable sigue mostrando la × y cerrando con Escape', () => {
    const onCerrar = vi.fn()
    render(
      <Modal titulo="Nueva marca" onCerrar={onCerrar}>
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeInTheDocument()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onCerrar).toHaveBeenCalledTimes(1)
  })
})

/**
 * Cláusula bajo prueba: `desplazable` agrega `modal-dialog-scrollable` — sin el prop (o con la
 * clase hardcodeada afuera de la condición) el body largo del modal no queda scrolleable con el
 * header fijo. Mutation-proof-tests: sacar `desplazable ? 'modal-dialog-scrollable' : ''` (dejar
 * la clase fija) hace fallar el primer `expect` de abajo; hardcodearla siempre puesta hace fallar
 * el segundo.
 */
describe('Modal — opt-in a diálogo con scroll (desplazable)', () => {
  it('sin la prop, no tiene la clase modal-dialog-scrollable', () => {
    render(
      <Modal titulo="Nueva marca" onCerrar={() => {}}>
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.getByRole('document')).not.toHaveClass('modal-dialog-scrollable')
  })

  it('con desplazable, el modal-dialog tiene la clase modal-dialog-scrollable', () => {
    render(
      <Modal titulo="Nueva marca" desplazable onCerrar={() => {}}>
        <p>contenido</p>
      </Modal>,
    )

    expect(screen.getByRole('document')).toHaveClass('modal-dialog-scrollable')
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
