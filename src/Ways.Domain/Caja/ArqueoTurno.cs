namespace Ways.Domain.Caja;

/// <summary>
/// Una fila por medio de pago con actividad en el turno, escrita una sola vez al cierre (doc 10
/// §7, design: Table Shapes — write path A; The Cierre Transaction). Append-only e irreversible
/// por diseño — ningún endpoint edita ni elimina una fila; no tiene columna de fecha propia
/// porque su momento ES <see cref="TurnoCaja.FechaCierre"/>.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo criterio que <see cref="Ways.Domain.Stock.MovimientoStock"/>, con filtro de tenant
/// escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnArqueoTurno</c>.
/// </summary>
public class ArqueoTurno
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdTurnoCaja { get; set; }
    public int IdMedioPago { get; set; }

    /// <summary>Derivado server-side por <c>CalculadorDeArqueo</c> (Slice 4) — nunca input de
    /// cliente (spec: Cierre Payload Carries Only Declared Counts).</summary>
    public decimal ImporteEsperado { get; set; }

    /// <summary>Lo que el cajero contó — el único dato que el cliente envía.</summary>
    public decimal ImporteDeclarado { get; set; }

    /// <summary><c>importe_esperado − importe_declarado</c> (positivo = faltante). Columna
    /// <c>GENERATED ALWAYS ... STORED</c> (design decisión 6): no puede desviarse de sus
    /// operandos ni por una escritura fuera de banda — ver <c>ArqueoTurnoConfiguration</c>, EF
    /// nunca la incluye en el INSERT.</summary>
    public decimal Diferencia { get; set; }
}
