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
