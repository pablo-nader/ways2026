using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>Alta de una cuenta de tesorería. La empresa solo se elige acá: una cuenta no cambia de
/// empresa, por eso <see cref="EdicionDeCuentaTesoreria"/> no la lleva. Los cuatro campos son obligatorios:
/// <see cref="IdEmpresa"/>, <see cref="SaldoInicial"/> y <see cref="Activo"/> son nullable para que su ausencia en
/// el JSON llegue al servicio y se rechace con <c>400 {campo}_requerido</c> en vez de tomar un valor por defecto.</summary>
public sealed record AltaDeCuentaTesoreria(int? IdEmpresa, string? Nombre, decimal? SaldoInicial, bool? Activo);

/// <summary>Edición de una cuenta; los tres campos son obligatorios (ver <see cref="AltaDeCuentaTesoreria"/>).
/// <see cref="SaldoInicial"/> solo puede diferir del guardado mientras la cuenta no tenga movimientos.</summary>
public sealed record EdicionDeCuentaTesoreria(string? Nombre, decimal? SaldoInicial, bool? Activo);

/// <summary>Fila del ABM de cuentas de tesorería. <see cref="SaldoActual"/> es el <c>final</c> del
/// último movimiento de la cuenta, o <see cref="SaldoInicial"/> si no tiene movimientos.
/// <see cref="SaldoInicialEditable"/> es falso cuando la cuenta ya tiene movimientos: lo calcula
/// el servidor para que la pantalla no lo deduzca.</summary>
public sealed record CuentaTesoreriaListado(
    int Id,
    int IdEmpresa,
    string? NombreEmpresa,
    string Nombre,
    decimal SaldoInicial,
    decimal SaldoActual,
    bool Activo,
    bool SaldoInicialEditable);

/// <summary>Movimiento de una cuenta tal como lo devuelve el ajuste manual. <see cref="NombreMedioPago"/> y <see cref="NombreEmpleado"/> son <c>null</c> cuando
/// la fila no tiene medio o cuando el medio o el usuario están dados de baja; el id se conserva para distinguir los dos casos.</summary>
public sealed record MovimientoDeCuentaListado(
    int Id,
    DateTimeOffset Fecha,
    TipoMovimientoCuenta Tipo,
    string Concepto,
    decimal Ingreso,
    decimal Egreso,
    decimal Final,
    int? IdTurnoCaja,
    int? IdMedioPago,
    string? NombreMedioPago,
    int IdEmpleado,
    string? NombreEmpleado,
    decimal? BaseBruta,
    decimal? PorcentajeAplicado);

/// <summary>Ajuste manual de una cuenta. <see cref="Concepto"/> es obligatorio y se manda exactamente uno de
/// <see cref="Importe"/> (con signo: suma o resta) y <see cref="SaldoObjetivo"/> (el saldo real de la plataforma; el servidor
/// calcula la diferencia contra el saldo vigente bajo el lock). Todos son nullable para que su ausencia llegue al servicio y se
/// rechace con un código específico.</summary>
public sealed record SolicitudDeAjusteDeCuenta(string? Concepto, decimal? Importe, decimal? SaldoObjetivo);

/// <summary>Resultado de un ajuste: el movimiento escrito y el saldo resultante (el mismo <c>final</c>).</summary>
public sealed record ResultadoDeAjusteDeCuenta(MovimientoDeCuentaListado Movimiento, decimal SaldoActual);
