import { useEffect, useMemo, useRef, useState } from 'react'
import { createPortal, flushSync } from 'react-dom'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeEtiquetas, MAXIMO_DE_ARTICULOS_POR_HOJA } from '../api/etiquetas'
import type { DatosDeEtiquetas, ListaPrecioAsignable, PaginaDe, ProveedorListado } from '../api/tipos'
import { Modal } from '../componentes/Modal'
import { TituloDeSeccion } from '../componentes/TituloDeSeccion'
import { ETIQUETAS_POR_HOJA, esEtiquetasPorHoja, type EtiquetasPorHoja } from '../etiquetas/disposicionDeHojas'
import { HojasDeGondola } from '../etiquetas/HojasDeGondola'
import { PLANTILLAS_DE_GONDOLA, plantillaDeGondolaPorId } from '../etiquetas/plantillasDeGondola'
import { pluralizar } from '../formato/pluralizar'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { GrillaDeArticulos, type SeleccionDeGrilla } from './articulos/GrillaDeArticulos'
import { ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import {
  agregarPagina,
  alternarArticulo,
  filasParaImprimir,
  quitarPagina,
  SELECCION_VACIA,
  type Seleccion,
} from './etiquetas-gondola/candidatos'

type Vista = { clave: string; datos: DatosDeEtiquetas }

function mensajeDeError(e: unknown, porDefecto: string): string {
  return e instanceof ErrorApi ? e.message : porDefecto
}

function PantallaEtiquetasGondola() {
  const { puntoVenta } = usePuntoVenta()
  const idPuntoVenta = puntoVenta?.id ?? null

  const [listas, setListas] = useState<ListaPrecioAsignable[] | null>(null)
  const [errorListas, setErrorListas] = useState('')
  const [idLista, setIdLista] = useState<number | ''>('')
  const [idPlantilla, setIdPlantilla] = useState(PLANTILLAS_DE_GONDOLA[0].id)
  const [porHoja, setPorHoja] = useState<EtiquetasPorHoja>(8)
  const [conOfertas, setConOfertas] = useState(false)
  const [proveedores, setProveedores] = useState<ProveedorListado[]>([])

  const [seleccion, setSeleccion] = useState<Seleccion>(SELECCION_VACIA)
  const [avisoDeTope, setAvisoDeTope] = useState(false)
  const [vistaAbierta, setVistaAbierta] = useState(false)

  const [vista, setVista] = useState<Vista | null>(null)
  const [cargandoVista, setCargandoVista] = useState(false)
  const [errorVista, setErrorVista] = useState('')

  // Cada lectura captura su generación; la respuesta de una generación vieja se descarta.
  const generacionDeVista = useRef(0)

  useEffect(() => {
    let vigente = true
    clienteDeClientes
      .listasDePrecioAsignables()
      .then((lista) => {
        if (!vigente) return
        setListas(lista)
        setIdLista((lista.find((l) => l.esDefault) ?? lista[0] ?? null)?.id ?? '')
      })
      .catch(() => {
        if (!vigente) return
        setListas([])
        setErrorListas('No se pudieron cargar las listas de precio.')
      })
    return () => {
      vigente = false
    }
  }, [])

  // Sin proveedores el filtro de la grilla solo ofrece "Todos" y "Sin proveedor": no bloquea la pantalla.
  useEffect(() => {
    let vigente = true
    api
      .get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200')
      .then((pagina) => {
        if (vigente) setProveedores(ordenarProveedoresPorEtiqueta(pagina.items))
      })
      .catch(() => undefined)
    return () => {
      vigente = false
    }
  }, [])

  const ids = useMemo(() => [...seleccion], [seleccion])
  const clave = `${idPuntoVenta ?? ''}|${idLista}|${ids.join(',')}`

  useEffect(() => {
    if (idPuntoVenta === null || idLista === '' || ids.length === 0) {
      generacionDeVista.current += 1
      setVista(null)
      setCargandoVista(false)
      setErrorVista('')
      return
    }
    const token = ++generacionDeVista.current
    setCargandoVista(true)
    setErrorVista('')

    clienteDeEtiquetas
      .datosDeArticulos(idPuntoVenta, idLista, ids)
      .then((datos) => {
        if (token === generacionDeVista.current) setVista({ clave, datos })
      })
      .catch((e: unknown) => {
        if (token !== generacionDeVista.current) return
        setVista(null)
        setErrorVista(mensajeDeError(e, 'No se pudo preparar la vista previa.'))
      })
      .finally(() => {
        if (token === generacionDeVista.current) setCargandoVista(false)
      })
  }, [idPuntoVenta, idLista, ids, clave])

  const plantilla = plantillaDeGondolaPorId(idPlantilla)
  const vistaVigente = vista !== null && vista.clave === clave ? vista : null
  const paraImprimir = vistaVigente ? filasParaImprimir(ids, vistaVigente.datos.filas, conOfertas) : null
  const puedeImprimir = !cargandoVista && paraImprimir !== null && paraImprimir.filas.length > 0

  const ofertaVigente = conOfertas && idPuntoVenta !== null && idLista !== '' ? { idPuntoVenta, idListaPrecio: idLista } : null

  const seleccionDeGrilla: SeleccionDeGrilla = {
    ids: seleccion,
    onAlternar: (fila) => {
      const resultado = alternarArticulo(seleccion, fila.id, MAXIMO_DE_ARTICULOS_POR_HOJA)
      setSeleccion(resultado.seleccion)
      setAvisoDeTope(resultado.excedido)
    },
    onAlternarPagina: (filas, marcar) => {
      const idsDeLaPagina = filas.map((fila) => fila.id)
      if (!marcar) {
        setSeleccion(quitarPagina(seleccion, idsDeLaPagina))
        setAvisoDeTope(false)
        return
      }
      const resultado = agregarPagina(seleccion, idsDeLaPagina, MAXIMO_DE_ARTICULOS_POR_HOJA)
      setSeleccion(resultado.seleccion)
      setAvisoDeTope(resultado.excedido)
    },
  }

  const [imprimiendo, setImprimiendo] = useState(false)

  useEffect(() => {
    const alEmpezar = () => flushSync(() => setImprimiendo(true))
    const alTerminar = () => setImprimiendo(false)
    window.addEventListener('beforeprint', alEmpezar)
    window.addEventListener('afterprint', alTerminar)
    return () => {
      window.removeEventListener('beforeprint', alEmpezar)
      window.removeEventListener('afterprint', alTerminar)
    }
  }, [])

  const imprimir = () => {
    flushSync(() => setImprimiendo(true))
    window.print()
    setImprimiendo(false)
  }

  const limpiarSeleccion = () => {
    setSeleccion(SELECCION_VACIA)
    setAvisoDeTope(false)
    setVistaAbierta(false)
  }

  const botonImprimir = (
    <button type="button" className="btn btn-primary" disabled={!puedeImprimir} onClick={imprimir}>
      Imprimir
    </button>
  )

  return (
    <div className="container-fluid py-3">
      <div className="d-print-none">
        <TituloDeSeccion nivel={2}>Etiquetas de góndola</TituloDeSeccion>
        <p className="text-muted">Elegí los artículos, la lista de precios y la plantilla; se imprime en A4 apaisado.</p>

        {idPuntoVenta === null && <div className="alert alert-warning">Elegí un punto de venta para preparar las etiquetas.</div>}
        {errorListas && <div className="alert alert-danger">{errorListas}</div>}

        <div className="row g-3 align-items-end mb-3">
          <div className="col-12 col-md-3">
            <label htmlFor="eg-lista" className="form-label">Lista de precios</label>
            <select id="eg-lista" className="form-select" value={idLista} disabled={listas === null || listas.length === 0} onChange={(e) => setIdLista(Number(e.target.value))}>
              {listas?.map((lista) => (
                <option key={lista.id} value={lista.id}>
                  {lista.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-12 col-md-3">
            <label htmlFor="eg-plantilla" className="form-label">Plantilla</label>
            <select id="eg-plantilla" className="form-select" value={idPlantilla} onChange={(e) => setIdPlantilla(e.target.value)}>
              {PLANTILLAS_DE_GONDOLA.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-6 col-md-2">
            <label htmlFor="eg-por-hoja" className="form-label">Etiquetas por hoja</label>
            <select
              id="eg-por-hoja"
              className="form-select"
              value={porHoja}
              onChange={(e) => {
                const valor = Number(e.target.value)
                if (esEtiquetasPorHoja(valor)) setPorHoja(valor)
              }}
            >
              {ETIQUETAS_POR_HOJA.map((cantidad) => (
                <option key={cantidad} value={cantidad}>
                  {cantidad}
                </option>
              ))}
            </select>
          </div>
          <div className="col-6 col-md-2">
            <div className="form-check">
              <input id="eg-ofertas" type="checkbox" className="form-check-input" checked={conOfertas} onChange={(e) => setConOfertas(e.target.checked)} />
              <label htmlFor="eg-ofertas" className="form-check-label">Ofertas</label>
            </div>
          </div>
          <div className="col-12 col-md-2 d-flex gap-2 justify-content-md-end">
            <button type="button" className="btn btn-outline-secondary" disabled={seleccion.size === 0} onClick={() => setVistaAbierta(true)}>
              Vista previa
            </button>
            {botonImprimir}
          </div>
        </div>

        <div className="d-flex flex-wrap align-items-center gap-3 mb-2">
          <span className="fw-semibold" aria-live="polite">
            {pluralizar(seleccion.size, 'seleccionado')}
          </span>
          <button type="button" className="btn btn-sm btn-outline-secondary" disabled={seleccion.size === 0} onClick={limpiarSeleccion}>
            Limpiar selección
          </button>
        </div>

        {avisoDeTope && (
          <div className="alert alert-warning py-2" role="alert">
            Se pueden elegir hasta {MAXIMO_DE_ARTICULOS_POR_HOJA} artículos por impresión: imprimí esta tanda y armá otra.
          </div>
        )}

        {(!conOfertas || ofertaVigente !== null) && (
          <GrillaDeArticulos proveedores={proveedores} seleccion={seleccionDeGrilla} ofertaVigente={ofertaVigente} />
        )}

        {errorVista && <div className="alert alert-danger py-2">{errorVista}</div>}
        {cargandoVista && <p className="text-muted">Preparando las etiquetas…</p>}
        {paraImprimir !== null && paraImprimir.omitidos > 0 && (
          <div className="alert alert-info py-2">
            {paraImprimir.omitidos === 1 ? '1 artículo elegido no se imprime' : `${paraImprimir.omitidos} artículos elegidos no se imprimen`}
            {conOfertas ? ': sin oferta vigente o sin precio en esta lista.' : ': sin precio en esta lista.'}
          </div>
        )}
        {seleccion.size === 0 && <p className="text-muted">Elegí al menos un artículo para ver la vista previa.</p>}
      </div>

      {vistaAbierta && (
        <Modal titulo="Vista previa" tamano="xl" desplazable etiquetaCerrar="Cerrar vista previa" onCerrar={() => setVistaAbierta(false)} pie={botonImprimir}>
          {cargandoVista && <p className="text-muted">Preparando las etiquetas…</p>}
          {errorVista && <div className="alert alert-danger py-2">{errorVista}</div>}
          {paraImprimir !== null && paraImprimir.filas.length > 0 ? (
            <HojasDeGondola filas={paraImprimir.filas} porHoja={porHoja} plantilla={plantilla} conOfertas={conOfertas} />
          ) : (
            !cargandoVista && <p className="text-muted">No hay etiquetas para imprimir con esta selección.</p>
          )}
        </Modal>
      )}

      {imprimiendo &&
        paraImprimir !== null &&
        paraImprimir.filas.length > 0 &&
        createPortal(
          <div className="gondola-impresion">
            <HojasDeGondola filas={paraImprimir.filas} porHoja={porHoja} plantilla={plantilla} conOfertas={conOfertas} />
          </div>,
          document.body,
        )}
    </div>
  )
}

export function EtiquetasGondola() {
  const { puntoVenta } = usePuntoVenta()
  return <PantallaEtiquetasGondola key={puntoVenta?.id ?? 'sin-pv'} />
}
