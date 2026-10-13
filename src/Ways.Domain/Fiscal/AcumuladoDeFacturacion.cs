namespace Ways.Domain.Fiscal;

/// <summary>
/// Saldo a facturar de un medio de pago en un mes, por empresa: clave <c>(id_empresa, id_medio_pago,
/// periodo)</c>. El cupo se cumple dentro de cada mes calendario y el saldo arranca en cero el día 1, así
/// que cada mes es una fila distinta.
///
/// PK-only, sin auditoría ni baja lógica, mismo criterio que <see cref="NumeracionFiscal"/>: no hereda de
/// <see cref="Common.EntidadTenant"/>, así que necesita el filtro de tenant escrito a mano
/// (<c>WaysDbContext.AplicarFiltroDeTenantEnAcumuladoDeFacturacion</c>). Su único escritor legítimo es un
/// <c>INSERT … ON CONFLICT DO UPDATE</c> crudo que suma el aporte de forma atómica; <c>SaveChanges</c> lo
/// rechaza.
///
/// <see cref="SaldoAFacturar"/> es <c>numeric(18,6)</c>: un aporte es importe (2 decimales) por porcentaje
/// (2 decimales) dividido por 100, o sea hasta 6 decimales exactos, y la suma de aportes no necesita más.
/// </summary>
public class AcumuladoDeFacturacion
{
    public int IdEmpresa { get; set; }

    public int IdMedioPago { get; set; }

    /// <summary>Primer día del mes (CHECK de día 1).</summary>
    public DateOnly Periodo { get; set; }

    public int IdTenant { get; set; }

    /// <summary>Lo vendido por el medio que todavía no se facturó. Puede ser negativo: lo facturado por
    /// arrastre de otro medio se debita completo (ver <see cref="PoliticaDeFacturacionPorMedio"/>).</summary>
    public decimal SaldoAFacturar { get; set; }

    public decimal TotalVendido { get; set; }

    public decimal TotalFacturado { get; set; }
}
