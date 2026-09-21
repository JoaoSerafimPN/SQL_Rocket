using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Informações de hardware/sistema operacional do host onde a instância SQL
/// Server está rodando, exibidas em "Informações > Servidor".
/// Preenchido em Modulo_Informacoes.ObterInformacoesServidorAsync.
/// </summary>
/// <remarks>
/// Alguns campos (Domínio, Versão do Windows, HDs) dependem de DMVs/recursos
/// que podem não existir em toda edição/versão — em especial no SQL Azure
/// (PaaS, sem sistema operacional exposto) — e nesses casos vêm preenchidos
/// com "Não disponível" em vez de lançar exceção. Os atributos [DisplayName]
/// controlam o rótulo exibido no PropertyGrid da tela.
/// </remarks>
public sealed class InformacoesServidorDto
{
    [DisplayName("Nome")]
    public string Nome { get; set; } = string.Empty;

    [DisplayName("IP")]
    public string Ip { get; set; } = string.Empty;

    [DisplayName("Memória")]
    public string Memoria { get; set; } = string.Empty;

    [DisplayName("Processador")]
    public string Processador { get; set; } = string.Empty;

    /// <summary>
    /// Uma linha por unidade que hospeda arquivos de banco de dados (não é
    /// necessariamente todo disco físico do host — ver observação em
    /// Modulo_Informacoes).
    /// </summary>
    [DisplayName("HDs")]
    public string Hds { get; set; } = string.Empty;

    [DisplayName("Versão do Windows")]
    public string VersaoWindows { get; set; } = string.Empty;

    [DisplayName("Domínio")]
    public string Dominio { get; set; } = string.Empty;
}
