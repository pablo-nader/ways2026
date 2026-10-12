import { useState } from 'react'
import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { combinarResultados, filtrarOpciones, MAXIMO_DE_OPCIONES_VISIBLES, SelectorConBusqueda, sumarRemotas } from './SelectorConBusqueda'
import type { OpcionDeSelector } from './SelectorConBusqueda'

type Opcion = OpcionDeSelector & { origen?: string }

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

const OPCIONES: Opcion[] = [
  { id: 1, etiqueta: '#1 — Consumidor Final' },
  { id: 2, etiqueta: '#2 — José Pérez' },
  { id: 3, etiqueta: '#3 — Ana Gómez' },
  { id: 4, etiqueta: '#4 — Perez Hermanos S.A.' },
]

type PropsDePrueba = Partial<Parameters<typeof SelectorConBusqueda<Opcion>>[0]> & { inicial?: number | null }

/** Contenedor con el estado del valor, como lo usaría una pantalla: el selector no guarda el valor. */
function Montaje({ inicial = 1, onChange, ...resto }: PropsDePrueba) {
  const [valor, setValor] = useState<number | string | null>(inicial)
  return (
    <SelectorConBusqueda<Opcion>
      nombreAccesible="Cliente"
      opciones={OPCIONES}
      valor={valor}
      onChange={(o) => {
        setValor(o.id)
        onChange?.(o)
      }}
      {...resto}
    />
  )
}

function caja() {
  return screen.getByRole('combobox', { name: 'Cliente' })
}

function etiquetasVisibles() {
  return screen.queryAllByRole('option').map((o) => o.textContent)
}

describe('combinarResultados / sumarRemotas / filtrarOpciones', () => {
  it('filtrarOpciones: contiene, sin distinguir mayúsculas ni acentos; vacío devuelve todo', () => {
    expect(filtrarOpciones(OPCIONES, 'jose').map((o) => o.id)).toEqual([2])
    expect(filtrarOpciones(OPCIONES, 'PEREZ').map((o) => o.id)).toEqual([2, 4])
    expect(filtrarOpciones(OPCIONES, 'ez her').map((o) => o.id)).toEqual([4])
    expect(filtrarOpciones(OPCIONES, '   ')).toHaveLength(4)
  })

  it('combinarResultados: respeta el orden de las primeras y agrega solo las siguientes que faltan', () => {
    const primeras: Opcion[] = [
      { id: 3, etiqueta: 'tres' },
      { id: 1, etiqueta: 'uno' },
    ]
    const siguientes: Opcion[] = [
      { id: 1, etiqueta: 'uno bis' },
      { id: 2, etiqueta: 'dos' },
      { id: 2, etiqueta: 'dos bis' },
    ]
    expect(combinarResultados(primeras, siguientes).map((o) => o.etiqueta)).toEqual(['tres', 'uno', 'dos'])
  })

  it('sumarRemotas: las locales conservan su posición, las remotas nuevas van detrás y, para el mismo id, mandan los datos remotos', () => {
    const locales: Opcion[] = [
      { id: 2, etiqueta: 'local 2' },
      { id: 3, etiqueta: 'local 3' },
    ]
    const remotas: Opcion[] = [
      { id: 9, etiqueta: 'remoto 9' },
      { id: 3, etiqueta: 'remoto 3', origen: 'servidor' },
    ]
    expect(sumarRemotas(locales, remotas)).toEqual([
      { id: 2, etiqueta: 'local 2' },
      { id: 3, etiqueta: 'remoto 3', origen: 'servidor' },
      { id: 9, etiqueta: 'remoto 9' },
    ])
  })
})

