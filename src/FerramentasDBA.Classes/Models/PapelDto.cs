namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um papel fixo (server role ou database role) e se o login/usuário
/// avaliado é membro dele — usado nas grades "Papéis do Servidor"/
/// "Papéis do Banco de Dados" da tela "Segurança &gt; Usuários" (checkbox
/// "Membro" editável pelo próprio usuário da ferramenta antes de clicar
/// em "Aplicar Alterações"). Preenchido/consumido em
/// Modulo_Admin.ObterPapeisServidorAsync/ObterPapeisBancoAsync e
/// AtualizarPapeisServidorAsync/AtualizarPapeisBancoAsync.
/// </summary>
public sealed class PapelDto
{
    public string Nome { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public bool Membro { get; set; }
}
