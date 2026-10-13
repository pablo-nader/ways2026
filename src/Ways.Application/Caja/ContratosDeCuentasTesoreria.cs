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

/// <summary>Cuenta con su saldo en la pantalla de tesorería. <see cref="SaldoActual"/> es el <c>final</c> del último
/// movimiento por id, o <see cref="SaldoInicial"/> si no tiene; <see cref="FechaUltimoMovimiento"/> es la fecha de ese
/// mismo movimiento, <c>null</c> sin movimientos.</summary>
public sealed record CuentaConSaldo(
    int Id,
    string Nombre,
    bool Activo,
    decimal SaldoInicial,
    decimal SaldoActual,
    DateTimeOffset? FechaUltimoMovimiento);

/// <summary>Medio de pago electrónico y activo cuyo cobro no entra a ninguna cuenta de la empresa.</summary>
public sealed record MedioElectronicoSinCuenta(int Id, string Nombre);

/// <summary>Respuesta de <c>GET /api/reportes/tesoreria/cuentas</c>: las cuentas vivas de la empresa y los medios
/// electrónicos activos que, al cerrar un turno, no acreditan en ninguna.</summary>
public sealed record CuentasDeLaEmpresa(
    int IdEmpresa,
    IReadOnlyList<CuentaConSaldo> Cuentas,
    IReadOnlyList<MedioElectronicoSinCuenta> MediosSinCuenta);

/// <summary>Fila del libro de una cuenta. <see cref="NombreMedioPago"/> y <see cref="NombreEmpleado"/> son <c>null</c> cuando
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

/// <summary>Totales del rango, calculados por el servidor sobre TODAS las filas del rango (no sobre la página). Los
/// descuentos (comisión y retenciones) son positivos cuando restan; <see cref="Cobrado"/> y <see cref="Ajustes"/> salen
/// con su signo. <see cref="Otros"/> suma los tipos que hoy ningún camino escribe (transferencia, depósito, retiro).
/// Siempre: apertura + <see cref="Neto"/> = saldo al cierre del rango.</summary>
public sealed record TotalesDeCuentaEnElRango(
    decimal Cobrado,
    decimal Comision,
    decimal RetencionIibb,
    decimal RetencionIva,
    decimal RetencionGanancias,
    decimal Ajustes,
    decimal Otros,
    decimal Neto);

/// <summary>Respuesta de <c>GET /api/reportes/tesoreria/cuentas/{idCuenta}/libro</c>. <see cref="SaldoDeApertura"/> es el
/// <c>final</c> del último movimiento anterior al rango (o el saldo inicial) y <see cref="SaldoAlCierre"/> es
/// apertura + neto del rango.</summary>
public sealed record LibroDeCuenta(
    int IdCuenta,
    string Nombre,
    bool Activo,
    decimal SaldoActual,
    decimal SaldoDeApertura,
    decimal SaldoAlCierre,
    TotalesDeCuentaEnElRango Totales,
    IReadOnlyList<MovimientoDeCuentaListado> Items,
    int Total,
    int Pagina,
    int Tamanio);

/// <summary>Ajuste manual de una cuenta. <see cref="Concepto"/> es obligatorio y se manda exactamente uno de
/// <see cref="Importe"/> (con signo: suma o resta) y <see cref="SaldoObjetivo"/> (el saldo real de la plataforma; el servidor
/// calcula la diferencia contra el saldo vigente bajo el lock). Todos son nullable para que su ausencia llegue al servicio y se
/// rechace con un código específico.</summary>
public sealed record SolicitudDeAjusteDeCuenta(string? Concepto, decimal? Importe, decimal? SaldoObjetivo);

/// <summary>Resultado de un ajuste: el movimiento escrito y el saldo resultante (el mismo <c>final</c>).</summary>
public sealed record ResultadoDeAjusteDeCuenta(MovimientoDeCuentaListado Movimiento, decimal SaldoActual);
