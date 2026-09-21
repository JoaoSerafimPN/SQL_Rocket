namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um arquivo de log de erro do SQL Server — o log atual (<see cref="Numero"/> == 0)
/// ou um dos arquivos arquivados (1, 2, 3...) — retornado por
/// sys.xp_enumerrorlogs. Usado para popular a lista de "Selecionar arquivo"
/// da tela "Segurança &gt; Logs" (equivalente à árvore "Select logs" do Log
/// File Viewer do SSMS, aqui simplificada para um único arquivo por vez) e
/// para montar o texto de <see cref="LogErroDto.FonteLog"/> de cada linha
/// lida desse arquivo.
/// </summary>
public sealed class ArquivoLogErroDto
{
    public int Numero { get; set; }
    public DateTime DataCriacao { get; set; }
    public long TamanhoBytes { get; set; }

    /// <summary>
    /// "Current - dd/MM/yyyy HH:mm:ss" para o arquivo 0 (log atual, em uso),
    /// "Archive #N - dd/MM/yyyy HH:mm:ss" para os demais — mesmo formato
    /// usado pelo SSMS na coluna "Log Source" do Log File Viewer.
    /// </summary>
    public string Rotulo => (Numero == 0 ? "Current" : $"Archive #{Numero}") + $" - {DataCriacao:dd/MM/yyyy HH:mm:ss}";

    public override string ToString() => Rotulo;
}
