using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha do painel "Conexões por Aplicação/Usuário" do Active Monitor
/// (Local e Azure — script fornecido pelo usuário originalmente para o
/// Azure e depois pedido também no Local): contagem de conexões de
/// usuário ativas agrupadas por Aplicação (program_name) e Usuário
/// (login_name), a partir de sys.dm_exec_sessions. Preenchido em
/// Modulo_Desempenho.ObterConexoesAgrupadasAsync — mesma consulta genérica
/// (nenhuma DMV específica de Azure), reaproveitada nas duas telas.
/// </summary>
public sealed class ConexaoAgrupadaDto
{
    [DisplayName("Aplicação")]
    public string Aplicacao { get; set; } = string.Empty;

    [DisplayName("Usuário")]
    public string Usuario { get; set; } = string.Empty;

    [DisplayName("Total de Conexões")]
    public int TotalConexoes { get; set; }
}
