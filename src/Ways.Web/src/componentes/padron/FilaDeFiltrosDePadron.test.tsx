import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { FilaDeFiltrosDePadron, type CeldaDeFiltroDePadron } from './FilaDeFiltrosDePadron'

type Filtros = { texto: string; entero: string; opcion: string; desde: number | null; hasta: number | null }

const VACIOS: Filtros = { texto: '', entero: '', opcion: '', desde: null, hasta: null }

const CELDAS: readonly CeldaDeFiltroDePadron<Filtros>[] = [
  { tipo: 'texto', clave: 'texto', etiqueta: 'nombre' },
  { tipo: 'entero', clave: 'entero', etiqueta: 'N°' },
  { tipo: 'opciones', clave: 'opcion', etiqueta: 'estado', opciones: [{ valor: 'true', etiqueta: 'Activo' }] },
  { tipo: 'rango', claveDesde: 'desde', claveHasta: 'hasta', etiqueta: 'Margen' },
  { tipo: 'vacia' },
  { tipo: 'acciones' },
]

function montar(sobrescribir: Partial<Parameters<typeof FilaDeFiltrosDePadron<Filtros>>[0]> = {}) {
  const onCambio = vi.fn()
  const onLimpiar = vi.fn()
  render(
    <table>
      <thead>
        <FilaDeFiltrosDePadron
          celdas={CELDAS}
          valores={VACIOS}
          onCambio={onCambio}
          onLimpiar={onLimpiar}
          hayFiltros
          deshabilitado={false}
          {...sobrescribir}
        />
      </thead>
    </table>,
  )

  return { onCambio, onLimpiar }
}

describe('FilaDeFiltrosDePadron', () => {
  it('rinde una celda por columna, en orden', () => {
    montar()

    expect(screen.getAllByRole('columnheader')).toHaveLength(CELDAS.length)
  })

  it('un texto avisa su clave y valor, y espera la demora', async () => {
    const usuario = userEvent.setup()
    const { onCambio } = montar()

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'a')

    expect(onCambio).toHaveBeenCalledWith({ texto: 'a' }, false)
  })

  it('un entero solo acepta dígitos', async () => {
    const usuario = userEvent.setup()
    const { onCambio } = montar()

    await usuario.type(screen.getByLabelText('Filtrar por N°'), '1a')

    expect(onCambio.mock.calls).toEqual([[{ entero: '1' }, false]])
  })

  it('un selector ofrece Todos y las opciones, y se aplica en el acto', async () => {
    const usuario = userEvent.setup()
    const { onCambio } = montar()

    expect(screen.getByRole('option', { name: 'Todos' })).toBeInTheDocument()
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')

    expect(onCambio).toHaveBeenCalledWith({ opcion: 'true' }, true)
  })

  it('un rango tiene dos campos, desde y hasta, y avisa cada uno por su clave', async () => {
    const usuario = userEvent.setup()
    const { onCambio } = montar()

    await usuario.type(screen.getByLabelText('Margen desde'), '5')
    await usuario.type(screen.getByLabelText('Margen hasta'), '9')

    expect(onCambio).toHaveBeenCalledWith({ desde: 5 }, false)
    expect(onCambio).toHaveBeenCalledWith({ hasta: 9 }, false)
  })

  it('los controles muestran el valor del borrador', () => {
    montar({ valores: { texto: 'ana', entero: '12', opcion: 'true', desde: 3, hasta: null } })

    expect(screen.getByLabelText('Filtrar por nombre')).toHaveValue('ana')
    expect(screen.getByLabelText('Filtrar por N°')).toHaveValue('12')
    expect(screen.getByLabelText('Filtrar por estado')).toHaveValue('true')
    expect(screen.getByLabelText('Margen desde')).toHaveValue('3,00')
  })

  it('Limpiar filtros avisa', async () => {
    const usuario = userEvent.setup()
    const { onLimpiar } = montar()

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(onLimpiar).toHaveBeenCalledTimes(1)
  })

  it('sin filtros puestos Limpiar filtros está deshabilitado', () => {
    montar({ hayFiltros: false })

    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('deshabilitado deja inertes todos los controles', () => {
    montar({ deshabilitado: true })

    for (const control of [
      screen.getByLabelText('Filtrar por nombre'),
      screen.getByLabelText('Filtrar por N°'),
      screen.getByLabelText('Filtrar por estado'),
      screen.getByLabelText('Margen desde'),
      screen.getByLabelText('Margen hasta'),
      screen.getByRole('button', { name: 'Limpiar filtros' }),
    ]) {
      expect(control).toBeDisabled()
    }
  })
})