describe('SelectorConBusqueda', () => {
  it('cerrado muestra la etiqueta de la opción elegida y no dibuja la lista', () => {
    render(<Montaje inicial={2} />)
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(caja()).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('se ve como un select de Bootstrap', () => {
    render(<Montaje />)
    expect(caja()).toHaveClass('form-select')
  })

  it('un clic abre la lista completa con la opción elegida como activa', async () => {
    render(<Montaje inicial={3} />)
    await userEvent.click(caja())
    expect(screen.getByRole('listbox', { name: 'Opciones de Cliente' })).toBeInTheDocument()
    expect(caja()).toHaveAttribute('aria-expanded', 'true')
    expect(etiquetasVisibles()).toEqual(OPCIONES.map((o) => o.etiqueta))
    const activa = screen.getByRole('option', { name: '#3 — Ana Gómez' })
    expect(activa).toHaveAttribute('aria-selected', 'true')
    expect(caja()).toHaveAttribute('aria-activedescendant', activa.id)
  })

  it('tipear filtra por "contiene", sin distinguir mayúsculas ni acentos, y no elige nada por sí solo', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.keyboard('JOSE')
    expect(etiquetasVisibles()).toEqual(['#2 — José Pérez'])
    await userEvent.clear(caja())
    await userEvent.keyboard('rez her')
    expect(etiquetasVisibles()).toEqual(['#4 — Perez Hermanos S.A.'])
    expect(onChange).not.toHaveBeenCalled()
  })

  it('al enfocar queda todo el texto seleccionado: lo que se tipea reemplaza la etiqueta', async () => {
    render(<Montaje inicial={2} />)
    await userEvent.click(caja())
    await userEvent.keyboard('gom')
    expect(caja()).toHaveValue('gom')
    expect(etiquetasVisibles()).toEqual(['#3 — Ana Gómez'])
  })

  it('Enter elige la opción activa; tras tipear, la primera coincidencia', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.keyboard('perez')
    await userEvent.keyboard('{Enter}')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith(OPCIONES[1])
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
  })

  it('las flechas mueven la opción activa con vuelta', async () => {
    const onChange = vi.fn()
    render(<Montaje inicial={1} onChange={onChange} />)
    await userEvent.click(caja())
    const activa = () => screen.getByRole('option', { selected: true }).textContent
    expect(activa()).toBe('#1 — Consumidor Final')
    await userEvent.keyboard('{ArrowDown}')
    expect(activa()).toBe('#2 — José Pérez')
    await userEvent.keyboard('{ArrowUp}{ArrowUp}')
    expect(activa()).toBe('#4 — Perez Hermanos S.A.')
    await userEvent.keyboard('{ArrowDown}')
    expect(activa()).toBe('#1 — Consumidor Final')
    expect(onChange).not.toHaveBeenCalled()
  })

  it('Home y End quedan para el cursor de texto: no mueven la opción activa', async () => {
    render(<Montaje inicial={1} />)
    await userEvent.click(caja())
    const activa = () => screen.getByRole('option', { selected: true }).textContent
    await userEvent.keyboard('{End}')
    expect(activa()).toBe('#1 — Consumidor Final')
    await userEvent.keyboard('{ArrowDown}{Home}')
    expect(activa()).toBe('#2 — José Pérez')
  })

  it('Enter durante una composición (IME o tecla muerta) no elige', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.keyboard('perez')
    fireEvent.keyDown(caja(), { key: 'Enter', isComposing: true })
    fireEvent.keyDown(caja(), { key: 'Enter', keyCode: 229 })
    expect(onChange).not.toHaveBeenCalled()
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    fireEvent.keyDown(caja(), { key: 'Enter' })
    expect(onChange).toHaveBeenCalledTimes(1)
  })

  it('si el control se deshabilita con la lista abierta, al volver a habilitarlo no reaparecen ni el texto ni la lista', async () => {
    const { rerender } = render(<Montaje />)
    await userEvent.click(caja())
    await userEvent.keyboard('perez')
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    rerender(<Montaje disabled />)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    rerender(<Montaje />)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#1 — Consumidor Final')
  })

  it('con la lista cerrada, ArrowDown la abre sin elegir', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    caja().focus()
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('Escape cierra, restaura la etiqueta elegida, no cambia el valor y avisa con alCancelar', async () => {
    const onChange = vi.fn()
    const alCancelar = vi.fn()
    render(<Montaje inicial={2} onChange={onChange} alCancelar={alCancelar} />)
    await userEvent.click(caja())
    await userEvent.keyboard('ana')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(onChange).not.toHaveBeenCalled()
    expect(alCancelar).toHaveBeenCalledTimes(1)
  })

  it('volver a abrir con el foco ya puesto (tras Escape) deja la etiqueta seleccionada, para que lo tipeado la reemplace', async () => {
    render(<Montaje inicial={2} />)
    await userEvent.click(caja())
    await userEvent.keyboard('{Escape}')
    const entrada = caja() as HTMLInputElement
    entrada.setSelectionRange(3, 3)
    fireEvent.click(entrada)
    expect(entrada.selectionStart).toBe(0)
    expect(entrada.selectionEnd).toBe('#2 — José Pérez'.length)
  })

  it('perder el foco sin elegir cierra y restaura la etiqueta, sin avisar con alCancelar', async () => {
    const onChange = vi.fn()
    const alCancelar = vi.fn()
    render(
      <>
        <Montaje inicial={2} onChange={onChange} alCancelar={alCancelar} />
        <button type="button">Afuera</button>
      </>,
    )
    await userEvent.click(caja())
    await userEvent.keyboard('ana')
    await userEvent.click(screen.getByRole('button', { name: 'Afuera' }))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(onChange).not.toHaveBeenCalled()
    expect(alCancelar).not.toHaveBeenCalled()
  })

  it('un clic fuera de cualquier control cierra la lista', async () => {
    render(<Montaje />)
    await userEvent.click(caja())
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    await userEvent.click(document.body)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('un clic sobre una opción la elige', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.click(screen.getByRole('option', { name: '#3 — Ana Gómez' }))
    expect(onChange).toHaveBeenCalledWith(OPCIONES[2])
    expect(caja()).toHaveValue('#3 — Ana Gómez')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('sin coincidencias muestra el texto de "sin resultados"', async () => {
    render(<Montaje textoSinResultados={(t) => `Nadie coincide con "${t}"`} />)
    await userEvent.click(caja())
    await userEvent.keyboard('zzz')
    expect(screen.queryAllByRole('option')).toHaveLength(0)
    expect(screen.getByText('Nadie coincide con "zzz"')).toBeInTheDocument()
  })

  it('deshabilitado no abre la lista', async () => {
    render(<Montaje disabled />)
    expect(caja()).toBeDisabled()
    await userEvent.click(caja())
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('un cambio del valor desde afuera se refleja en la caja cerrada', () => {
    const { rerender } = render(<SelectorConBusqueda<Opcion> nombreAccesible="Cliente" opciones={OPCIONES} valor={1} onChange={() => {}} />)
    expect(caja()).toHaveValue('#1 — Consumidor Final')
    rerender(<SelectorConBusqueda<Opcion> nombreAccesible="Cliente" opciones={OPCIONES} valor={4} onChange={() => {}} />)
    expect(caja()).toHaveValue('#4 — Perez Hermanos S.A.')
  })

  describe('lista larga', () => {
    const MUCHAS: Opcion[] = Array.from({ length: MAXIMO_DE_OPCIONES_VISIBLES + 20 }, (_, i) => ({ id: i + 1, etiqueta: `#${i + 1} — Cliente ${i + 1}` }))

    it('dibuja solo las primeras y avisa que hay que seguir escribiendo; el aviso no es una opción ni se puede activar', async () => {
      const onChange = vi.fn()
      render(<Montaje opciones={MUCHAS} onChange={onChange} />)
      await userEvent.click(caja())
      expect(screen.getAllByRole('option')).toHaveLength(MAXIMO_DE_OPCIONES_VISIBLES)
      const aviso = screen.getByText(/seguí escribiendo para acotar/)
      expect(aviso).toHaveTextContent(`primeras ${MAXIMO_DE_OPCIONES_VISIBLES} coincidencias`)
      expect(aviso).not.toHaveAttribute('role', 'option')
      await userEvent.click(aviso)
      await userEvent.keyboard('{ArrowUp}{ArrowUp}{ArrowDown}')
      expect(screen.getAllByRole('option', { selected: true })).toHaveLength(1)
      expect(onChange).not.toHaveBeenCalled()
    })

    it('con el tope alcanzado, el anuncio de lectores de pantalla dice que la lista está acotada en vez de contar resultados', async () => {
      render(<Montaje opciones={MUCHAS} />)
      await userEvent.click(caja())
      const anuncio = document.querySelector('[aria-live="polite"]')
      expect(anuncio).toHaveTextContent(`Se muestran las primeras ${MAXIMO_DE_OPCIONES_VISIBLES} coincidencias`)
      expect(anuncio).not.toHaveTextContent(`${MAXIMO_DE_OPCIONES_VISIBLES} resultados`)
    })

    it('por debajo del tope, el anuncio cuenta los resultados', async () => {
      render(<Montaje opciones={MUCHAS} />)
      await userEvent.click(caja())
      await userEvent.keyboard('Cliente 6')
      expect(document.querySelector('[aria-live="polite"]')).toHaveTextContent(/^\d+ resultados?\.$/)
    })

    it('al acotar por debajo del tope, el aviso desaparece', async () => {
      render(<Montaje opciones={MUCHAS} />)
      await userEvent.click(caja())
      await userEvent.keyboard('Cliente 6')
      expect(screen.queryByText(/seguí escribiendo para acotar/)).not.toBeInTheDocument()
      expect(screen.getAllByRole('option').length).toBeLessThanOrEqual(MAXIMO_DE_OPCIONES_VISIBLES)
    })
  })

  describe('posición y tamaño de la lista', () => {
    function simularCaja(rect: Partial<DOMRect>) {
      const completo = { top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0, x: 0, y: 0, toJSON: () => ({}), ...rect }
      vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(completo as DOMRect)
    }

    it('con lugar abajo, se ancla bajo la caja y su alto máximo entra en la ventana', async () => {
      vi.stubGlobal('innerHeight', 768)
      simularCaja({ top: 40, bottom: 70, left: 20, width: 200 })
      render(<Montaje />)
      await userEvent.click(caja())
      const lista = screen.getByRole('listbox').parentElement as HTMLElement
      expect(lista.style.top).toBe('70px')
      expect(lista.style.bottom).toBe('')
      expect(Number.parseFloat(lista.style.maxHeight)).toBeLessThanOrEqual(768 - 70)
      expect(lista).toHaveStyle({ overflowY: 'auto' })
    })

    it('sin lugar abajo y con más arriba, se abre hacia arriba sin pasarse del borde superior', async () => {
      vi.stubGlobal('innerHeight', 400)
      simularCaja({ top: 330, bottom: 360, left: 20, width: 200 })
      render(<Montaje />)
      await userEvent.click(caja())
      const lista = screen.getByRole('listbox').parentElement as HTMLElement
      expect(lista.style.top).toBe('')
      expect(lista.style.bottom).toBe('70px')
      expect(Number.parseFloat(lista.style.maxHeight)).toBeLessThanOrEqual(330)
    })

    it('un scroll dentro de la lista no la cierra, pero uno de la página sí', async () => {
      render(<Montaje />)
      await userEvent.click(caja())
      fireEvent.scroll(screen.getByRole('listbox'))
      expect(screen.getByRole('listbox')).toBeInTheDocument()
      fireEvent.scroll(document.body)
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    })
  })

  describe('búsqueda remota', () => {
    const ESPERA = 300

    function respuestaControlable() {
      const pedidos: { texto: string; resolver: (o: Opcion[]) => void; rechazar: (e: Error) => void }[] = []
      const alBuscar = vi.fn(
        (texto: string) =>
          new Promise<Opcion[]>((resolver, rechazar) => {
            pedidos.push({ texto, resolver, rechazar })
          }),
      )
      return { alBuscar, pedidos }
    }

    /** Relojes falsos: la espera de tipeo y las respuestas tardías se controlan sin dormir. */
    function preparar() {
      vi.useFakeTimers()
      // Testing Library detecta los relojes falsos por el global `jest`: sin él, esperar a user-event cuelga.
      vi.stubGlobal('jest', { advanceTimersByTime: vi.advanceTimersByTime })
      return userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    }

    const avanzar = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms))

    it('espera a que se deje de tipear y consulta una sola vez con el texto final', async () => {
      const usuario = preparar()
      const { alBuscar } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('pere')
      await avanzar(ESPERA - 1)
      expect(alBuscar).not.toHaveBeenCalled()
      await avanzar(1)
      expect(alBuscar).toHaveBeenCalledTimes(1)
      expect(alBuscar).toHaveBeenCalledWith('pere')
    })

    it('sin texto no consulta', async () => {
      const usuario = preparar()
      const { alBuscar } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await avanzar(ESPERA * 10)
      expect(alBuscar).not.toHaveBeenCalled()
    })

    it('mientras consulta muestra "Buscando…" y sigue ofreciendo las coincidencias locales', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      expect(screen.getByText('Buscando…')).toBeInTheDocument()
      expect(etiquetasVisibles()).toEqual(['#2 — José Pérez', '#4 — Perez Hermanos S.A.'])
      await avanzar(ESPERA)
      await act(async () => pedidos[0].resolver([]))
      expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
    })

    it('las remotas se agregan detrás de lo ya mostrado; para el mismo id conserva su lugar pero toma los datos remotos', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await act(async () =>
        pedidos[0].resolver([
          { id: 9, etiqueta: '#9 — Perez Lejano' },
          { id: 4, etiqueta: '#4 — Perez Hermanos (servidor)' },
        ]),
      )
      expect(etiquetasVisibles()).toEqual(['#2 — José Pérez', '#4 — Perez Hermanos (servidor)', '#9 — Perez Lejano'])
    })

    it('una respuesta tardía no cambia lo que Enter elige: la opción activa por flechas se conserva', async () => {
      const usuario = preparar()
      const onChange = vi.fn()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} onChange={onChange} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await usuario.keyboard('{ArrowDown}')
      expect(screen.getByRole('option', { selected: true })).toHaveTextContent('#4 — Perez Hermanos S.A.')
      await avanzar(ESPERA)
      await act(async () =>
        pedidos[0].resolver([
          { id: 9, etiqueta: '#9 — Perez Lejano' },
          { id: 5, etiqueta: '#5 — Perez Nuevo' },
        ]),
      )
      expect(screen.getByRole('option', { selected: true })).toHaveTextContent('#4 — Perez Hermanos S.A.')
      await usuario.keyboard('{Enter}')
      expect(onChange).toHaveBeenCalledWith(OPCIONES[3])
    })

    it('sin tocar flechas, una respuesta tardía tampoco cambia la opción que Enter elige', async () => {
      const usuario = preparar()
      const onChange = vi.fn()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} onChange={onChange} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      expect(screen.getByRole('option', { selected: true })).toHaveTextContent('#2 — José Pérez')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }]))
      await usuario.keyboard('{Enter}')
      expect(onChange).toHaveBeenCalledWith(OPCIONES[1])
    })

    it('buscarLocal aporta coincidencias que la etiqueta no tiene, al instante y sin esperar al servidor', async () => {
      const usuario = preparar()
      const { alBuscar } = respuestaControlable()
      const buscarLocal = (t: string): Opcion[] => (t === '2030' ? [{ id: 3, etiqueta: '#3 — Ana Gómez' }] : [])
      render(<Montaje alBuscar={alBuscar} buscarLocal={buscarLocal} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('2030')
      expect(etiquetasVisibles()).toEqual(['#3 — Ana Gómez'])
    })

    it('una respuesta que llega tarde para un texto anterior se descarta', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('gom')
      await avanzar(ESPERA)
      await usuario.keyboard('ez')
      await avanzar(ESPERA)
      expect(pedidos).toHaveLength(2)
      await act(async () => pedidos[1].resolver([{ id: 7, etiqueta: '#7 — Gomez Nuevo' }]))
      await act(async () => pedidos[0].resolver([{ id: 8, etiqueta: '#8 — Gomi Viejo' }]))
      expect(etiquetasVisibles()).toContain('#7 — Gomez Nuevo')
      expect(etiquetasVisibles()).not.toContain('#8 — Gomi Viejo')
    })

    it('una respuesta que llega con la lista ya cerrada no la reabre ni aparece al volver a abrir', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await usuario.keyboard('{Escape}')
      await act(async () => pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }]))
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
      await usuario.click(caja())
      await usuario.keyboard('perez')
      expect(etiquetasVisibles()).not.toContain('#9 — Perez Lejano')
    })

    it('lo que trajo el servidor se olvida al cerrar: al volver a tipear lo mismo se vuelve a consultar y no se muestra lo viejo', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }]))
      expect(etiquetasVisibles()).toContain('#9 — Perez Lejano')

      await usuario.keyboard('{Escape}')
      await usuario.click(caja())
      await usuario.keyboard('perez')
      expect(etiquetasVisibles()).not.toContain('#9 — Perez Lejano')
      expect(screen.getByText('Buscando…')).toBeInTheDocument()
      await avanzar(ESPERA)
      expect(pedidos).toHaveLength(2)
    })

    it('vaciar el texto olvida lo que trajo el servidor: al tipear lo mismo en la misma apertura no reaparece lo viejo antes de la nueva respuesta', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }]))
      expect(etiquetasVisibles()).toContain('#9 — Perez Lejano')

      await usuario.keyboard('{Backspace>5/}')
      await usuario.keyboard('perez')
      expect(etiquetasVisibles()).not.toContain('#9 — Perez Lejano')
      expect(screen.getByText('Buscando…')).toBeInTheDocument()
      await avanzar(ESPERA)
      expect(pedidos).toHaveLength(2)
    })

    it('sin consulta remota (por ejemplo, sin red) desaparece lo que había traído antes', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      const { rerender } = render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }]))
      expect(etiquetasVisibles()).toContain('#9 — Perez Lejano')

      rerender(<Montaje alBuscar={undefined} esperaMs={ESPERA} />)
      expect(etiquetasVisibles()).not.toContain('#9 — Perez Lejano')
      expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
    })

    it('si la consulta falla y no hay coincidencias locales, muestra el mensaje del error', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('zzz')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].rechazar(new Error('No se pudo buscar.')))
      expect(screen.getByText('No se pudo buscar.')).toBeInTheDocument()
    })

    it('si la consulta falla pero hay coincidencias locales, las deja y no muestra el error', async () => {
      const usuario = preparar()
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      await avanzar(ESPERA)
      await act(async () => pedidos[0].rechazar(new Error('No se pudo buscar.')))
      expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
      expect(screen.queryByText('No se pudo buscar.')).not.toBeInTheDocument()
      expect(etiquetasVisibles()).toEqual(['#2 — José Pérez', '#4 — Perez Hermanos S.A.'])
    })

    it('sin alBuscar, tipear nunca muestra "Buscando…"', async () => {
      const usuario = preparar()
      render(<Montaje esperaMs={ESPERA} />)
      await usuario.click(caja())
      await usuario.keyboard('perez')
      expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
    })
  })
})
