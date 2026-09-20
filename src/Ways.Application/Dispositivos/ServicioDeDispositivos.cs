using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;

namespace Ways.Application.Dispositivos;

/// <summary>
/// Vinculación y resolución de dispositivos de escritorio (stage-desktop-pos). El alta/listado/
/// revocación corren bajo la sesión normal de un Admin (RLS + query filter de tenant ya scopean);
/// la resolución del dispositivo (<see cref="ResolverActualAsync"/>/<see cref="ResolverIdentidadAsync"/>)
/// corre ANTES de que exista sesión alguna, así que lee <c>dispositivos</c> bajo el modo
/// <c>Login</c> (policy <c>dispositivos_login_lectura</c>, mismo patrón que <c>usuarios_login_lectura</c>)
/// y resuelve punto de venta/tenant/empresa contra el contexto de plataforma — no hay policy de
/// login sobre esas tablas, ni hace falta agregar una.
/// </summary>
public class ServicioDeDispositivos(
    IWaysDbContext db,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto)
{
    private static readonly ErrorDominio DispositivoNoVinculado =
        new("dispositivo_no_vinculado", "Este dispositivo no está vinculado a ningún punto de venta.", 404);

    /// <summary>Admin: vincula un dispositivo nuevo a un punto de venta de su propio tenant.
    /// Devuelve el DTO de respuesta MÁS el secreto en texto plano — el único momento en que
    /// existe fuera de la cookie; el llamador (endpoint) lo escribe en <c>ways.dispositivo</c>
    /// y lo descarta.
    ///
    /// judgment-day ronda 1 (hallazgo BLOCKER 1): el pre-chequeo de <c>puntoVenta.Modo</c> de más
    /// abajo, hecho ANTES de abrir la transacción, es solo UX rápida (404 temprano si el punto de
    /// venta ni siquiera existe) — la autoridad real es el RE-chequeo bajo
    /// <see cref="BloquearYLeerModoDePuntoVentaAsync"/> (<c>FOR UPDATE</c> sobre la fila, dentro de
    /// la MISMA transacción que el <c>INSERT</c>). Sin ese re-chequeo, esto y
    /// <see cref="Ways.Application.Organizacion.ServicioDeOrganizacion.ActualizarModoPuntoVentaAsync"/>
    /// podían correr en paralelo bajo READ COMMITTED y comitear los dos — un dispositivo activo
    /// vinculado a un punto de venta que el flip de modo acababa de pasar a Web. El lock lo toma
    /// el MISMO statement que hace <c>ActualizarModoPuntoVentaAsync</c>
    /// (<c>TomarLockDePuntoVentaAsync</c>) sobre la misma fila, así que las dos escrituras se
    /// serializan.</summary>
    public async Task<(DispositivoActual Datos, string Secreto)> CrearAsync(
        AltaDispositivo datos, CancellationToken ct = default)
    {
        var nombre = NombreDeDispositivo.Normalizar(datos.Nombre);

        // El query filter de EF (EntidadTenant) + RLS ya acotan esto al tenant del actor: si el
        // punto de venta es de otro tenant o está dado de baja, esto no aparece — 404, nunca un
        // oráculo de existencia cross-tenant (mismo criterio que el resto del ABM de organización).
        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == datos.IdPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {datos.IdPuntoVenta}.");

        // Invariante "una PC-caja = un punto de venta" (DB CHANGE GATE aprobado): vincular un
        // dispositivo a un punto de venta Web dejaría una fila muerta — ServicioDeVentas.
        // ResolverPuntoVentaAsync nunca aceptaría una venta de ese dispositivo contra ese punto de
        // venta (exige Escritorio). El backstop real de "a lo sumo un dispositivo activo" es
        // ux_dispositivos_punto_venta_activo (ManejadorDeErrores, 409 punto_venta_ya_tiene_dispositivo);
        // este chequeo es el de COMPATIBILIDAD de modo, una dimensión distinta. Best-effort a
        // propósito (solo evita abrir la transacción para un 404/409 obvio) — el RE-chequeo bajo
        // lock, más abajo, es la autoridad real.
        if (puntoVenta.Modo != ModoPuntoVenta.Escritorio)
        {
            throw new ErrorDominio(
                "punto_venta_modo_incompatible",
                "Solo un punto de venta en modo Escritorio puede tener un dispositivo vinculado.",
                409);
        }

        var (secreto, hash) = TokenDeDispositivo.GenerarNuevo();
        var ahora = reloj.Ahora;

        // Construida afuera del lambda: FabricaDeEstrategiaSinReintento (ef-retry-safe-writes,
        // forma (b)) es la que hace retry-safe esta alta, no el izado — se mantiene igual que
        // ServicioDeUsuarios.CrearAsync porque no hay ganancia en moverla adentro.
        var dispositivo = new Dispositivo
        {
            IdPuntoVenta = puntoVenta.Id,
            Nombre = nombre,
            TokenHash = hash,
            IdUsuarioAlta = contexto.UsuarioId,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var modoBajoLock = await BloquearYLeerModoDePuntoVentaAsync(puntoVenta.Id, ct);
            if (modoBajoLock != ModoPuntoVenta.Escritorio)
            {
                throw new ErrorDominio(
                    "punto_venta_modo_incompatible",
                    "Solo un punto de venta en modo Escritorio puede tener un dispositivo vinculado.",
                    409);
            }

            db.Dispositivos.Add(dispositivo);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);
        });

        var actual = await ProyectarDesdeDispositivoAsync(dispositivo, ct);
        return (actual, secreto);
    }

    /// <summary>
    /// Lock de fila (<c>FOR UPDATE</c>) sobre el punto de venta + relectura de <c>modo</c> bajo
    /// ese lock — el mismo statement, en espíritu, que
    /// <c>ServicioDeOrganizacion.TomarLockDePuntoVentaAsync</c> toma antes de re-chequear "sin
    /// dispositivo activo". <c>modo::text</c> por el mismo motivo que el resto de los enums leídos
    /// por ADO crudo en este repo (<c>ExigirTurnoAbiertoBajoLockAsync</c>): compara contra el
    /// literal <c>'escritorio'</c>, nunca contra el enum nativo de Npgsql.</summary>
    private async Task<ModoPuntoVenta> BloquearYLeerModoDePuntoVentaAsync(int idPuntoVenta, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT modo::text FROM puntos_venta WHERE id_punto_venta = $1 FOR UPDATE";

        var parametro = comando.CreateParameter();
        parametro.Value = idPuntoVenta;
        comando.Parameters.Add(parametro);

        var modo = (string?)await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"El punto de venta {idPuntoVenta} desapareció bajo el lock — ya se validó su existencia " +
                "afuera de esta transacción.");

        return modo switch
        {
            "escritorio" => ModoPuntoVenta.Escritorio,
            "web" => ModoPuntoVenta.Web,
            _ => throw new InvalidOperationException($"Modo de punto de venta desconocido: '{modo}'.")
        };
    }

    /// <summary>Admin: dispositivos activos (no revocados) del tenant en curso.</summary>
    public async Task<IReadOnlyList<DispositivoListado>> ListarAsync(CancellationToken ct = default) =>
        await db.Dispositivos
            .Join(db.PuntosVenta, d => d.IdPuntoVenta, p => p.Id, (d, p) => new { d, p })
            .OrderBy(x => x.d.Nombre)
            .Select(x => new DispositivoListado(
                x.d.Id,
                x.d.Nombre,
                new PuntoVentaDeDispositivo(x.p.Id, x.p.Nombre),
                x.d.CreatedAt,
                x.d.UltimoUsoAt))
            .ToListAsync(ct);

    /// <summary>Admin: revocación — baja lógica, nunca física (misma convención que el resto
    /// del esquema). 404 si el dispositivo no existe o no es del tenant en curso (RLS +
    /// query filter ya lo garantizan).</summary>
    public async Task RevocarAsync(int id, CancellationToken ct = default)
    {
        var dispositivo = await db.Dispositivos.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el dispositivo {id}.");

        dispositivo.Revocar(reloj.Ahora);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Anónimo: resuelve el dispositivo de la cookie para pintar el encabezado del POS
    /// de escritorio antes de cualquier login. El llamador tiene que haber puesto el contexto de
    /// tenant en modo <c>Login</c> antes de invocar esto (mismo contrato que
    /// <c>ServicioDeAutenticacion.IniciarSesionAsync</c>).</summary>
    public async Task<DispositivoActual> ResolverActualAsync(string? secreto, CancellationToken ct = default)
    {
        var dispositivo = await ResolverDispositivoVigenteAsync(secreto, ct);
        return await ProyectarDesdeDispositivoAsync(dispositivo, ct);
    }

    /// <summary>Anónimo: la identidad mínima (id de dispositivo + tenant) que
    /// <c>POST /api/auth/login-dispositivo</c> necesita para, a partir de ahí, resolver el login
    /// del cajero ya en modo <c>Tenant</c> (no hace falta una policy de login para
    /// <c>usuarios</c> en este camino: el tenant ya se conoce antes de tocar esa tabla).</summary>
    public async Task<(int IdDispositivo, int IdTenant)> ResolverIdentidadAsync(
        string? secreto, CancellationToken ct = default)
    {
        var dispositivo = await ResolverDispositivoVigenteAsync(secreto, ct);
        return (dispositivo.Id, dispositivo.IdTenant);
    }

    /// <summary>Anónimo: marca el uso — se llama recién después de un login de dispositivo
    /// exitoso, ya en modo <c>Tenant</c> (RLS <c>dispositivos_tenant</c> permite el UPDATE ahí;
    /// no hace falta una policy de escritura en modo login).</summary>
    public async Task RegistrarUsoAsync(int idDispositivo, CancellationToken ct = default)
    {
        var dispositivo = await db.Dispositivos.FirstOrDefaultAsync(d => d.Id == idDispositivo, ct);
        if (dispositivo is null)
        {
            return;
        }

        dispositivo.RegistrarUso(reloj.Ahora);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Núcleo común de <see cref="ResolverActualAsync"/>/<see cref="ResolverIdentidadAsync"/>:
    /// mismo 404 <c>dispositivo_no_vinculado</c> para "no existe", "token inválido", "revocado",
    /// "PV dado de baja" o "tenant suspendido/dado de baja" — nunca se distingue cuál, para no
    /// filtrar si un dispositivo existió alguna vez (mismo criterio que
    /// <c>ServicioDeAutenticacion</c> con el mail).</summary>
    private async Task<Dispositivo> ResolverDispositivoVigenteAsync(string? secreto, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secreto))
        {
            throw DispositivoNoVinculado;
        }

        var hash = TokenDeDispositivo.Hashear(secreto);

        // "Tenant" se ignora a propósito: bajo modo Login el tenant todavía no se conoce, así
        // que el filtro de EF (IdTenant == TenantActual.Id, null en Login) nunca podría pasar —
        // la policy dispositivos_login_lectura de RLS es la autorización real acá, igual que
        // usuarios_login_lectura para el login por mail. "BajaLogica" sigue activo: un
        // dispositivo revocado (deleted_at) no aparece.
        var dispositivo = await db.Dispositivos
            .IgnoreQueryFilters(["Tenant"])
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == hash, ct);

        if (dispositivo is null)
        {
            throw DispositivoNoVinculado;
        }

        // PV/tenant se resuelven en modo plataforma: no hay (ni hace falta agregar) una policy
        // de RLS en modo login sobre puntos_venta/tenants — mismo patrón que la verificación de
        // suspensión de tenant en ServicioDeAutenticacion.IniciarSesionAsync.
        var puntoVentaActivo = await dbPlataforma.PuntosVenta
            .AnyAsync(p => p.Id == dispositivo.IdPuntoVenta && p.IdTenant == dispositivo.IdTenant, ct);

        if (!puntoVentaActivo)
        {
            throw DispositivoNoVinculado;
        }

        var tenantActivo = await dbPlataforma.Tenants
            .AnyAsync(t => t.Id == dispositivo.IdTenant && t.Estado == EstadoTenant.Activo, ct);

        if (!tenantActivo)
        {
            throw DispositivoNoVinculado;
        }

        return dispositivo;
    }

    private async Task<DispositivoActual> ProyectarDesdeDispositivoAsync(
        Dispositivo dispositivo, CancellationToken ct)
    {
        var puntoVenta = await dbPlataforma.PuntosVenta
            .FirstAsync(p => p.Id == dispositivo.IdPuntoVenta && p.IdTenant == dispositivo.IdTenant, ct);

        var empresa = await dbPlataforma.Empresas
            .FirstAsync(e => e.Id == puntoVenta.IdEmpresa && e.IdTenant == dispositivo.IdTenant, ct);

        return new DispositivoActual(
            dispositivo.Id,
            dispositivo.Nombre,
            dispositivo.IdPuntoVenta,
            new PuntoVentaDeDispositivo(puntoVenta.Id, puntoVenta.Nombre),
            new EmpresaDeDispositivo(empresa.NombreFantasia ?? empresa.RazonSocial));
    }
}
