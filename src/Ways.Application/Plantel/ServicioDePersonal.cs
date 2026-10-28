using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Listados;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Plantel;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.Plantel;

/// <summary>
/// ABM del personal. La autorización (<c>LecturaDePersonal</c> para leer, <c>GestionDeCatalogo</c>
/// para escribir) se aplica en la capa de API.
///
/// Las comprobaciones de disponibilidad de usuario y cliente son pre-chequeos de UX: el contrato
/// real son los índices parciales <c>ux_personal_usuario</c> y <c>ux_personal_cliente</c>, que
/// <c>ManejadorDeErrores</c> traduce al mismo 409.
/// </summary>
public class ServicioDePersonal(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
{
    private const decimal ValorHoraMaximo = 999_999_999_999.99m;

    public async Task<PaginaDe<PersonalListado>> ListarAsync(
        string? busqueda = null,
        int pagina = 1,
        int tamanio = 25,
        FiltrosDePersonal? filtros = null,
        string? orden = null,
        string? direccion = null,
        CancellationToken ct = default)
    {
        // Antes de leer nada: una clave o dirección de orden desconocida es un pedido inválido aunque no haya filas.
        var ordenPedido = OrdenDePersonal.Interpretar(orden, direccion);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Personal.AsQueryable();

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            var patron = PatronDeContiene(busqueda.Trim());
            query = query.Where(p => Coincide(p.Nombre, patron) || Coincide(p.Documento, patron));
        }

        // Un solo LEFT JOIN alimenta filtros, total y orden: usuario y cliente se filtran y ordenan por el nombre
        // que la celda muestra, y un vínculo dado de baja llega como null (no coincide ni se ordena antes que nada).
        var unidas = Proyectar(query);
        if (filtros is not null)
        {
            unidas = Filtrar(unidas, filtros);
        }

        var total = await unidas.CountAsync(ct);

        var filas = await Ordenar(unidas, ordenPedido)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        return new PaginaDe<PersonalListado>(filas.Select(Armar).ToList(), total, pagina, tamanio);
    }

    private static IQueryable<FilaDePersonal> Filtrar(IQueryable<FilaDePersonal> filas, FiltrosDePersonal filtros)
    {
        if (FiltrosDeListado.PatronONulo(filtros.Nombre) is { } nombre)
        {
            // La celda muestra el nombre y, debajo, el documento: el filtro mira los dos.
            filas = filas.Where(f => Coincide(f.Personal.Nombre, nombre) || Coincide(f.Personal.Documento, nombre));
        }

        if (filtros.ValorHoraDesde is decimal valorDesde)
        {
            filas = filas.Where(f => f.Personal.ValorHora >= valorDesde);
        }

        if (filtros.ValorHoraHasta is decimal valorHasta)
        {
            filas = filas.Where(f => f.Personal.ValorHora <= valorHasta);
        }

        if (filtros.SaldoDesde is decimal saldoDesde)
        {
            filas = filas.Where(f => f.Personal.Saldo >= saldoDesde);
        }

        if (filtros.SaldoHasta is decimal saldoHasta)
        {
            filas = filas.Where(f => f.Personal.Saldo <= saldoHasta);
        }

        if (FiltrosDeListado.PatronONulo(filtros.Usuario) is { } usuario)
        {
            filas = filas.Where(f => Coincide(f.NombreUsuario, usuario));
        }

        if (FiltrosDeListado.PatronONulo(filtros.Cliente) is { } cliente)
        {
            // El mismo texto que NombreDeCliente, escrito inline porque EF no traduce una llamada a un método propio.
            filas = filas.Where(f => f.ClienteVivo != null && Coincide(
                !string.IsNullOrWhiteSpace(f.ClienteVivo.RazonSocial)
                    ? f.ClienteVivo.RazonSocial
                    : (f.ClienteVivo.Nombre + " " + (f.ClienteVivo.Apellido ?? string.Empty)).Trim(),
                cliente));
        }

        return filas;
    }

    /// <summary>Orden de SQL, aplicado antes de paginar. Sin orden pedido: nombre. Una celda vacía (sin vincular, o
    /// vinculada a un registro dado de baja) va al final en las dos direcciones.</summary>
    private static IOrderedQueryable<FilaDePersonal> Ordenar(
        IQueryable<FilaDePersonal> filas, OrdenDeListado<ColumnaDeOrdenDePersonal>? orden)
    {
        if (orden is null)
        {
            return filas.OrderBy(f => f.Personal.Nombre).ThenBy(f => f.Personal.Id);
        }

        var desc = orden.Value.Descendente;
        var ordenadas = orden.Value.Columna switch
        {
            ColumnaDeOrdenDePersonal.ValorHora => desc
                ? filas.OrderByDescending(f => f.Personal.ValorHora)
                : filas.OrderBy(f => f.Personal.ValorHora),
            ColumnaDeOrdenDePersonal.Saldo => desc
                ? filas.OrderByDescending(f => f.Personal.Saldo)
                : filas.OrderBy(f => f.Personal.Saldo),
            ColumnaDeOrdenDePersonal.Usuario => desc
                ? filas.OrderBy(f => f.NombreUsuario == null).ThenByDescending(f => f.NombreUsuario)
                : filas.OrderBy(f => f.NombreUsuario == null).ThenBy(f => f.NombreUsuario),
            ColumnaDeOrdenDePersonal.Cliente => desc
                ? filas.OrderBy(f => f.ClienteVivo == null).ThenByDescending(f =>
                    f.ClienteVivo == null ? null
                    : !string.IsNullOrWhiteSpace(f.ClienteVivo.RazonSocial) ? f.ClienteVivo.RazonSocial
                    : (f.ClienteVivo.Nombre + " " + (f.ClienteVivo.Apellido ?? string.Empty)).Trim())
                : filas.OrderBy(f => f.ClienteVivo == null).ThenBy(f =>
                    f.ClienteVivo == null ? null
                    : !string.IsNullOrWhiteSpace(f.ClienteVivo.RazonSocial) ? f.ClienteVivo.RazonSocial
                    : (f.ClienteVivo.Nombre + " " + (f.ClienteVivo.Apellido ?? string.Empty)).Trim()),
            _ => desc
                ? filas.OrderByDescending(f => f.Personal.Nombre)
                : filas.OrderBy(f => f.Personal.Nombre),
        };

        return ordenadas.ThenBy(f => f.Personal.Id);
    }

    public async Task<PersonalListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var fila = await Proyectar(db.Personal.Where(p => p.Id == id)).FirstOrDefaultAsync(ct)
            ?? throw SinPersonal(id);

        return Armar(fila);
    }

    public async Task<PersonalListado> CrearAsync(DatosDePersonal datos, CancellationToken ct = default)
    {
        var normalizados = Normalizar(datos);
        await ExigirReferenciasAsync(datos, excluirId: null, ct);

        var ahora = reloj.Ahora;
        var personal = new Personal
        {
            Nombre = normalizados.Nombre,
            Documento = normalizados.Documento,
            Telefono = normalizados.Telefono,
            Observaciones = normalizados.Observaciones,
            ValorHora = datos.ValorHora,
            IdEmpresa = datos.IdEmpresa,
            IdUsuario = datos.IdUsuario,
            IdCliente = datos.IdCliente,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.Personal.Add(personal);
        await db.SaveChangesAsync(ct);

        return await ObtenerAsync(personal.Id, ct);
    }

    public async Task<PersonalListado> ActualizarAsync(int id, DatosDePersonal datos, CancellationToken ct = default)
    {
        if (!await db.Personal.AnyAsync(p => p.Id == id, ct))
        {
            throw SinPersonal(id);
        }

        var normalizados = Normalizar(datos);
        await ExigirReferenciasAsync(datos, excluirId: id, ct);

        var personal = await db.Personal.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw SinPersonal(id);

        personal.Nombre = normalizados.Nombre;
        personal.Documento = normalizados.Documento;
        personal.Telefono = normalizados.Telefono;
        personal.Observaciones = normalizados.Observaciones;
        personal.ValorHora = datos.ValorHora;
        personal.IdEmpresa = datos.IdEmpresa;
        personal.IdUsuario = datos.IdUsuario;
        personal.IdCliente = datos.IdCliente;
        personal.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);

        return await ObtenerAsync(id, ct);
    }

    /// <summary>Baja lógica: libera el usuario y el cliente vinculados (los índices únicos solo miran
    /// filas vivas). Solo procede si ninguna fila referencia al personal (por ejemplo, un movimiento
    /// de su cuenta): el lock de la fila va antes de la lectura y de la consulta de referencias, sin
    /// reintento, como en la baja de proveedores.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<Personal>(id, ct);

            var personal = await db.Personal.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw SinPersonal(id);

            await guarda.ExigirSinReferenciasAsync(
                personal, "personal_en_uso", "el personal", etiquetasPropias: null, ct);

            var ahora = reloj.Ahora;
            personal.DeletedAt = ahora;
            personal.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });
    }

    private static ErrorDominio SinPersonal(int id) =>
        ErrorDominio.NoEncontrado($"No existe el personal {id}.");

    /// <summary>Se arma con inicializador de objeto y no con constructor posicional: EF resuelve
    /// <c>fila.Personal.Nombre</c> de un inicializador, y no de un constructor, al filtrar y ordenar por encima del
    /// join.</summary>
    private sealed class FilaDePersonal
    {
        public required Personal Personal { get; init; }
        public int? IdUsuarioVivo { get; init; }
        public string? NombreUsuario { get; init; }
        public Cliente? ClienteVivo { get; init; }
    }

    /// <summary>LEFT JOIN: un usuario o cliente dado de baja deja de ser visible para el join y la
    /// fila se devuelve igual, sin ese vínculo.</summary>
    private IQueryable<FilaDePersonal> Proyectar(IQueryable<Personal> personal) =>
        from p in personal
        join u in db.Usuarios on p.IdUsuario equals u.Id into usuarios
        from u in usuarios.DefaultIfEmpty()
        join c in db.Clientes on p.IdCliente equals c.Id into clientes
        from c in clientes.DefaultIfEmpty()
        select new FilaDePersonal
        {
            Personal = p,
            IdUsuarioVivo = u == null ? null : u.Id,
            NombreUsuario = u == null ? null : u.NombreUsuario,
            ClienteVivo = c,
        };

    private static PersonalListado Armar(FilaDePersonal fila)
    {
        var p = fila.Personal;
        var cliente = fila.ClienteVivo;

        return new PersonalListado(
            p.Id, p.Nombre, p.Documento, p.Telefono, p.Observaciones, p.ValorHora, p.Saldo, p.IdEmpresa,
            fila.IdUsuarioVivo, fila.NombreUsuario,
            cliente?.Id, cliente is null ? null : NombreDeCliente(cliente));
    }

    private static string NombreDeCliente(Cliente cliente) =>
        !string.IsNullOrWhiteSpace(cliente.RazonSocial)
            ? cliente.RazonSocial
            : $"{cliente.Nombre} {cliente.Apellido}".Trim();

    private record Normalizados(string Nombre, string? Documento, string? Telefono, string? Observaciones);

    private static Normalizados Normalizar(DatosDePersonal datos)
    {
        var nombre = datos.Nombre?.Trim() ?? string.Empty;
        if (nombre.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El campo nombre es obligatorio.", 400);
        }

        var resultado = new Normalizados(
            nombre.Length <= 150 ? nombre : throw MuyLargo("nombre", 150),
            Opcional(datos.Documento, "documento", 30),
            Opcional(datos.Telefono, "telefono", 50),
            Opcional(datos.Observaciones, "observaciones", null));

        if (datos.ValorHora < 0
            || datos.ValorHora > ValorHoraMaximo
            || decimal.Round(datos.ValorHora, 2) != datos.ValorHora)
        {
            throw new ErrorDominio(
                "valor_hora_invalido", "El valor hora debe estar entre 0 y 999999999999.99, con hasta dos decimales.", 400);
        }

        return resultado;
    }

    private static ErrorDominio MuyLargo(string campo, int maximo) =>
        new($"{campo}_muy_largo", $"El campo {campo} no puede superar los {maximo} caracteres.", 400);

    private static string? Opcional(string? valor, string campo, int? largoMaximo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        return largoMaximo is { } maximo && limpio.Length > maximo ? throw MuyLargo(campo, maximo) : limpio;
    }

    private async Task ExigirReferenciasAsync(DatosDePersonal datos, int? excluirId, CancellationToken ct)
    {
        if (datos.IdEmpresa is { } idEmpresa && !await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
        }

        if (datos.IdUsuario is { } idUsuario)
        {
            // El filtro de tenant de EF deja invisibles los usuarios de otros tenants y los de la
            // plataforma (id_tenant NULL), junto con los dados de baja.
            if (!await db.Usuarios.AnyAsync(u => u.Id == idUsuario, ct))
            {
                throw new ErrorDominio("usuario_invalido", $"No existe el usuario {idUsuario} en este tenant.", 400);
            }

            if (await db.Personal.AnyAsync(p => p.IdUsuario == idUsuario && p.Id != excluirId, ct))
            {
                throw ErrorDominio.Conflicto("usuario_ya_vinculado", "Ese usuario ya está vinculado a otro personal.");
            }
        }

        if (datos.IdCliente is { } idCliente)
        {
            var cliente = await db.Clientes
                .Where(c => c.Id == idCliente)
                .Select(c => new { c.Numero })
                .FirstOrDefaultAsync(ct)
                ?? throw new ErrorDominio("referencia_invalida", $"No existe el cliente {idCliente}.", 400);

            if (ReglaDeClientes.EsConsumidorFinal(cliente.Numero))
            {
                throw new ErrorDominio(
                    "cliente_consumidor_final", "El consumidor final no puede vincularse a un personal.", 400);
            }

            if (await db.Personal.AnyAsync(p => p.IdCliente == idCliente && p.Id != excluirId, ct))
            {
                throw ErrorDominio.Conflicto("cliente_ya_vinculado", "Ese cliente ya está vinculado a otro personal.");
            }
        }
    }
}
