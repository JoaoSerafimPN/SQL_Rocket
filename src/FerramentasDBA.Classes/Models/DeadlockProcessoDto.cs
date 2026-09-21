namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Um dos processos (SPIDs) envolvidos em um deadlock — uma linha do
/// &lt;process-list&gt; do deadlock graph do SQL Server, já com o "Bloqueio
/// Retido"/"Bloqueio Solicitado" resolvidos cruzando com o
/// &lt;resource-list&gt; do mesmo deadlock. Preenchido em
/// Modulo_Deadlock, consumido pela tela Manutenção &gt; Headblock e
/// DeadLock (tabela "Resumo dos Processos Envolvidos").
/// </summary>
public sealed class DeadlockProcessoDto
{
    /// <summary>Id interno do processo dentro do deadlock graph (ex.: "process29bf6f67c8") — usado só para cruzar com owner/waiter, não é útil sozinho para o usuário.</summary>
    public string ProcessId { get; set; } = string.Empty;

    public string Spid { get; set; } = string.Empty;

    /// <summary>
    /// "Processo prejudicado" — sentido depende do tipo de evento (ver
    /// <see cref="DeadlockEventoDto.TipoEvento"/>): num Deadlock, true
    /// quando este processo está em &lt;victim-list&gt; (o SQL Server
    /// escolheu ele para cancelar/dar rollback); num BlockedProcess
    /// (head blocking), true quando este é o processo BLOQUEADO (o que
    /// ficou esperando — ninguém é cancelado nesse tipo de evento).
    /// </summary>
    public bool EhVitima { get; set; }

    /// <summary>
    /// Resumo tipo "SELECT de Pedidos"/"INSERT na VD_PEDIDOSSTATUS" — melhor
    /// esforço, obtido com uma expressão regular simples sobre o texto do
    /// primeiro frame da executionStack (ou o inputbuf, se o frame não tiver
    /// texto inline). Não é um parser de T-SQL de verdade — comandos fora do
    /// padrão SELECT/INSERT/UPDATE/DELETE/MERGE aparecem só com a primeira
    /// linha do comando, sem tentar adivinhar a tabela.
    /// </summary>
    public string OperacaoPrimaria { get; set; } = string.Empty;

    /// <summary>
    /// "Ad-hoc query (Consulta direta)" quando o frame não tem procname; ou
    /// "Trigger &lt;nome&gt;"/"Procedure &lt;nome&gt;" quando tem — a
    /// distinção Trigger/Procedure é uma HEURÍSTICA por convenção de nome
    /// (contém "TR_"), já que o deadlock graph sozinho não diz o tipo do
    /// objeto (isso exigiria consultar sys.objects do banco onde ocorreu, o
    /// que não é feito aqui — a análise é 100% offline, a partir do arquivo
    /// enviado).
    /// </summary>
    public string GatilhoOuCodigo { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;
    public string LoginName { get; set; } = string.Empty;
    public string ClientApp { get; set; } = string.Empty;

    /// <summary>Ex.: "Lock de Leitura (S) na VD_PEDIDOS" — recurso que este processo já possuía (owner) quando o deadlock foi detectado. Vazio quando este processo não aparece como owner de nenhum recurso do deadlock.</summary>
    public string BloqueioRetidoDescricao { get; set; } = string.Empty;

    /// <summary>Ex.: "Lock Exclusivo (X) na VD_PEDIDOSPLUS" — recurso que este processo estava esperando (waiter) quando o deadlock foi detectado.</summary>
    public string BloqueioSolicitadoDescricao { get; set; } = string.Empty;
}
