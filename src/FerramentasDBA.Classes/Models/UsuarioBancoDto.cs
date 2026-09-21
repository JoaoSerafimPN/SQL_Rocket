using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um usuário de um banco de dados específico (sys.database_principals)
/// — já vinculado (ou não) a um login do servidor. Usado para popular a
/// grade da aba "Criar Usuário no Banco" e o combo de usuário da aba
/// "Permissões de Banco/Tabelas e Views", da tela "Segurança &gt;
/// Usuários". Preenchido em Modulo_Admin.ObterUsuariosBancoAsync.
/// </summary>
public sealed class UsuarioBancoDto
{
    [DisplayName("Usuário")]
    public string Nome { get; set; } = string.Empty;

    [DisplayName("Login associado")]
    public string? LoginAssociado { get; set; }

    [DisplayName("Tipo")]
    public string TipoUsuario { get; set; } = string.Empty;

    /// <summary>Usado pelo ComboBox (item exibido = nome do usuário).</summary>
    public override string ToString() => Nome;
}
