import { useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeEtiquetas, MAXIMO_DE_ARTICULOS_POR_HOJA } from '../api/etiquetas'
import type { DatosDeEtiquetas, ListaPrecioAsignable } from '../api/tipos'
import { PaginadorDeServidor } from '../componentes/grilla/PaginadorDeServidor'
import { TituloDeSeccion } from '../componentes/TituloDeSeccion'
import { ETIQUETAS_POR_HOJA, esEtiquetasPorHoja, type EtiquetasPorHoja } from '../etiquetas/disposicionDeHojas'
import { formatearPrecioDeGondola } from '../etiquetas/contenidoDeGondola'
import { HojasDeGondola } from '../etiquetas/HojasDeGondola'
import { PLANTILLAS_DE_GONDOLA, plantillaDeGondolaPorId } from '../etiquetas/plantillasDeGondola'
import { pluralizar } from '../formato/pluralizar'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import {
  agregarPagina,
  alternarArticulo,
  candidatosDeBusqueda,
  candidatosDeOfertas,
  esSeleccionable,
  filasParaImprimir,
  paginaCompleta,
  quitarPagina,
  SELECCION_VACIA,
  type Candidato,
  type Seleccion,
} from './etiquetas-gondola/candidatos'

const DEMORA_DE_BUSQUEDA_MS = 300

