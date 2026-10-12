import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  COPIA_SALDO_CON_MOVIMIENTOS,
  LARGO_MAXIMO_DE_NOMBRE,
  aAlta,
  aEdicion,
  aFormulario,
  clienteDeCuentasTesoreria,
  conSaldoInicialBloqueado,
  copiaDeBajaDeCuenta,
  errorAlGuardar,
  formularioVacio,
  nombreDeEmpresa,
  validarFormulario,
  type CampoConError,
  type CuentaTesoreriaListado,
  type ErrorDeCuenta,
  type Formulario,
} from '../api/cuentasTesoreria'
import type { EmpresaListado } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import { Modal } from '../componentes/Modal'
import { formatearImporte } from '../formato/importes'

const ID_FORMULARIO = 'formulario-cuenta-tesoreria'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se dio de baja, pero no se pudo actualizar la vista. Recargá la pantalla.'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/**
 * ABM de cuentas de tesorería (solo Admin, como el endpoint). Mismo esqueleto que `Personal.tsx`: token de
 * generación para las lecturas, `ocupadoRef` de re-entrancia compartido entre guardado y baja, y `bloqueado`
 * deja inertes todos los controles mientras una escritura está en vuelo o la confirmación de baja está abierta.
 * El saldo inicial queda de solo lectura cuando el servidor informa `saldoInicialEditable = false` o rechaza
 * el cambio con `saldo_inicial_con_movimientos`; la pantalla no lo deduce.
 */
