namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno para a listagem de usuários/logins do sistema.
/// Preenchido futuramente a partir de sys.server_principals /
/// sys.database_principals e sys.database_role_members.
/// </summary>
public sealed class UsuarioDto
{
    public string NomeLogin { get; set; } = string.Empty;
    public string TipoPrincipal { get; set; } = string.Empty; // SQL_LOGIN, WINDOWS_LOGIN, etc.
    public bool ContaDesabilitada { get; set; }
    public DateTime? DataCriacao { get; set; }
    public DateTime? UltimoLogin { get; set; }
    public IList<string> Perfis { get; set; } = new List<string>();
}