type Grilla = { modo: 'servidor' | 'local'; candidatos: Candidato[]; total: number; truncado: boolean }
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

  const [busqueda, setBusqueda] = useState('')
  const [busquedaAplicada, setBusquedaAplicada] = useState('')
  const [pagina, setPagina] = useState(1)
  const [tamanio, setTamanio] = useState(25)

  const [grilla, setGrilla] = useState<Grilla | null>(null)
  const [cargandoGrilla, setCargandoGrilla] = useState(false)
  const [errorGrilla, setErrorGrilla] = useState('')

  const [seleccion, setSeleccion] = useState<Seleccion>(SELECCION_VACIA)
  const [avisoDeTope, setAvisoDeTope] = useState(false)

  const [vista, setVista] = useState<Vista | null>(null)
  const [cargandoVista, setCargandoVista] = useState(false)
  const [errorVista, setErrorVista] = useState('')

  // Cada lectura captura su generación; la respuesta de una generación vieja se descarta. Cualquier cambio de
  // lista, modo, búsqueda o página arranca una generación nueva.
  const generacionDeGrilla = useRef(0)
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

  useEffect(() => {
    const temporizador = setTimeout(() => {
      setBusquedaAplicada(busqueda)
      setPagina(1)
    }, DEMORA_DE_BUSQUEDA_MS)
    return () => clearTimeout(temporizador)
  }, [busqueda])

  // En modo Ofertas el servidor devuelve hasta 200 filas de una vez y la página se corta acá: cambiar de página o de tamaño no relee.
  const paginaDeServidor = conOfertas ? 0 : pagina
  const tamanioDeServidor = conOfertas ? 0 : tamanio

  useEffect(() => {
    if (idPuntoVenta === null || idLista === '') return
    const token = ++generacionDeGrilla.current
    setCargandoGrilla(true)
    setErrorGrilla('')

    const cargar = async () => {
      try {
        if (conOfertas) {
          const datos = await clienteDeEtiquetas.datosPorFiltro(idPuntoVenta, idLista, {
            busqueda: busquedaAplicada.trim() || null,
            soloConOfertaVigente: true,
          })
          if (token !== generacionDeGrilla.current) return
          const candidatos = candidatosDeOfertas(datos)
          setGrilla({ modo: 'local', candidatos, total: candidatos.length, truncado: datos.truncado })
        } else {
          const resultado = await clienteDeEtiquetas.buscarArticulos(busquedaAplicada, paginaDeServidor, tamanioDeServidor)
          if (token !== generacionDeGrilla.current) return
          const ids = resultado.items.map((articulo) => articulo.id)
          const datos = ids.length > 0 ? await clienteDeEtiquetas.datosDeArticulos(idPuntoVenta, idLista, ids) : null
          if (token !== generacionDeGrilla.current) return
          setGrilla({ modo: 'servidor', candidatos: candidatosDeBusqueda(resultado.items, datos), total: resultado.total, truncado: false })
        }
      } catch (e) {
        if (token !== generacionDeGrilla.current) return
        setGrilla(null)
        setErrorGrilla(mensajeDeError(e, 'No se pudieron cargar los artículos.'))
      } finally {
        if (token === generacionDeGrilla.current) setCargandoGrilla(false)
      }
    }
    void cargar()
  }, [idPuntoVenta, idLista, conOfertas, busquedaAplicada, paginaDeServidor, tamanioDeServidor])

  const ids = useMemo(() => [...seleccion.keys()], [seleccion])
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

  const filasDeLaPagina =
    grilla === null ? [] : grilla.modo === 'local' ? grilla.candidatos.slice((pagina - 1) * tamanio, pagina * tamanio) : grilla.candidatos

  const alElegirLista = (valor: string) => {
    setIdLista(Number(valor))
    setPagina(1)
  }

  const alAlternarOfertas = (activas: boolean) => {
    setConOfertas(activas)
    setPagina(1)
  }

  const alAlternarArticulo = (candidato: Candidato) => {
    const resultado = alternarArticulo(seleccion, candidato, MAXIMO_DE_ARTICULOS_POR_HOJA)
    setSeleccion(resultado.seleccion)
    setAvisoDeTope(resultado.excedido)
  }

  const alAlternarPagina = () => {
    if (paginaCompleta(seleccion, filasDeLaPagina)) {
      setSeleccion(quitarPagina(seleccion, filasDeLaPagina))
      setAvisoDeTope(false)
      return
    }
    const resultado = agregarPagina(seleccion, filasDeLaPagina, MAXIMO_DE_ARTICULOS_POR_HOJA)
    setSeleccion(resultado.seleccion)
    setAvisoDeTope(resultado.excedido)
  }

  const limpiarSeleccion = () => {
    setSeleccion(SELECCION_VACIA)
    setAvisoDeTope(false)
  }

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
            <select id="eg-lista" className="form-select" value={idLista} disabled={listas === null || listas.length === 0} onChange={(e) => alElegirLista(e.target.value)}>
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
              <optgroup label="Claras">
                {PLANTILLAS_DE_GONDOLA.filter((p) => p.tono === 'claro').map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.nombre}
                  </option>
                ))}
              </optgroup>
              <optgroup label="Oscuras">
                {PLANTILLAS_DE_GONDOLA.filter((p) => p.tono === 'oscuro').map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.nombre}
                  </option>
                ))}
              </optgroup>
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
              <input id="eg-ofertas" type="checkbox" className="form-check-input" checked={conOfertas} onChange={(e) => alAlternarOfertas(e.target.checked)} />
              <label htmlFor="eg-ofertas" className="form-check-label">Ofertas</label>
            </div>
          </div>
          <div className="col-12 col-md-2 text-md-end">
            <button type="button" className="btn btn-primary" disabled={!puedeImprimir} onClick={() => window.print()}>
              Imprimir
            </button>
          </div>
        </div>

        <div className="d-flex flex-wrap align-items-center gap-3 mb-2">
          <div className="flex-grow-1" style={{ maxWidth: '28rem' }}>
            <label htmlFor="eg-busqueda" className="visually-hidden">Buscar artículo</label>
            <input
              id="eg-busqueda"
              type="search"
              className="form-control"
              placeholder="Buscar por código o nombre"
              value={busqueda}
              onChange={(e) => setBusqueda(e.target.value)}
            />
          </div>
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
        {errorGrilla && <div className="alert alert-danger py-2">{errorGrilla}</div>}
        {grilla?.truncado && (
          <div className="alert alert-info py-2">
            Hay más de {MAXIMO_DE_ARTICULOS_POR_HOJA} artículos con oferta: se muestran los primeros. Afiná la búsqueda para ver el resto.
          </div>
        )}

        <div className="table-responsive">
          <table className="table table-sm align-middle">
            <thead>
              <tr>
                <th scope="col" style={{ width: '2.5rem' }}>
                  <input
                    type="checkbox"
                    className="form-check-input"
                    aria-label="Seleccionar la página"
                    checked={paginaCompleta(seleccion, filasDeLaPagina)}
                    disabled={cargandoGrilla || !filasDeLaPagina.some(esSeleccionable)}
                    onChange={alAlternarPagina}
                  />
                </th>
                <th scope="col">Código</th>
                <th scope="col">Nombre</th>
                <th scope="col" className="text-end">Precio</th>
              </tr>
            </thead>
            <tbody>
              {filasDeLaPagina.map((candidato) => (
                <tr key={candidato.idArticulo}>
                  <td>
                    <input
                      type="checkbox"
                      className="form-check-input"
                      aria-label={`Seleccionar ${candidato.nombre}`}
                      checked={seleccion.has(candidato.idArticulo)}
                      disabled={!esSeleccionable(candidato) && !seleccion.has(candidato.idArticulo)}
                      onChange={() => alAlternarArticulo(candidato)}
                    />
                  </td>
                  <td>{candidato.codigoInterno}</td>
                  <td>
                    {candidato.nombre}
                    {candidato.conOferta && <span className="badge text-bg-warning ms-2">Oferta</span>}
                  </td>
                  <td className="text-end">
                    {candidato.precio === null ? <span className="text-muted">{candidato.motivoDeExclusion}</span> : formatearPrecioDeGondola(candidato.precio)}
                  </td>
                </tr>
              ))}
              {grilla !== null && filasDeLaPagina.length === 0 && !cargandoGrilla && (
                <tr>
                  <td colSpan={4} className="text-center text-muted">
                    {conOfertas ? 'No hay artículos con oferta vigente para esta búsqueda.' : 'No hay artículos para esta búsqueda.'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        {grilla !== null && (
          <PaginadorDeServidor
            pagina={pagina}
            tamanio={tamanio}
            total={grilla.total}
            sustantivo="artículo"
            etiquetaDeTamanio="Artículos por página"
            inerte={cargandoGrilla}
            onTamanio={(valor) => {
              setTamanio(valor)
              setPagina(1)
            }}
            onPagina={(delta) => setPagina((actual) => Math.max(1, actual + delta))}
          />
        )}

        <TituloDeSeccion nivel={3} className="mt-4">Vista previa</TituloDeSeccion>
        {errorVista && <div className="alert alert-danger py-2">{errorVista}</div>}
        {cargandoVista && <p className="text-muted">Preparando las etiquetas…</p>}
        {paraImprimir !== null && paraImprimir.omitidos > 0 && (
          <div className="alert alert-info py-2">
            {paraImprimir.omitidos === 1 ? '1 artículo elegido no se imprime' : `${paraImprimir.omitidos} artículos elegidos no se imprimen`}
            {conOfertas ? ': sin oferta vigente o sin precio en esta lista.' : ': sin precio en esta lista.'}
          </div>
        )}
        {seleccion.size === 0 && <p className="text-muted">Elegí al menos un artículo para ver las hojas.</p>}
      </div>

      {paraImprimir !== null && paraImprimir.filas.length > 0 && (
        <HojasDeGondola filas={paraImprimir.filas} porHoja={porHoja} plantilla={plantilla} conOfertas={conOfertas} />
      )}
    </div>
  )
}

export function EtiquetasGondola() {
  const { puntoVenta } = usePuntoVenta()
  return <PantallaEtiquetasGondola key={puntoVenta?.id ?? 'sin-pv'} />
}
