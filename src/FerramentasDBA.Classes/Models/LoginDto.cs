using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um login do servidor (sys.server_principals) — SQL Server
/// Authentication ou Windows (usuário/grupo). Usado para popular a grade
/// da aba "Criar Login" e os combos de login das abas "Criar Usuário no
/// Banco" e "Permissões de Servidor", da tela "Segurança &gt; Usuários".
/// Preenchido em Modulo_Admin.ObterLoginsAsync.
/// </summary>
public sealed class LoginDto
{
    [DisplayName("Login")]
    public string Nome { get; set; } = string.Empty;

    [DisplayName("Autenticação")]
    public string TipoAutenticacao { get; set; } = string.Empty; // "SQL Server" ou "Windows"

    [DisplayName("Desabilitado")]
    public bool Desabilitado { get; set; }

    [DisplayName("Criado em")]
    public DateTime DataCriacao { get; set; }

    /// <summary>Usado pelo ComboBox (item exibido = nome do login).</summary>
    public override string ToString() => Nome;
}
