namespace Ways.Application.Listados;

/// <summary>Columnas por las que <c>GET /api/compras</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeCompras
{
    Numero,
    Proveedor,
    Tipo,
    Estado,
    FechaRecepcion,
    Total,
}

/// <summary>Columnas por las que <c>GET /api/presupuestos</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDePresupuestos
{
    Numero,
    Cliente,
    PuntoVenta,
    Estado,
    Vencimiento,
    FechaEmision,
    Total,
}

/// <summary>Columnas por las que <c>GET /api/remitos</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeRemitos
{
    Numero,
    Cliente,
    PuntoVenta,
    Estado,
    FechaEmision,
    Total,
}

/// <summary>Columnas por las que <c>GET /api/ordenes-compra</c> acepta <c>orden</c>.</summary>
public enum ColumnaDeOrdenDeOrdenesDeCompra
{
    Numero,
    Proveedor,
    PuntoVenta,
    Estado,
    FechaEmision,
    FechaEsperada,
}
