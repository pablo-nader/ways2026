import { useEffect, useState, type ReactNode } from 'react'
import { api, ErrorApi } from '../api/cliente'
import type { AlicuotaIvaListado, CondicionFiscalListado, TipoComprobanteListado } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
  type DefinicionDeFiltro,
  type ValorOrdenable,
} from '../componentes/grilla'

type ColumnaFiscal<T> = {
  clave: string
  etiqueta: string
  /** Lo que la celda muestra: es lo que filtra la columna y, sin `orden`, lo que la ordena. */
  texto: (fila: T) => string
  /** Valor crudo por el que se ordena, cuando el texto no alcanza (números y vacíos). */
  orden?: (fila: T) => ValorOrdenable
  opciones?: DefinicionDeFiltro['opciones']
  celda?: (fila: T) => ReactNode
}

const OPCIONES_SI_NO = [
  { valor: 'Sí', etiqueta: 'Sí' },
  { valor: 'No', etiqueta: 'No' },
]
const OPCIONES_DE_ESTADO = [
  { valor: 'Activo', etiqueta: 'Activo' },
  { valor: 'Inactivo', etiqueta: 'Inactivo' },
]
const OPCIONES_DE_CLASE = [
  { valor: 'Venta', etiqueta: 'Venta' },
  { valor: 'Compra', etiqueta: 'Compra' },
]
const OPCIONES_DE_SIGNO = [
  { valor: '+1', etiqueta: '+1' },
  { valor: '−1', etiqueta: '−1' },
]

const siNo = (valor: boolean) => (valor ? 'Sí' : 'No')
const textoDeEstado = (fila: { activo: boolean }) => (fila.activo ? 'Activo' : 'Inactivo')
const textoOGuion = (valor: string | number | null) => (valor === null ? '—' : String(valor))

function estadoDeActivo<T extends { activo: boolean }>(): ColumnaFiscal<T> {
  return {
    clave: 'estado',
    etiqueta: 'Estado',
    texto: textoDeEstado,
    opciones: OPCIONES_DE_ESTADO,
    celda: (fila) => <EtiquetaActivo activo={fila.activo} />,
  }
}

const COLUMNAS_DE_CONDICIONES: ColumnaFiscal<CondicionFiscalListado>[] = [
  { clave: 'codigo', etiqueta: 'Código', texto: (c) => c.codigo },
  { clave: 'nombre', etiqueta: 'Nombre', texto: (c) => c.nombre },
  { clave: 'codigoAfip', etiqueta: 'Código AFIP/ARCA', texto: (c) => textoOGuion(c.codigoAfip), orden: (c) => c.codigoAfip },
  estadoDeActivo(),
]

const COLUMNAS_DE_ALICUOTAS: ColumnaFiscal<AlicuotaIvaListado>[] = [
  { clave: 'nombre', etiqueta: 'Nombre', texto: (a) => a.nombre },
  { clave: 'porcentaje', etiqueta: 'Porcentaje', texto: (a) => `${a.porcentaje}%`, orden: (a) => a.porcentaje },
  { clave: 'codigoAfip', etiqueta: 'Código AFIP/ARCA', texto: (a) => textoOGuion(a.codigoAfip), orden: (a) => a.codigoAfip },
  estadoDeActivo(),
]

const COLUMNAS_DE_TIPOS: ColumnaFiscal<TipoComprobanteListado>[] = [
  { clave: 'clase', etiqueta: 'Clase', texto: (t) => (t.clase === 'Venta' ? 'Venta' : 'Compra'), opciones: OPCIONES_DE_CLASE },
  { clave: 'codigo', etiqueta: 'Código', texto: (t) => t.codigo },
  { clave: 'nombre', etiqueta: 'Nombre', texto: (t) => t.nombre },
  { clave: 'letra', etiqueta: 'Letra', texto: (t) => textoOGuion(t.letra), orden: (t) => t.letra },
  { clave: 'signo', etiqueta: 'Signo', texto: (t) => (t.signo > 0 ? '+1' : '−1'), orden: (t) => t.signo, opciones: OPCIONES_DE_SIGNO },
  { clave: 'discriminaIva', etiqueta: 'Discrimina IVA', texto: (t) => siNo(t.discriminaIva), opciones: OPCIONES_SI_NO },
  { clave: 'esFiscal', etiqueta: 'Fiscal', texto: (t) => siNo(t.esFiscal), opciones: OPCIONES_SI_NO },
  { clave: 'afectaStock', etiqueta: 'Afecta stock', texto: (t) => siNo(t.afectaStock), opciones: OPCIONES_SI_NO },
  estadoDeActivo(),
]

type ArmadoDeColumnas<T> = {
  columnas: readonly ColumnaFiscal<T>[]
  ordenables: ColumnasOrdenables<T>
  filtrables: ColumnasFiltrables<T>
  celdas: string[]
}

