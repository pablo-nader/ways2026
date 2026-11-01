import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { Link, useInRouterContext } from 'react-router'
import type { CuentaTesoreriaListado } from '../../api/cuentasTesoreria'
import {
  AVISO_DE_DESCONEXION,
  clienteDeMercadoPago,
  copiaDeFalloDeMercadoPago,
  esUrlDeAutorizacionNavegable,
  type EstadoDeConexionMercadoPago,
} from '../../api/mercadoPago'
import type { EmpresaListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
import { fechaHoraATexto } from '../../formato/fechas'

const RUTA_DE_CUENTAS_DE_TESORERIA = '/caja/cuentas-tesoreria'

type Props = {
  empresa: EmpresaListado
  /** Cuentas de tesorería ACTIVAS de esta empresa. */
  cuentas: readonly CuentaTesoreriaListado[]
  /** Por qué no se pudieron cargar las cuentas, si no se pudieron. */
  errorDeCuentas: string
  /** Cambia cuando la pantalla completó un canje de esta empresa: dispara una lectura nueva. */
  version: number
  /** Hay una escritura de la pantalla en vuelo (el canje del retorno de Mercado Pago). */
  bloqueado: boolean
  /** Aviso de resultado que la pantalla quiere mostrar en la tarjeta de esta empresa. */
  onAviso: (texto: string) => void
}

function EnlaceACuentas() {
  const enRouter = useInRouterContext()
  if (enRouter) return <Link to={RUTA_DE_CUENTAS_DE_TESORERIA}>cuentas de tesorería</Link>

  return <a href={RUTA_DE_CUENTAS_DE_TESORERIA}>cuentas de tesorería</a>
}

/**
 * Conexión de UNA empresa con Mercado Pago: lee su estado, inicia el OAuth y desconecta. El canje del código no vive acá
 * sino en la sección, porque el retorno trae la empresa en el fragmento y hay que completarlo aunque la tarjeta no esté.
 *
 * `react-async-state`: la lectura lleva generación (la respuesta vieja se descarta); la desconexión deja inerte la tarjeta
 * entera mientras dura y se guarda con un ref síncrono; "Conectar" no suelta `ocupado` si el POST salió bien, porque la
 * página se va a Mercado Pago.
 */
export function ConexionMercadoPago({ empresa, cuentas, errorDeCuentas, version, bloqueado, onAviso }: Props) {
  const idSelector = useId()
  const [estado, setEstado] = useState<EstadoDeConexionMercadoPago | null>(null)
  const [cargando, setCargando] = useState(true)
  const [errorDeLectura, setErrorDeLectura] = useState('')
  const [error, setError] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [elegida, setElegida] = useState('')
  const [confirmando, setConfirmando] = useState(false)
  const [disparador, setDisparador] = useState<HTMLElement | null>(null)

  const generacion = useRef(0)
  const ocupadoRef = useRef(false)

  const cargar = useCallback(
    async (token: number) => {
      setCargando(true)
      try {
        const leido = await clienteDeMercadoPago.obtener(empresa.id)
        if (generacion.current !== token) return
        setEstado(leido)
        setErrorDeLectura('')
      } catch (e) {
        if (generacion.current !== token) return
        setErrorDeLectura(copiaDeFalloDeMercadoPago(e, 'leer'))
      } finally {
        if (generacion.current === token) setCargando(false)
      }
    },
    [empresa.id],
  )

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar, version])

  const cuentaVigente = cuentas.some((c) => String(c.id) === elegida)
    ? elegida
    : cuentas.length === 1
      ? String(cuentas[0].id)
      : ''
  const inerte = bloqueado || ocupado

  async function conectar() {
    if (ocupadoRef.current || !cuentaVigente) return

    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    try {
      const inicio = await clienteDeMercadoPago.iniciar(empresa.id, Number(cuentaVigente))
      if (!esUrlDeAutorizacionNavegable(inicio.urlAutorizacion)) {
        setError(copiaDeFalloDeMercadoPago(null, 'iniciar'))
      } else {
        // La página se va: la tarjeta queda inerte hasta que el navegador navegue.
        window.location.assign(inicio.urlAutorizacion)

        return
      }
    } catch (e) {
      setError(copiaDeFalloDeMercadoPago(e, 'iniciar'))
    }

    ocupadoRef.current = false
    setOcupado(false)
  }

  function pedirDesconexion(boton: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparador(boton)
    setError('')
    setConfirmando(true)
  }

  function cancelarDesconexion() {
    if (ocupadoRef.current) return

    setConfirmando(false)
    setError('')
  }

  async function confirmarDesconexion() {
    if (ocupadoRef.current) return

    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    try {
      try {
        await clienteDeMercadoPago.desconectar(empresa.id)
      } catch (e) {
        setError(copiaDeFalloDeMercadoPago(e, 'desconectar'))

        return
      }

      setConfirmando(false)
      onAviso(AVISO_DE_DESCONEXION)
      await cargar(token)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  const activa = estado?.conectada === true
  const requiereReconexion = !activa && estado?.estado === 'requiere_reconexion'

  return (
    <section className="border rounded p-3 mb-3" aria-label={`Mercado Pago de ${empresa.razonSocial}`}>
      <h6 className="mb-2">{empresa.razonSocial}</h6>

      {confirmando && (
        <ConfirmacionDeBaja
          titulo={`la conexión de "${empresa.razonSocial}" con Mercado Pago`}
          pregunta="desconectar"
          arrastra={[]}
          nota="Se borran los tokens guardados. Los cobros con Mercado Pago dejan de funcionar hasta volver a conectar."
          etiquetaConfirmar="Desconectar"
          etiquetaEnCurso="Desconectando…"
          ocupado={ocupado}
          disparador={disparador}
          error={error}
          onConfirmar={() => void confirmarDesconexion()}
          onCancelar={cancelarDesconexion}
        />
      )}

      {error && !confirmando && <div className="alert alert-danger">{error}</div>}

      {cargando && estado === null ? (
        <Cargando />
      ) : errorDeLectura && estado === null ? (
        <div className="alert alert-danger">{errorDeLectura}</div>
      ) : activa && estado ? (
        <div>
          <p className="mb-1">
            <span className="badge bg-success me-2">Conectada</span>
            Cuenta de Mercado Pago <strong>{estado.idUsuarioMercadoPago}</strong>
            {estado.nombreCuenta && <> · cuenta de tesorería «{estado.nombreCuenta}»</>}
          </p>
          {estado.fechaConexion && <p className="mb-1 text-muted">Conectada el {fechaHoraATexto(estado.fechaConexion)}.</p>}
          {estado.accessTokenExpiraEn && (
            <p className="mb-2 text-muted">El acceso vence el {fechaHoraATexto(estado.accessTokenExpiraEn)} y se renueva solo.</p>
          )}
          <button
            type="button"
            className="btn btn-outline-danger"
            disabled={inerte || confirmando}
            onClick={(evento) => pedirDesconexion(evento.currentTarget)}
          >
            Desconectar
          </button>
        </div>
      ) : (
        <div>
          {requiereReconexion && (
            <div className="alert alert-warning" role="alert">
              Hay que volver a conectar Mercado Pago: Mercado Pago rechazó la renovación del acceso.
            </div>
          )}
          {!requiereReconexion && <p className="text-muted">Mercado Pago no está conectado.</p>}

          {errorDeCuentas ? (
            <div className="alert alert-danger">{errorDeCuentas}</div>
          ) : cuentas.length === 0 ? (
            <p>
              Esta empresa no tiene cuentas de tesorería activas. Para conectar Mercado Pago primero hay que crear una cuenta
              en <EnlaceACuentas />.
            </p>
          ) : (
            <form
              className="row g-2 align-items-end"
              onSubmit={(e) => {
                e.preventDefault()
                void conectar()
              }}
            >
              <div className="col-md-5">
                <label className="form-label" htmlFor={idSelector}>
                  Cuenta de tesorería de Mercado Pago
                </label>
                <select
                  id={idSelector}
                  className="form-select"
                  value={cuentaVigente}
                  onChange={(e) => setElegida(e.target.value)}
                  disabled={inerte}
                  required
                >
                  <option value="">Elegí una cuenta</option>
                  {cuentas.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <button type="submit" className="btn btn-primary" disabled={inerte || !cuentaVigente}>
                  {ocupado ? 'Conectando…' : 'Conectar Mercado Pago'}
                </button>
              </div>
            </form>
          )}
        </div>
      )}
    </section>
  )
}
