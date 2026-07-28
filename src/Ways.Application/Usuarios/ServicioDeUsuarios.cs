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
            query = query.IgnoreQueryFilters();
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

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        ValidarPassword(datos.Password);
        await ExigirRolExistenteAsync(datos.RolId, ct);
        await ExigirDisponibilidadAsync(nombre, mail, null, ct);

        var ahora = reloj.Ahora;
        var usuario = new Usuario
        {
            NombreUsuario = nombre,
            Mail = mail,
            RolId = datos.RolId,
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
        // sin tener permiso para convertirlo en admin.
        if (usuario.RolId != datos.RolId)
        {
            PoliticaDeRoles.ValidarPuedeAsignarRol(contexto.Rol, (RolConocido)datos.RolId);
            await ExigirRolExistenteAsync(datos.RolId, ct);
        }

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        await ExigirDisponibilidadAsync(nombre, mail, id, ct);

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
        return await db.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el usuario {id}.");
    }

    private async Task ExigirRolExistenteAsync(int rolId, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == rolId, ct))
        {
            throw new ErrorDominio("rol_inexistente", $"No existe el rol {rolId}.");
        }
    }

    private async Task ExigirDisponibilidadAsync(
        string usuario, string mail, int? excluirId, CancellationToken ct)
    {
        // citext: la comparación ya es case-insensitive en el motor.
        var tomadoUsuario = await db.Usuarios.AnyAsync(
            u => u.NombreUsuario == usuario && u.Id != excluirId, ct);

        if (tomadoUsuario)
        {
            throw ErrorDominio.Conflicto("usuario_duplicado", $"El usuario '{usuario}' ya existe.");
        }

        var tomadoMail = await db.Usuarios.AnyAsync(
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