function armarColumnas<T>(columnas: readonly ColumnaFiscal<T>[]): ArmadoDeColumnas<T> {
  const ordenables: ColumnasOrdenables<T> = {}
  const filtrables: ColumnasFiltrables<T> = {}

  for (const columna of columnas) {
    ordenables[columna.clave] = { valor: columna.orden ?? columna.texto }
    filtrables[columna.clave] = { etiqueta: columna.etiqueta, valor: columna.texto, opciones: columna.opciones }
  }

  return { columnas, ordenables, filtrables, celdas: columnas.map((columna) => columna.clave) }
}

const ARMADO_DE_CONDICIONES = armarColumnas(COLUMNAS_DE_CONDICIONES)
const ARMADO_DE_ALICUOTAS = armarColumnas(COLUMNAS_DE_ALICUOTAS)
const ARMADO_DE_TIPOS = armarColumnas(COLUMNAS_DE_TIPOS)

/**
 * Los 3 catálogos fiscales son de solo lectura en esta etapa (ADR-11, gate #4): los define
 * la plataforma, no el tenant — sin POST/PUT/DELETE mapeados en la API a propósito.
 */
export function CatalogosFiscales() {
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[] | null>(null)
  const [alicuotas, setAlicuotas] = useState<AlicuotaIvaListado[] | null>(null)
  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true

    Promise.all([
      api.get<CondicionFiscalListado[]>('/catalogos-fiscales/condiciones-fiscales'),
      api.get<AlicuotaIvaListado[]>('/catalogos-fiscales/alicuotas-iva'),
      api.get<TipoComprobanteListado[]>('/catalogos-fiscales/tipos-comprobante'),
    ])
      .then(([c, a, t]) => {
        if (!vigente) return
        setCondiciones(c)
        setAlicuotas(a)
        setTipos(t)
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los catálogos fiscales.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargando = condiciones === null || alicuotas === null || tipos === null

  return (
    <div className="container-fluid py-4 d-flex flex-column gap-4">
      {error && <div className="alert alert-danger">{error}</div>}

      {cargando && !error ? (
        <Cargando />
      ) : (
        <>
          <Box titulo="Condiciones fiscales" variante="inverse">
            <p className="text-muted">Las define la plataforma. RI, Monotributo, Exento, Consumidor Final…</p>
            <TablaFiscal
              nombre="Condiciones fiscales"
              armado={ARMADO_DE_CONDICIONES}
              filas={condiciones ?? []}
            />
          </Box>

          <Box titulo="Alícuotas de IVA" variante="inverse">
            <TablaFiscal nombre="Alícuotas de IVA" armado={ARMADO_DE_ALICUOTAS} filas={alicuotas ?? []} />
          </Box>

          <Box titulo="Tipos de comprobante" variante="inverse">
            <TablaFiscal nombre="Tipos de comprobante" armado={ARMADO_DE_TIPOS} filas={tipos ?? []} />
          </Box>
        </>
      )}
    </div>
  )
}

/** Tabla de solo lectura con orden y un filtro por columna. No tiene columna de acciones, así que el botón de limpiar
 * filtros va arriba de la tabla y no dentro de la fila de filtros. */
function TablaFiscal<T extends { id: number }>({
  nombre,
  armado,
  filas,
}: {
  nombre: string
  armado: ArmadoDeColumnas<T>
  filas: readonly T[]
}) {
  const { columnas } = armado
  const { filas: filtradas, filtros, cambiar, limpiar, hayFiltros } = useFiltrosDeColumna(filas, armado.filtrables)
  const { filas: visibles, orden, alternar } = useOrdenamiento(filtradas, armado.ordenables)

  return (
    <div className="table-responsive">
      <div className="d-flex justify-content-end mb-2">
        <BotonIcono icono="limpiar" etiqueta={`Limpiar filtros de ${nombre.toLowerCase()}`} onClick={limpiar} disabled={!hayFiltros} />
      </div>
      <table aria-label={nombre} className="table table-striped table-bordered align-middle">
        <thead>
          <tr>
            {columnas.map((columna) => (
              <EncabezadoOrdenable key={columna.clave} columna={columna.clave} orden={orden} onAlternar={alternar}>
                {columna.etiqueta}
              </EncabezadoOrdenable>
            ))}
          </tr>
          <FilaDeFiltros
            celdas={armado.celdas}
            columnas={armado.filtrables}
            filtros={filtros}
            onCambio={cambiar}
            onLimpiar={limpiar}
            hayFiltros={hayFiltros}
            conLimpiar={false}
          />
        </thead>
        <tbody>
          {visibles.map((fila) => (
            <tr key={fila.id}>
              {columnas.map((columna) => (
                <td key={columna.clave}>{columna.celda ? columna.celda(fila) : columna.texto(fila)}</td>
              ))}
            </tr>
          ))}
          {visibles.length === 0 && (
            <tr>
              <td colSpan={columnas.length} className="text-center text-muted py-4">
                {filas.length === 0 ? 'No hay registros cargados.' : 'Ningún registro coincide con los filtros.'}
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

function EtiquetaActivo({ activo }: { activo: boolean }) {
  return (
    <span className={`badge ${activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
      {activo ? 'Activo' : 'Inactivo'}
    </span>
  )
}
