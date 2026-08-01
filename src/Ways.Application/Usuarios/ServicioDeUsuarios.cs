using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

/// <summary>
/// ABM de usuarios. Toda decisión de "quién puede hacer qué" delega en
/// <see cref="PoliticaDeRoles"/>, que es puro dominio y se testea sin base.
/// </summary>
public class ServicioDeUsuarios(
    IWaysDbContext db,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto)
{
    private const int LargoMinimoPassword = 8;

    /// <summary>Identidad tenant-aware del actor en curso, para <see cref="PoliticaDeRoles"/>
    /// (doc 09, ADR-8).</summary>
    private ActorDeGestion Actor => new(contexto.Rol, contexto.UsuarioId, contexto.IdTenant);

    public async Task<PaginaDe<UsuarioListado>> ListarAsync(
        string? busqueda = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Usuarios.Include(u => u.Rol).AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) y filtraría cuentas de otros tenants al admin.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            // Las columnas son citext, así que el LIKE que genera Contains
            // ya es case-insensitive sin necesidad de ILIKE.
            var termino = busqueda.Trim();
            query = query.Where(u =>
                u.NombreUsuario.Contains(termino) || u.Mail.Contains(termino));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(u => u.RolId).ThenBy(u => u.NombreUsuario)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(u => new UsuarioListado(
                u.Id, u.NombreUsuario, u.Mail, u.RolId,
                u.Rol!.Nombre, u.Estado, u.UltimaConexion, u.CreatedAt))
            .ToListAsync(ct);

        return new PaginaDe<UsuarioListado>(items, total, pagina, tamanio);
    }

    public async Task<UsuarioListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        var usuario = await BuscarAsync(id, ct);

        return new UsuarioListado(
            usuario.Id, usuario.NombreUsuario, usuario.Mail, usuario.RolId,
            usuario.Rol!.Nombre, usuario.Estado, usuario.UltimaConexion, usuario.CreatedAt);
    }

    public async Task<UsuarioListado> CrearAsync(CrearUsuario datos, CancellationToken ct = default)
    {
        PoliticaDeRoles.ValidarPuedeAsignarRol(contexto.Rol, (RolConocido)datos.RolId);

        // Un actor de tenant siempre crea dentro de su propio tenant — el valor que haya
        // llegado en `datos.IdTenant` se ignora, no se confía en lo que mande el cliente
        // (doc 09: el tenant nunca viaja como parámetro editable). Solo un actor de
        // plataforma puede (y tiene que) elegir el tenant destino explícitamente.
        var idTenantDestino = Actor.EsDePlataforma ? datos.IdTenant : Actor.IdTenant;
        PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance((RolConocido)datos.RolId, idTenantDestino);

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        ValidarPassword(datos.Password);
        await ExigirRolExistenteAsync(datos.RolId, ct);
        await ExigirDisponibilidadAsync(nombre, mail, idTenantDestino, null, ct);

        var ahora = reloj.Ahora;
        var usuario = new Usuario
        {
            NombreUsuario = nombre,
            Mail = mail,
            RolId = datos.RolId,
            IdTenant = idTenantDestino,
            Estado = datos.Estado,
            PasswordHash = hasheador.Hashear(datos.Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync(ct);

        return await ObtenerAsync(usuario.Id, ct);
    }

    public async Task<UsuarioListado> ActualizarAsync(
        int id, ActualizarUsuario datos, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);

        // Cambiar el rol se valida aparte: podés editar el mail de un supervisor
        // sin tener permiso para convertirlo en admin. El tenant de la cuenta no se toca
        // acá (es inmutable una vez creada), solo se revalida que el rol nuevo siga siendo
        // consistente con él.
        if (usuario.RolId != datos.RolId)
        {
            PoliticaDeRoles.ValidarPuedeAsignarRol(contexto.Rol, (RolConocido)datos.RolId);
            PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance((RolConocido)datos.RolId, usuario.IdTenant);
            await ExigirRolExistenteAsync(datos.RolId, ct);
        }

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        await ExigirDisponibilidadAsync(nombre, mail, usuario.IdTenant, id, ct);

        var estabaBloqueado = usuario.Estado == EstadoUsuario.Bloqueado;

        usuario.NombreUsuario = nombre;
        usuario.Mail = mail;
        usuario.RolId = datos.RolId;
        usuario.Estado = datos.Estado;
        usuario.UpdatedAt = reloj.Ahora;

        // Pasar de bloqueado a activo limpia el contador de intentos fallidos,
        // si no la cuenta se vuelve a bloquear al primer error.
        if (estabaBloqueado && datos.Estado == EstadoUsuario.Activo)
        {
            usuario.Desbloquear(reloj.Ahora);
        }

        await db.SaveChangesAsync(ct);
        return await ObtenerAsync(id, ct);
    }

    public async Task CambiarPasswordAsync(
        int id, CambiarPassword datos, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        // Cualquiera puede cambiar su propia contraseña; sobre la de otro rigen las reglas de rol.
        if (usuario.Id != contexto.UsuarioId)
        {
            PoliticaDeRoles.ValidarPuedeIntervenirSobre(
                contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);
        }

        ValidarPassword(datos.PasswordNueva);

        usuario.CambiarPassword(
            hasheador.Hashear(datos.PasswordNueva), hasheador.Algoritmo, reloj.Ahora);

        await db.SaveChangesAsync(ct);
    }

    public async Task DesbloquearAsync(int id, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);

        usuario.Desbloquear(reloj.Ahora);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Baja lógica: escribe deleted_at, no borra la fila.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: true);

        usuario.DeletedAt = reloj.Ahora;
        usuario.UpdatedAt = reloj.Ahora;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Roles que el usuario autenticado puede asignar. Alimenta el combo del ABM.</summary>
    public async Task<IReadOnlyList<RolListado>> RolesAsignablesAsync(CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        var permitidos = PoliticaDeRoles.RolesAsignablesPor(contexto.Rol)
            .Select(r => (int)r)
            .ToArray();

        return await db.Roles
            .Where(r => permitidos.Contains(r.Id))
            .OrderBy(r => r.Id)
            .Select(r => new RolListado(r.Id, r.Nombre, r.Descripcion))
            .ToListAsync(ct);
    }

    private void ExigirPermisoDeGestion()
    {
        if (!PoliticaDeRoles.PuedeGestionarUsuarios(contexto.Rol))
        {
            throw ErrorDominio.Prohibido("No tenés permisos para gestionar usuarios.");
        }
    }

    private async Task<Usuario> BuscarAsync(int id, CancellationToken ct)
    {
        var usuario = await db.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el usuario {id}.");

        // El filtro de EF (más RLS por debajo) ya deja invisible una cuenta de otro
        // alcance, así que en la práctica esto nunca dispara sobre una fila que sí llegó a
        // `usuario` — es la capa de defensa explícita del dominio (doc 09, ADR-8), no un
        // sustituto de las otras dos.
        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, usuario.IdTenant);

        return usuario;
    }

    private async Task ExigirRolExistenteAsync(int rolId, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == rolId, ct))
        {
            throw new ErrorDominio("rol_inexistente", $"No existe el rol {rolId}.");
        }
    }

    private async Task ExigirDisponibilidadAsync(
        string usuario, string mail, int? idTenantScope, int? excluirId, CancellationToken ct)
    {
        // `usuario` es único por tenant, no global (doc 09, ADR-7): dos tenants pueden
        // tener cada uno un "admin" sin choque. La agrupación de plataforma
        // (`id_tenant IS NULL`) es un tenant más a este efecto — `NULLS NOT DISTINCT` en el
        // índice hace que este `== idTenantScope` con `idTenantScope: null` también
        // funcione una vez que exista la columna (gate #2 pendiente).
        // citext: la comparación ya es case-insensitive en el motor.
        var tomadoUsuario = await db.Usuarios.AnyAsync(
            u => u.NombreUsuario == usuario && u.IdTenant == idTenantScope && u.Id != excluirId, ct);

        if (tomadoUsuario)
        {
            throw ErrorDominio.Conflicto("usuario_duplicado", $"El usuario '{usuario}' ya existe.");
        }

        // A diferencia de `usuario`, el mail es único GLOBAL, no por tenant (`ux_usuarios_mail`
        // no lleva id_tenant). `IgnoreQueryFilters(["Tenant"])` es obligatorio acá: sin él, un
        // actor de tenant solo ve su propio alcance y una colisión con otro tenant pasaría este
        // chequeo para reventar recién en el `SaveChangesAsync` (23505) — el backstop de
        // `ManejadorDeErrores` cubre esa carrera, pero el chequeo previo tiene que intentar
        // atajarla igual para devolver el 409 de negocio en el camino feliz.
        var tomadoMail = await db.Usuarios.IgnoreQueryFilters(["Tenant"]).AnyAsync(
            u => u.Mail == mail && u.Id != excluirId, ct);

        if (tomadoMail)
        {
            throw ErrorDominio.Conflicto("mail_duplicado", $"El mail '{mail}' ya está en uso.");
        }
    }

    private static string Normalizar(string? valor, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo",
                $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static void ValidarPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < LargoMinimoPassword)
        {
            throw new ErrorDominio(
                "password_debil",
                $"La contraseña debe tener al menos {LargoMinimoPassword} caracteres.", 400);
        }
    }
}
