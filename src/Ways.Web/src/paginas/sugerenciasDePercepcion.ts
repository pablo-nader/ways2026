import type { EncabezadoDeCompraFormulario, PercepcionFormulario, TotalesDeCompra } from '../api/compras'
import { discriminaIvaEfectivo } from '../api/compras'
import type {
  EmpresaListado,
  ProveedorListado,
  PuntoVentaListado,
  TipoComprobanteListado,
  TipoDePercepcion,
} from '../api/tipos'

/** Datos de referencia que el editor ya cargó: lo que hace falta para pre-cargar percepciones sin
 * volver a pedir nada al servidor. `empresas` puede venir vacío (un rol sin acceso a empresas no
 * recibe sus alícuotas): en ese caso simplemente no hay sugerencia y la percepción se carga a mano. */
export type ReferenciaDePercepciones = {
  proveedores: ProveedorListado[]
  tipos: TipoComprobanteListado[]
  puntosVenta: PuntoVentaListado[]
  empresas: EmpresaListado[]
}

function redondear(valor: number, decimales: number): number {
  const factor = 10 ** decimales
  return Math.round((valor + Number.EPSILON) * factor) / factor
}

/** `round(base × alícuota / 100, 2)`: el importe que se propone, nunca el que se envía si el
 * operador lo corrigió con el de la factura. */
export function importeDePercepcion(baseImponible: number, alicuota: number): number {
  return redondear((baseImponible * alicuota) / 100, 2)
}

/** Base imponible que se sugiere: el neto gravado (suma del neto de las alícuotas mayores a 0%).
 * Un comprobante que no discrimina IVA no tiene neto, así que la base es lo que se compró
 * (subtotal menos descuento). Se calcula sobre los totales SIN percepciones. */
export function baseDePercepcion(totales: TotalesDeCompra, discriminaIva: boolean): number {
  if (!discriminaIva) return redondear(totales.subtotal - totales.descuentoTotal, 2)
  return redondear(
    totales.alicuotas.filter((a) => a.porcentaje > 0).reduce((acumulado, a) => acumulado + a.neto, 0),
    2,
  )
}

function alicuotaDeLaEmpresa(empresa: EmpresaListado | null, tipo: TipoDePercepcion): number | null {
  if (empresa === null) return null
  return tipo === 'iibb' ? empresa.alicuotaPercepcionIibb : empresa.alicuotaPercepcionIva
}

function proveedorPercibe(proveedor: ProveedorListado | null, tipo: TipoDePercepcion): boolean {
  if (proveedor === null) return false
  return tipo === 'iibb' ? proveedor.percibeIibb : proveedor.percibeIva
}

/** Percepciones que corresponde pre-cargar: el proveedor percibe ese impuesto, la empresa del punto
 * de venta de la compra tiene la alícuota, el tipo registra libro IVA y, para IVA, el comprobante
 * discrimina IVA. Vienen en modo automático: base e importe se derivan del neto en cada render. */
export function percepcionesSugeridas(
  encabezado: EncabezadoDeCompraFormulario,
  referencia: ReferenciaDePercepciones,
): PercepcionFormulario[] {
  const tipo = referencia.tipos.find((t) => t.id === encabezado.idTipoComprobante) ?? null
  if (tipo === null || !tipo.registraLibroIva) return []

  const proveedor = referencia.proveedores.find((p) => p.id === encabezado.idProveedor) ?? null
  const puntoVenta = referencia.puntosVenta.find((pv) => pv.id === encabezado.idPuntoVenta) ?? null
  const empresa = referencia.empresas.find((e) => e.id === puntoVenta?.idEmpresa) ?? null
  const discrimina = discriminaIvaEfectivo(tipo, encabezado.discriminaIva)

  const sugeridas: PercepcionFormulario[] = []
  for (const impuesto of ['iibb', 'iva'] as const) {
    const alicuota = alicuotaDeLaEmpresa(empresa, impuesto)
    if (alicuota === null || !proveedorPercibe(proveedor, impuesto)) continue
    if (impuesto === 'iva' && !discrimina) continue
    sugeridas.push({ tipo: impuesto, baseImponible: null, alicuota, importe: null, automatica: true })
  }

  return sugeridas
}

/** Combina lo que ya hay con lo sugerido: las filas que el operador tocó o agregó se respetan; las
 * automáticas que dejaron de corresponder (otro proveedor, otro tipo) se retiran y las que siguen
 * correspondiendo toman la alícuota vigente; lo sugerido que falta se agrega salvo que el operador
 * ya lo haya quitado. */
