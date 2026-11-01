using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>Pedido de vista previa o de reconstrucción. Los tres campos son obligatorios; son nullable para que su ausencia llegue
/// al servicio y se rechace con un código específico. <see cref="Desde"/> y <see cref="Hasta"/> son fechas de calendario inclusivas,
/// en la zona horaria de la empresa.</summary>
public sealed record SolicitudDeReconstruccionDeCuentas(int? IdEmpresa, DateOnly? Desde, DateOnly? Hasta);

/// <summary>Una fila que el cierre habría escrito: el cobro, la comisión o una retención de un medio en un turno.</summary>
public sealed record ComponenteDeReconstruccion(
    TipoMovimientoCuenta Tipo, decimal Ingreso, decimal Egreso, decimal? PorcentajeAplicado);

/// <summary>Lo que se acredita (o se acreditó) por un medio en un turno cerrado, con la fecha de cierre del turno.</summary>
public sealed record AcreditacionDeReconstruccion(
    int IdTurnoCaja, DateTimeOffset FechaCierre, int IdMedioPago, string NombreMedio, int IdCuentaTesoreria,
    string NombreCuenta, decimal BaseBruta, decimal Neto, IReadOnlyList<ComponenteDeReconstruccion> Componentes);

/// <summary>Efecto sobre una cuenta: filas nuevas, su neto y el saldo vigente antes y después.</summary>
public sealed record CuentaDeReconstruccion(
    int IdCuentaTesoreria, string Nombre, int Filas, decimal Neto, decimal SaldoAntes, decimal SaldoDespues);

/// <summary>La vista previa devuelve lo que se escribiría; la aplicación, lo que se escribió. Sin nada pendiente, las listas
/// vienen vacías y <see cref="Filas"/> es cero.</summary>
public sealed record PlanDeReconstruccion(
    int IdEmpresa, DateOnly Desde, DateOnly Hasta, int Turnos, int Filas,
    IReadOnlyList<AcreditacionDeReconstruccion> Acreditaciones, IReadOnlyList<CuentaDeReconstruccion> Cuentas);
