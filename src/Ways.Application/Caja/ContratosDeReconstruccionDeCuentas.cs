using Ways.Domain.Caja;
using Ways.Domain.Pagos;

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

/// <summary>Débito que el reconciliador de pagos habría escrito: un pago vivo, todavía sin filas <c>pago</c>, en la cuenta actual de
/// uno de sus medios, fechado con la fecha del pago. <see cref="Importe"/> es lo que sale de la cuenta.</summary>
public sealed record DebitoDeReconstruccion(
    int IdPago, DateTimeOffset Fecha, CategoriaPago Categoria, string Concepto, int IdMedioPago, string NombreMedio,
    int IdCuentaTesoreria, string NombreCuenta, decimal Importe);

/// <summary>Efecto sobre una cuenta: filas nuevas, su neto y el saldo vigente antes y después.</summary>
public sealed record CuentaDeReconstruccion(
    int IdCuentaTesoreria, string Nombre, int Filas, decimal Neto, decimal SaldoAntes, decimal SaldoDespues);

/// <summary>La vista previa devuelve lo que se escribiría; la aplicación, lo que se escribió. Sin nada pendiente, las listas
/// vienen vacías y <see cref="Filas"/> es cero. <see cref="Filas"/> cuenta las filas de cierre de <see cref="Acreditaciones"/> más
/// una por cada débito de <see cref="Debitos"/>; <see cref="Turnos"/> y <see cref="Pagos"/>, los turnos y los pagos distintos. Los
/// turnos con fecha de cierre y los pagos con fecha menor o igual a la marca de conciliación de la empresa
/// (<see cref="ConciliadoHasta"/>) están congelados: no se reconstruyen y lo pendiente se informa aparte en
/// <see cref="Congeladas"/> y <see cref="DebitosCongelados"/>, sin contarlo en los contadores.</summary>
public sealed record PlanDeReconstruccion(
    int IdEmpresa, DateOnly Desde, DateOnly Hasta, int Turnos, int Pagos, int Filas,
    IReadOnlyList<AcreditacionDeReconstruccion> Acreditaciones, IReadOnlyList<DebitoDeReconstruccion> Debitos,
    IReadOnlyList<CuentaDeReconstruccion> Cuentas, DateTimeOffset? ConciliadoHasta,
    IReadOnlyList<AcreditacionDeReconstruccion> Congeladas, IReadOnlyList<DebitoDeReconstruccion> DebitosCongelados);
