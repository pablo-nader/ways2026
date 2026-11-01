using Ways.Domain.Organizacion;

namespace Ways.Application.Organizacion;

/// <summary><paramref name="Modo"/> es OBLIGATORIO (skill <c>dto-contract-honesty</c>): docs/10
/// §9.1 dice que el modo nunca se infiere y no tiene default de base. Es <c>nullable</c> a
/// proposito — sin el <c>?</c>, un body JSON que omite <c>modo</c> liga el parametro al default
/// del CLR, que es <see cref="ModoPuntoVenta.Escritorio"/> (valor 0), y el tenant nace con un
/// punto de venta contra el que nadie puede vender: la web lo rechaza por modo y todavia no tiene
/// dispositivo vinculado. El <c>?</c> hace distinguible "omitido" de "Escritorio" y deja que
/// <c>ServicioDeAprovisionamiento</c> devuelva 400 en vez de aprovisionar algo inservible.</summary>
public record SolicitudDeAprovisionamiento(
    string NombreTenant,
    string RazonSocialEmpresa,
    string NombrePuntoVenta,
    string MailAdmin,
    ModoPuntoVenta? Modo);

/// <summary><paramref name="PasswordTemporal"/> se devuelve UNA sola vez, en esta respuesta:
/// no se persiste en texto plano en ningún lado (ADR-16) — solo el hash queda en
/// <c>usuarios</c>.</summary>
public record ResultadoAprovisionamiento(
    int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuarioAdmin, string PasswordTemporal);

// --- Lectura/edición de organización (ServicioDeOrganizacion) ---
// El alta de tenants sigue siendo plataforma-only vía ServicioDeAprovisionamiento (ADR-16); estos
// contratos cubren listado/detalle/edición de datos descriptivos, suspensión de tenants y el alta
// de puntos de venta sobre una empresa existente.

/// <summary>Los tres contadores son hijos VIVOS del tenant (el filtro <c>"BajaLogica"</c> corre
/// dentro de las subconsultas correlacionadas de <see cref="ServicioDeOrganizacion"/>) y
/// <paramref name="CantidadUsuarios"/> cuenta solo cuentas del tenant: el personal de plataforma
/// (<c>id_tenant IS NULL</c>) no se cuenta bajo ningún tenant.</summary>
public record TenantListado(
    int Id,
    string Nombre,
    EstadoTenant Estado,
    DateTimeOffset CreatedAt,
    int CantidadEmpresas,
    int CantidadPuntosVenta,
    int CantidadUsuarios);

/// <summary>Solo el nombre: <see cref="EstadoTenant"/> se cambia por las acciones dedicadas
/// (suspender/reactivar), no por esta edición general.</summary>
public record TenantEdicion(string Nombre);

/// <summary><paramref name="NombreTenant"/> es nullable a propósito (design D13): si el tenant
/// dueño quedó dado de baja, la empresa se sigue listando con el nombre en <c>null</c> — se
/// muestra como anomalía en vez de desaparecer del listado. <paramref name="IdTenant"/> deja de
/// renderizarse y pasa a ser la clave del filtro por tenant.</summary>
public record EmpresaListado(
    int Id,
    int IdTenant,
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    string? NombreTenant,
    decimal? AlicuotaPercepcionIibb,
    decimal? AlicuotaPercepcionIva,
    string? IngresosBrutos = null,
    DateOnly? InicioActividades = null);

/// <summary>Edición completa: las dos alícuotas de percepción (porcentajes entre 0 y 100, hasta 3
/// decimales), <paramref name="IngresosBrutos"/> (texto libre, hasta 30 caracteres) e
/// <paramref name="InicioActividades"/> se reemplazan con lo que llegue, y <c>null</c> (o el campo
/// ausente, que el binding no distingue de <c>null</c>) los deja sin informar: un PUT sin estos
/// campos los borra, igual que a las alícuotas. El formulario siempre manda todo.</summary>
public record EmpresaEdicion(
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    decimal? AlicuotaPercepcionIibb = null,
    decimal? AlicuotaPercepcionIva = null,
    string? IngresosBrutos = null,
    DateOnly? InicioActividades = null);

/// <summary>Los dos nombres de dueño son nullable por el mismo criterio que
/// <see cref="EmpresaListado.NombreTenant"/> (design D13); <paramref name="IdTenant"/> e
/// <paramref name="IdEmpresa"/> dejan de renderizarse y quedan como claves de los dos filtros.</summary>
public record PuntoVentaListado(
    int Id,
    int IdTenant,
    int IdEmpresa,
    string Nombre,
    string? Domicilio,
    string? Horario,
    string? Whatsapp,
    string? Instagram,
    string? Facebook,
    string? Web,
    string? NombreTenant,
    string? RazonSocialEmpresa,
    ModoPuntoVenta Modo,
    bool EnvioPorDefecto,
    int IdSucursal = 0,
    string? NombreSucursal = null);

