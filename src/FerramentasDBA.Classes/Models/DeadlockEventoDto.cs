namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um evento de deadlock (&lt;deadlock&gt;) ou de head blocking
/// (&lt;blocked-process-report&gt;) do SQL Server, com os processos
/// envolvidos já resolvidos — ver <see cref="TipoEvento"/> para saber qual
/// dos dois é. Um mesmo arquivo .xel/.xml pode conter vários eventos — cada
/// um vira um <see cref="DeadlockEventoDto"/> na lista retornada por
/// Modulo_Deadlock.AnalisarArquivoXelAsync/AnalisarArquivoXmlAsync, exibida
/// na grade "Eventos encontrados" da tela Manutenção &gt; Headblock e
/// DeadLock — selecionar uma linha ali mostra o detalhe ("Resumo dos
/// Processos Envolvidos") deste evento.
/// </summary>
public sealed class DeadlockEventoDto
{
    /// <summary>Horário do evento — vem do atributo "timestamp" do &lt;event&gt; (quando presente no XML) ou do próprio evento do .xel; null quando nenhuma das duas fontes tinha essa informação.</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>
    /// "Deadlock" (evento xml_deadlock_report — tem vítima/vencedor, alguém
    /// é cancelado) ou "BlockedProcess" (evento blocked_process_report —
    /// head blocking, tem processo bloqueado/bloqueador, ninguém é
    /// cancelado). Usado pela tela para decidir o texto dos cabeçalhos da
    /// tabela "Resumo dos Processos Envolvidos".
    /// </summary>
    public string TipoEvento { get; set; } = "Deadlock";

    /// <summary>Versão em português de <see cref="TipoEvento"/>, usada como coluna da grade "Eventos encontrados".</summary>
    public string DescricaoTipoEvento => string.Equals(TipoEvento, "BlockedProcess", StringComparison.OrdinalIgnoreCase)
        ? "Bloqueio (Head Blocking)"
        : "Deadlock";

    public List<DeadlockProcessoDto> Processos { get; set; } = new();

    /// <summary>XML original do evento (&lt;deadlock&gt; ou &lt;blocked-process-report&gt;), guardado para referência/depuração (não exibido na grade principal).</summary>
    public string XmlBruto { get; set; } = string.Empty;

    /// <summary>Usado como coluna da grade "Eventos encontrados".</summary>
    public int QuantidadeProcessos => Processos.Count;

    /// <summary>Usado como coluna da grade "Eventos encontrados" — ex.: "146, 171".</summary>
    public string ResumoSpids => string.Join(", ", Processos.Select(p => p.Spid));
}