export function CuentasTesoreria() {
  const [cuentas, setCuentas] = useState<CuentaTesoreriaListado[] | null>(null)
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [errorFormulario, setErrorFormulario] = useState<ErrorDeCuenta | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [baja, setBaja] = useState<CuentaTesoreriaListado | null>(null)
  const [ocupadoBaja, setOcupadoBaja] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  const contenedorRef = useRef<HTMLDivElement>(null)

  /** Toda acción que reemplaza lo que hay en pantalla (recargar, guardar, dar de baja) acuña una generación nueva
   * ANTES de cambiar el estado, y toda respuesta tardía de una generación anterior se descarta. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": la ÚNICA guarda de re-entrancia válida. */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const resultado = await clienteDeCuentasTesoreria.listar()
      if (generacion.current !== token) return
      setCuentas(resultado)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las cuentas de tesorería.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  // Las empresas solo alimentan el selector del alta. La corrida del efecto es su propia generación: el
  // desmontaje la invalida y una respuesta tardía se descarta.
  useEffect(() => {
    let vigente = true

    clienteDeOrganizacion
      .listarEmpresas()
      .then((lista) => {
        if (vigente) setEmpresas(lista)
      })
      .catch(() => {
        if (!vigente) return
        setEmpresas([])
        setErrorEmpresas('No se pudieron cargar las empresas: no se pueden crear cuentas hasta recargar la pantalla.')
      })

    return () => {
      vigente = false
    }
  }, [])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const rechazoLocal = validarFormulario(datos)
    if (rechazoLocal) {
      setErrorFormulario(rechazoLocal)
      return
    }

    // Una lectura en vuelo queda invalidada por el token de esta escritura: no va a dejar datos ni a bajar
    // `cargando`. Si la escritura falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario(null)
    try {
      try {
        if (datos.id === null) {
          await clienteDeCuentasTesoreria.crear(aAlta(datos))
        } else {
          await clienteDeCuentasTesoreria.actualizar(datos.id, aEdicion(datos))
        }
      } catch (e) {
        if (generacion.current === token) {
          setErrorFormulario(errorAlGuardar(e))
          const rechazoDeSaldo = e instanceof ErrorApi && e.codigo === 'saldo_inicial_con_movimientos'
          if (rechazoDeSaldo) setFormulario((prev) => prev && conSaldoInicialBloqueado(prev))
          if (habiaLecturaEnVuelo || rechazoDeSaldo) void cargar(++generacion.current)
        }

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      const nombre = datos.nombre.trim()
      await refrescarTrasEscribir(
        token,
        datos.id === null ? `Cuenta "${nombre}" creada.` : `Cuenta "${nombre}" actualizada.`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  function abrirFormulario(valor: Formulario) {
    setFormulario(valor)
    setAviso('')
    setError('')
    setErrorFormulario(null)
  }

  /** La modal no ofrece cancelar con una escritura en vuelo, así que cerrar no supersede ninguna escritura y no
   * acuña generación. */
  function cancelarFormulario() {
    setFormulario(null)
    setErrorFormulario(null)
  }

  function cambiarFormulario(valor: Formulario) {
    setFormulario(valor)
    setErrorFormulario(null)
  }

  function pedirBaja(cuenta: CuentaTesoreriaListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(cuenta)
    setError('')
    setAviso('')
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    const cuenta = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupadoBaja(cuenta.id)
    setError('')
    try {
      try {
        await clienteDeCuentasTesoreria.eliminar(cuenta.id)
      } catch (e) {
        setError(copiaDeBajaDeCuenta(e, cuenta.nombre))

        return
      }

      setBaja(null)
      setFormulario((prev) => (prev?.id === cuenta.id ? null : prev))
      await refrescarTrasEscribir(token, `Cuenta "${cuenta.nombre}" dada de baja.`, AVISO_REFRESCO_FALLIDO_BAJA)
    } finally {
      ocupadoRef.current = false
      setOcupadoBaja(null)
    }
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera. */
  const bloqueado = guardando || ocupadoBaja !== null || baja !== null

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <BotonIcono
        icono="agregar"
        className="text-nowrap"
        onClick={() => abrirFormulario(formularioVacio(empresas ?? []))}
        disabled={bloqueado || empresas === null || errorEmpresas !== ''}
      />
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Cuentas de tesorería" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {errorEmpresas && <div className="alert alert-warning">{errorEmpresas}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la cuenta "${baja.nombre}"`}
            ocupado={ocupadoBaja !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <ModalDeCuenta
            valor={formulario}
            empresas={empresas ?? []}
            guardando={guardando}
            bloqueado={bloqueado}
            error={errorFormulario}
            focoDeReserva={contenedorRef}
            onCambio={cambiarFormulario}
            onGuardar={guardar}
            onCancelar={cancelarFormulario}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <p className="small text-muted mb-2">
              El saldo actual es el de la última operación de la cuenta; sin operaciones, es el saldo inicial.
            </p>
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Empresa</th>
                  <th className="text-end">Saldo actual</th>
                  <th className="text-end">Saldo inicial</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {cuentas?.map((cuenta) => (
                  <tr key={cuenta.id}>
                    <td>{cuenta.nombre}</td>
                    <td>{cuenta.nombreEmpresa ?? <span className="text-muted fst-italic">sin empresa</span>}</td>
                    <td className="text-end">{formatearMoneda(cuenta.saldoActual)}</td>
                    <td className="text-end">{formatearMoneda(cuenta.saldoInicial)}</td>
                    <td>
                      {cuenta.activo ? (
                        <span className="badge text-bg-success">Activa</span>
                      ) : (
                        <span className="badge text-bg-secondary">Inactiva</span>
                      )}
                    </td>
                    <td className="text-end text-nowrap">
                      <BotonIcono
                        icono="editar"
                        etiqueta={`Editar ${cuenta.nombre}`}
                        className="me-1"
                        onClick={() => abrirFormulario(aFormulario(cuenta))}
                        disabled={bloqueado}
                      />
                      <BotonIcono
                        icono="eliminar"
                        etiqueta={`Eliminar ${cuenta.nombre}`}
                        onClick={(evento) => pedirBaja(cuenta, evento.currentTarget)}
                        disabled={bloqueado}
                      />
                    </td>
                  </tr>
                ))}
                {cuentas?.length === 0 && (
                  <tr>
                    <td colSpan={6} className="text-center text-muted py-4">
                      Todavía no hay cuentas de tesorería.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

function ErrorDeCampo({ id, error, campo }: { id: string; error: ErrorDeCuenta | null; campo: CampoConError }) {
  if (error?.campo !== campo) return null

  return (
    <div id={id} role="alert" className="invalid-feedback d-block">
      {error.mensaje}
    </div>
  )
}

function ModalDeCuenta({
  valor,
  empresas,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  empresas: EmpresaListado[]
  guardando: boolean
  bloqueado: boolean
  error: ErrorDeCuenta | null
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNueva = valor.id === null
  const empresaDeLaCuenta = empresas.find((e) => e.id === valor.idEmpresa)

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNueva ? 'Nueva cuenta de tesorería' : 'Editar cuenta de tesorería'}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error?.campo === 'general' ? error.mensaje : ''} />
      <form
        id={ID_FORMULARIO}
        className="row g-3"
        autoComplete="off"
        noValidate
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <div className="col-md-6">
          <label className="form-label" htmlFor="cta-nombre">
            Nombre
          </label>
          <input
            id="cta-nombre"
            autoFocus
            className={`form-control${error?.campo === 'nombre' ? ' is-invalid' : ''}`}
            maxLength={LARGO_MAXIMO_DE_NOMBRE}
            value={valor.nombre}
            aria-invalid={error?.campo === 'nombre' || undefined}
            aria-describedby={error?.campo === 'nombre' ? 'cta-nombre-error' : undefined}
            onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            disabled={bloqueado}
          />
          <ErrorDeCampo id="cta-nombre-error" error={error} campo="nombre" />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="cta-empresa">
            Empresa
          </label>
          {esNueva ? (
            <select
              id="cta-empresa"
              className={`form-select${error?.campo === 'idEmpresa' ? ' is-invalid' : ''}`}
              value={valor.idEmpresa ?? ''}
              aria-invalid={error?.campo === 'idEmpresa' || undefined}
              aria-describedby={error?.campo === 'idEmpresa' ? 'cta-empresa-error' : undefined}
              onChange={(e) =>
                onCambio({ ...valor, idEmpresa: e.target.value === '' ? null : Number(e.target.value) })
              }
              disabled={bloqueado}
            >
              <option value="">Elegí una empresa</option>
              {empresas.map((empresa) => (
                <option key={empresa.id} value={empresa.id}>
                  {nombreDeEmpresa(empresa)}
                </option>
              ))}
            </select>
          ) : (
            <input
              id="cta-empresa"
              className="form-control"
              readOnly
              value={empresaDeLaCuenta ? nombreDeEmpresa(empresaDeLaCuenta) : `Empresa ${valor.idEmpresa}`}
              aria-describedby="cta-empresa-ayuda"
            />
          )}
          {!esNueva && (
            <div id="cta-empresa-ayuda" className="form-text">
              Una cuenta no cambia de empresa.
            </div>
          )}
          <ErrorDeCampo id="cta-empresa-error" error={error} campo="idEmpresa" />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="cta-saldo-inicial">
            Saldo inicial
          </label>
          <CampoImporte
            id="cta-saldo-inicial"
            className={`form-control${error?.campo === 'saldoInicial' ? ' is-invalid' : ''}`}
            admiteNegativos
            valor={valor.saldoInicial}
            readOnly={!valor.saldoInicialEditable}
            aria-invalid={error?.campo === 'saldoInicial' || undefined}
            aria-describedby={
              error?.campo === 'saldoInicial'
                ? 'cta-saldo-inicial-error'
                : !valor.saldoInicialEditable
                  ? 'cta-saldo-inicial-ayuda'
                  : undefined
            }
            onChange={(saldoInicial) => onCambio({ ...valor, saldoInicial })}
            disabled={bloqueado}
          />
          {!valor.saldoInicialEditable && (
            <div id="cta-saldo-inicial-ayuda" className="form-text">
              {COPIA_SALDO_CON_MOVIMIENTOS}
            </div>
          )}
          <ErrorDeCampo id="cta-saldo-inicial-error" error={error} campo="saldoInicial" />
        </div>

        <div className="col-md-6 d-flex align-items-end">
          <div className="form-check">
            <input
              id="cta-activa"
              type="checkbox"
              className="form-check-input"
              checked={valor.activo}
              onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              disabled={bloqueado}
            />
            <label className="form-check-label" htmlFor="cta-activa">
              Activa
            </label>
          </div>
        </div>
      </form>
    </Modal>
  )
}
