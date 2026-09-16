namespace Ways.Application.Dispositivos;

/// <summary>Cuerpo de <c>POST /api/dispositivos</c> (Admin). <see cref="Nombre"/> se normaliza
/// (trim, 1..100) en <see cref="NombreDeDispositivo"/> — dto-contract-honesty: los dos campos se
/// usan, ninguno se acepta y se descarta.</summary>
public record AltaDispositivo(int IdPuntoVenta, string Nombre);

public record PuntoVentaDeDispositivo(int Numero, string Nombre);

public record EmpresaDeDispositivo(string Nombre);

/// <summary>Forma exacta que ya consume <c>src/Ways.Web/src/api/dispositivos.ts</c>
/// (<c>DispositivoActual</c>) — tanto <c>GET /actual</c> como <c>POST /</c> devuelven esto.</summary>
public record DispositivoActual(
    int Id,
    string Nombre,
    int IdPuntoVenta,
    PuntoVentaDeDispositivo PuntoVenta,
    EmpresaDeDispositivo Empresa);

/// <summary>Fila de <c>GET /api/dispositivos</c> (Admin) — dispositivos activos del tenant.</summary>
public record DispositivoListado(
    int Id,
    string Nombre,
    PuntoVentaDeDispositivo PuntoVenta,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UltimoUsoAt);
