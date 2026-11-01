namespace Ways.Application.Listados;

/// <summary>Columnas por las que <c>GET /api/reportes/cajas</c> (histórico de cajas) acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeHistoricoDeCajas
{
    Turno,
    PuntoVenta,
    Apertura,
    Cierre,
    Esperado,
    Declarado,
    Diferencia,
}

/// <summary>Columnas por las que <c>GET /api/caja/turnos</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeTurnos
{
    Turno,
    Apertura,
    Cierre,
}

/// <summary>Columnas por las que <c>GET /api/proveedores/{id}/cuenta-corriente</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeCuentaDeProveedor
{
    Fecha,
    Tipo,
    Referencia,
    Detalle,
    Importe,
    SaldoResultante,
}

/// <summary>Columnas por las que <c>GET /api/personal/{id}/cuenta</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeCuentaDePersonal
{
    Fecha,
    Tipo,
    Horas,
    ValorHora,
    Importe,
    SaldoResultante,
    Detalle,
}

/// <summary>Columnas por las que <c>GET /api/caja/retiros</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeRetiros
{
    Fecha,
    PuntoVenta,
    Turno,
    Empleado,
    Importe,
    Motivo,
    EstadoTurno,
    Conciliado,
    EsRetiroDeCierre,
}
