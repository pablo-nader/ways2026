namespace Ways.Api.ConectorMcp;

/// <summary>Mails que pueden autorizar el conector, leídos de un único valor de configuración
/// (<see cref="OpcionesDeMcp.MailsHabilitados"/>) con los mails separados por coma o punto y coma.
/// La comparación ignora mayúsculas y los espacios alrededor de cada mail.</summary>
public sealed class ListaDeMailsHabilitados(string? valor)
{
    private static readonly char[] Separadores = [',', ';'];

    private readonly HashSet<string> _mails = new(
        valor?.Split(Separadores, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        StringComparer.OrdinalIgnoreCase);

    public int Cantidad => _mails.Count;

    public bool Incluye(string? mail) => mail is not null && _mails.Contains(mail.Trim());
}