/// <summary>Cuerpo de <c>POST /api/puntos-venta/{id}/modo</c> (stage-desktop-pos, gate aprobado):
/// el único campo editable acá es el modo — no comparte forma con <see cref="PuntoVentaEdicion"/>
/// porque el flip tiene su propia precondición (sin dispositivo activo) y su propio rastro de
/// auditoría, ninguno de los dos aplicable a la edición descriptiva.</summary>
/// <c>Modo</c> es nullable por la misma razon que en <see cref="SolicitudDeAprovisionamiento"/>:
/// un body que omite el campo ligaria el parametro a <see cref="ModoPuntoVenta.Escritorio"/>
/// (valor 0 del CLR) y el flip mutaria el punto de venta en silencio en vez de rechazar el pedido.
public record PuntoVentaModoEdicion(ModoPuntoVenta? Modo);

/// <summary>Cuerpo de <c>POST /api/puntos-venta</c>: alta de un punto de venta sobre una empresa
/// EXISTENTE. <paramref name="IdEmpresa"/> y <paramref name="Modo"/> son <c>nullable</c> a propósito,
/// por la misma razón que en <see cref="SolicitudDeAprovisionamiento"/>: un body que los omite
/// ligaría el valor al default del CLR (<c>0</c> y <see cref="ModoPuntoVenta.Escritorio"/>) en vez
/// de rechazarse con <c>400</c>. El resto es la misma lista descriptiva de
/// <see cref="PuntoVentaEdicion"/>, con las mismas validaciones; <c>EnvioPorDefecto</c> no se
/// acepta en el alta: nace en <c>false</c> y se cambia con la edición.</summary>
public record PuntoVentaAlta(
    int? IdEmpresa,
    string Nombre,
    ModoPuntoVenta? Modo,
    string? Domicilio = null,
    string? Horario = null,
    string? Whatsapp = null,
    string? Instagram = null,
    string? Facebook = null,
    string? Web = null,
    int? IdSucursal = null);

/// <summary><see cref="PuntoVentaListado.IdEmpresa"/> no es editable acá: es estructural
/// (a qué empresa pertenece), no descriptivo — moverlo de empresa queda fuera de esta
/// edición.</summary>
public record PuntoVentaEdicion(
    string Nombre,
    string? Domicilio,
    string? Horario,
    string? Whatsapp,
    string? Instagram,
    string? Facebook,
    string? Web,
    bool EnvioPorDefecto = false);

/// <summary>Una sucursal viva con la cantidad de cajas vivas que tiene. <paramref name="RazonSocialEmpresa"/> es
/// nullable por el mismo criterio que <see cref="PuntoVentaListado.RazonSocialEmpresa"/>.</summary>
public record SucursalListado(
    int Id,
    int IdTenant,
    int IdEmpresa,
    string Nombre,
    string? RazonSocialEmpresa,
    int CantidadPuntosVenta,
    string? Calle = null,
    string? Altura = null,
    string? Ciudad = null,
    string? Provincia = null,
    string? CodigoPostal = null,
    decimal? Latitud = null,
    decimal? Longitud = null,
    string? Referencia = null);

/// <summary>Cuerpo de <c>POST /api/sucursales</c>. <paramref name="IdEmpresa"/> es <c>nullable</c> a propósito (un body
/// que lo omite ligaría <c>0</c> y daría un 404 engañoso en vez del <c>400 empresa_requerida</c>).
/// El resto es la dirección estructurada, toda opcional (<see cref="Ways.Domain.Organizacion.DireccionDeSucursal"/>).</summary>
public record SucursalAlta(
    int? IdEmpresa,
    string Nombre,
    string? Calle = null,
    string? Altura = null,
    string? Ciudad = null,
    string? Provincia = null,
    string? CodigoPostal = null,
    decimal? Latitud = null,
    decimal? Longitud = null,
    string? Referencia = null);

/// <summary>Cuerpo de <c>PUT /api/sucursales/{id}</c>: el nombre y la dirección son lo editable; la empresa es
/// estructural. La dirección se reemplaza entera: un campo omitido la deja vacía.</summary>
public record SucursalEdicion(
    string Nombre,
    string? Calle = null,
    string? Altura = null,
    string? Ciudad = null,
    string? Provincia = null,
    string? CodigoPostal = null,
    decimal? Latitud = null,
    decimal? Longitud = null,
    string? Referencia = null);

/// <summary>Cuerpo de <c>POST /api/puntos-venta/{id}/sucursal</c>: la sucursal destino de la caja. <c>Nullable</c> por la
/// misma razón que <see cref="PuntoVentaModoEdicion"/>: omitido no puede ligarse a <c>0</c>.</summary>
public record PuntoVentaSucursalEdicion(int? IdSucursal);

/// <summary>Cuerpo de <c>POST /api/sucursales/{id}/fusionar</c>: la sucursal que absorbe a la del id. <c>Nullable</c> por
/// la misma razón que <see cref="PuntoVentaSucursalEdicion"/>.</summary>
public record SucursalFusion(int? IdSucursalDestino);
