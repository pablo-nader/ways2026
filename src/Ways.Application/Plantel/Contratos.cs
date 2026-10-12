namespace Ways.Application.Plantel;

/// <summary>Un vínculo que apunta a un usuario o cliente dado de baja se muestra como sin vincular:
/// <see cref="IdUsuario"/>/<see cref="NombreUsuario"/> y <see cref="IdCliente"/>/<see cref="NombreCliente"/>
/// llegan en <c>null</c> aunque la fila conserve el id.</summary>
public record PersonalListado(
    int Id,
    string Nombre,
    string? Documento,
    string? Telefono,
    string? Observaciones,
    decimal ValorHora,
    decimal Saldo,
    int? IdEmpresa,
    int? IdUsuario,
    string? NombreUsuario,
    int? IdCliente,
    string? NombreCliente);

/// <summary>Cuerpo del alta y de la edición. <see cref="ValorHora"/> admite hasta dos decimales y es
/// el valor por defecto de la próxima carga de horas. <see cref="IdUsuario"/> debe ser un usuario
/// vivo del tenant e <see cref="IdCliente"/> un cliente vivo que no sea el consumidor final; cada
/// uno se vincula a lo sumo a un personal vivo.</summary>
public record DatosDePersonal(
    string Nombre,
    string? Documento,
    string? Telefono,
    string? Observaciones,
    decimal ValorHora,
    int? IdEmpresa,
    int? IdUsuario,
    int? IdCliente);