export function fusionarSugerencias(
  actuales: PercepcionFormulario[],
  sugeridas: PercepcionFormulario[],
  descartadas: TipoDePercepcion[],
): PercepcionFormulario[] {
  const vigentes = actuales
    .filter((p) => !p.automatica || sugeridas.some((s) => s.tipo === p.tipo))
    .map((p) => {
      const sugerida = sugeridas.find((s) => s.tipo === p.tipo)
      return p.automatica && sugerida ? { ...p, alicuota: sugerida.alicuota } : p
    })
  const nuevas = sugeridas.filter((s) => !vigentes.some((p) => p.tipo === s.tipo) && !descartadas.includes(s.tipo))
  return [...vigentes, ...nuevas]
}

/** Aplica las sugerencias al encabezado: se llama al cambiar proveedor, tipo, punto de venta o
 * "discrimina IVA", que son los datos de los que dependen. */
export function conSugerenciasDePercepcion(
  encabezado: EncabezadoDeCompraFormulario,
  referencia: ReferenciaDePercepciones,
): EncabezadoDeCompraFormulario {
  return {
    ...encabezado,
    percepciones: fusionarSugerencias(
      encabezado.percepciones,
      percepcionesSugeridas(encabezado, referencia),
      encabezado.percepcionesDescartadas,
    ),
  }
}

/** Elegir proveedor pre-carga su modo de precios y reinicia las percepciones que el operador había
 * quitado (son decisiones sobre el proveedor anterior), y recién entonces sugiere las del nuevo. */
export function alElegirProveedor(
  encabezado: EncabezadoDeCompraFormulario,
  idProveedor: number | '',
  referencia: ReferenciaDePercepciones,
): EncabezadoDeCompraFormulario {
  const proveedor = referencia.proveedores.find((p) => p.id === idProveedor) ?? null
  return conSugerenciasDePercepcion(
    {
      ...encabezado,
      idProveedor,
      preciosIncluyenIva: proveedor?.preciosIncluyenIva ?? false,
      percepcionesDescartadas: [],
    },
    referencia,
  )
}

/** Llena base e importe de las filas automáticas a partir de la base vigente; las demás quedan
 * como están. Es una función de los datos del render: no se guarda en el estado. */
export function resolverPercepciones(percepciones: PercepcionFormulario[], base: number): PercepcionFormulario[] {
  return percepciones.map((p) => {
    if (!p.automatica) return p
    return {
      ...p,
      baseImponible: base,
      importe: p.alicuota === null ? null : importeDePercepcion(base, p.alicuota),
    }
  })
}

/** Cambio manual de una fila. Parte de la fila ya resuelta (lo que el operador ve) y la congela:
 * desde acá no se vuelve a derivar del neto. Tocar base o alícuota re-propone el importe a partir
 * de ellas; tocar el importe lo deja tal cual, porque manda la factura. */
export function editarPercepcion(
  resuelta: PercepcionFormulario,
  cambios: Partial<Pick<PercepcionFormulario, 'baseImponible' | 'alicuota' | 'importe'>>,
): PercepcionFormulario {
  const siguiente = { ...resuelta, ...cambios, automatica: false }
  if ('importe' in cambios) return siguiente

  const { baseImponible, alicuota } = siguiente
  return {
    ...siguiente,
    importe: baseImponible === null || alicuota === null ? siguiente.importe : importeDePercepcion(baseImponible, alicuota),
  }
}

/** Percepción agregada a mano: arranca congelada con la base vigente y la alícuota de la empresa
 * (o vacía si no se conoce); el importe se propone solo cuando hay alícuota. */
export function percepcionManual(
  tipo: TipoDePercepcion,
  base: number,
  alicuotaDeLaEmpresa: number | null,
): PercepcionFormulario {
  return {
    tipo,
    baseImponible: base,
    alicuota: alicuotaDeLaEmpresa,
    importe: alicuotaDeLaEmpresa === null ? null : importeDePercepcion(base, alicuotaDeLaEmpresa),
    automatica: false,
  }
}

/** Alícuota de la empresa del punto de venta elegido, para una percepción agregada a mano. */
export function alicuotaDeEmpresaParaCompra(
  encabezado: EncabezadoDeCompraFormulario,
  referencia: ReferenciaDePercepciones,
  tipo: TipoDePercepcion,
): number | null {
  const puntoVenta = referencia.puntosVenta.find((pv) => pv.id === encabezado.idPuntoVenta) ?? null
  const empresa = referencia.empresas.find((e) => e.id === puntoVenta?.idEmpresa) ?? null
  return alicuotaDeLaEmpresa(empresa, tipo)
}

/** Un comprobante persistido → filas del formulario (siempre manuales: son lo que se guardó). */
export function percepcionesDesdeDetalle(
  percepciones: { tipo: TipoDePercepcion; alicuota: number; baseImponible: number | null; importe: number | null }[],
): PercepcionFormulario[] {
  return percepciones.map((p) => ({
    tipo: p.tipo,
    baseImponible: p.baseImponible,
    alicuota: p.alicuota,
    importe: p.importe,
    automatica: false,
  }))
}
