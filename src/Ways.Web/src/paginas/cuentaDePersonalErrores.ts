import { ErrorApi } from '../api/cliente'
import { CODIGO_RESULTADO_INCIERTO } from '../api/bajas'
import type { CampoDeCuenta, ErrorDeCuenta } from './cuentaDePersonalFormularios'

export type { CampoDeCuenta, ErrorDeCuenta }

/** Cuál de las tres escrituras falló: un 403 y un 404 significan cosas distintas en cada una. */
export type Escritura = 'horas' | 'ajuste' | 'pago'

const COPIA_GENERICA = 'No se pudo registrar.'
const COPIA_INCIERTA = 'No se pudo confirmar el resultado: revisá el estado de cuenta antes de reintentar.'

/** Se elige por `codigo`, nunca por `mensaje`. Es un `Map` porque el código viene del servidor: sobre un
 * objeto, `'constructor'` resolvería contra el prototipo. Los códigos son los de `ReglaDeMovimientoDePersonal`,
 * `ReglaDePagoDePersonal`, `ServicioDePagoDePersonal` y los de gastos y turnos que el pago reutiliza. */
const ERROR_POR_CODIGO: ReadonlyMap<string, ErrorDeCuenta> = new Map<string, ErrorDeCuenta>([
  ['fecha_requerida', { campo: 'fecha', mensaje: 'Ingresá la fecha.' }],
  ['horas_invalidas', { campo: 'horas', mensaje: 'Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.' }],
  [
    'valor_hora_invalido',
    { campo: 'valorHora', mensaje: 'El valor hora debe estar entre 0 y 999.999.999.999,99, con hasta dos decimales.' },
  ],
  ['importe_fuera_de_rango', { campo: 'general', mensaje: 'El importe supera el máximo admitido.' }],
  ['detalle_muy_largo', { campo: 'detalle', mensaje: 'El detalle no puede superar los 500 caracteres.' }],
  ['ajuste_importe_invalido', { campo: 'importe', mensaje: 'El importe del ajuste no puede ser cero y admite hasta dos decimales.' }],
  [
    'ajuste_detalle_requerido',
    { campo: 'detalle', mensaje: 'El detalle es obligatorio y tiene que tener al menos 5 caracteres.' },
  ],
  ['pago_personal_origen_invalido', { campo: 'origen', mensaje: 'El origen de los fondos elegido no existe.' }],
  [
    'pago_personal_vacio',
    { campo: 'general', mensaje: 'Cargá un descuento de cuenta corriente o al menos una línea de dinero.' },
  ],
  [
    'pago_personal_descuento_invalido',
    { campo: 'descuento', mensaje: 'El descuento debe ser mayor que 0, con hasta dos decimales.' },
  ],
  ['pago_personal_demasiadas_lineas', { campo: 'lineas', mensaje: 'Un pago admite hasta 20 líneas de dinero.' }],
  [
    'pago_personal_importe_invalido',
    { campo: 'lineas', mensaje: 'El importe de cada línea debe ser mayor que 0, con hasta dos decimales.' },
  ],
  ['pago_personal_referencia_muy_larga', { campo: 'lineas', mensaje: 'La referencia no puede superar los 100 caracteres.' }],
  ['pago_personal_origen_requerido', { campo: 'origen', mensaje: 'Elegí de dónde sale el dinero.' }],
  ['pago_personal_origen_sin_lineas', { campo: 'origen', mensaje: 'Sin líneas de dinero no hay origen de fondos que elegir.' }],
  ['pago_personal_empresa_requerida', { campo: 'empresa', mensaje: 'Elegí la empresa que paga.' }],
  [
    'pago_personal_empresa_no_aplica',
    { campo: 'empresa', mensaje: 'La empresa solo se elige cuando el dinero sale de la tesorería.' },
  ],
  [
    'pago_personal_punto_venta_requerido',
    {
      campo: 'general',
      mensaje: 'Elegí un punto de venta desde el menú: el cajón del turno y el descuento de cuenta corriente lo necesitan.',
    },
  ],
  [
    'turno_no_abierto',
    {
      campo: 'origen',
      mensaje: 'No hay un turno abierto en el punto de venta de la sesión. Abrí el turno desde Caja y volvé a intentar.',
    },
  ],
  [
    'personal_sin_cliente_vinculado',
    {
      campo: 'descuento',
      mensaje: 'El empleado no tiene un cliente vinculado vigente para descontar de su cuenta corriente.',
    },
  ],
  [
    'descuento_supera_la_deuda',
    { campo: 'descuento', mensaje: 'El descuento supera la deuda actual del cliente vinculado. Actualizá la pantalla.' },
  ],
  ['gasto_fecha_futura', { campo: 'fecha', mensaje: 'La fecha del pago no puede ser futura.' }],
  [
    'gasto_medio_pago_cuenta_corriente_invalido',
    { campo: 'lineas', mensaje: 'La cuenta corriente no se puede usar como medio de pago de una línea de dinero.' },
  ],
  [
    'punto_venta_no_pertenece_a_la_empresa',
    { campo: 'empresa', mensaje: 'El punto de venta de la sesión no pertenece a la empresa elegida.' },
  ],
])

/** El rechazo de una escritura de la cuenta, ubicado en el campo que corresponde. */
export function errorDeEscritura(e: unknown, escritura: Escritura): ErrorDeCuenta {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: COPIA_INCIERTA }

  const propio = ERROR_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 403) {
    return escritura === 'pago'
      ? { campo: 'origen', mensaje: 'Pagar desde la tesorería requiere ser administrador.' }
      : { campo: 'general', mensaje: 'No tenés permiso para registrar esto.' }
  }

  if (e.estado === 404) {
    return {
      campo: 'general',
      mensaje:
        escritura === 'pago'
          ? 'No se encontró el empleado, el medio de pago, la empresa o el punto de venta elegido. Actualizá la pantalla e intentá de nuevo.'
          : 'El empleado ya no existe. Volvé al personal y actualizá el listado.',
    }
  }

  const delServidor = e.message.trim()

  if (e.estado >= 500) {
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return { campo: 'general', mensaje: esCommitAmbiguo ? delServidor : COPIA_INCIERTA }
  }

  return { campo: 'general', mensaje: delServidor || COPIA_GENERICA }
}
