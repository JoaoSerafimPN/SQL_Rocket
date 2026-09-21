namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma linha capturada pelo "Profiler" (Manutenção &gt; Profiler) — mesmas
/// colunas do SQL Server Profiler clássico (EventClass, TextData,
/// ApplicationName, NTUserName, LoginName, CPU, Reads, Writes, Duration,
/// ClientProcessID, SPID, StartTime, EndTime, DatabaseID, DatabaseName,
/// HostName, RowCounts, ServerName), preenchida por
/// Modulo_Profiler.MontarEvento a partir de um evento de Extended Events —
/// tanto ao vivo (ring buffer de uma sessão criada pela própria ferramenta,
/// aba "Executar") quanto de um arquivo .xel já existente (aba "Analisar
/// arquivo"). Nem todo campo existe em todo tipo de evento (ex.: um evento
/// "Audit Login" não tem Duration/CPU/Reads/Writes/RowCounts — só os
/// eventos "…Completed" têm) — os campos que não existem para aquele
/// evento específico ficam null, exibidos como célula vazia na grade (não
/// como "0", que seria enganoso).
/// </summary>
public sealed class ProfilerEventoDto
{
    /// <summary>Nome no estilo do Profiler clássico (ex.: "SQL:BatchCompleted", "Audit Login") — ver Modulo_Profiler.CatalogoEventos para o mapeamento com o nome real do evento de Extended Events (ex.: "sql_batch_completed").</summary>
    public string EventClass { get; set; } = string.Empty;

    /// <summary>Texto do comando/lote (campo "batch_text" do sql_batch_completed, "statement" do rpc_completed, ou a mensagem do error_reported) — null nos eventos que não carregam texto (ex.: Audit Login/Logout).</summary>
    public string? TextData { get; set; }

    public string? ApplicationName { get; set; }
    public string? NTUserName { get; set; }
    public string? LoginName { get; set; }

    /// <summary>CPU consumida pelo comando, em MILISSEGUNDOS (o Extended Events guarda em microssegundos — já convertido aqui). Null nos eventos sem essa informação.</summary>
    public long? Cpu { get; set; }

    /// <summary>
    /// Leituras (o Profiler clássico usa "Reads" para leituras LÓGICAS, não
    /// físicas — mesma convenção seguida aqui: usa logical_reads quando
    /// existe, e cai para physical_reads só se logical_reads não vier no
    /// evento).
    /// </summary>
    public long? Reads { get; set; }

    public long? Writes { get; set; }

    /// <summary>Duração do comando, em MILISSEGUNDOS (Extended Events guarda em microssegundos — já convertido aqui). Null nos eventos "instantâneos" (Audit Login/Logout, Existing Connection, Attention, Error) — não é "0", é "não se aplica".</summary>
    public long? Duration { get; set; }

    public int? ClientProcessID { get; set; }

    /// <summary>SPID (session_id) — mesma sessão que aparece em sp_who2/Active Monitor.</summary>
    public int? Spid { get; set; }

    /// <summary>
    /// Calculado (não é um campo bruto do Extended Events — o evento só tem
    /// UM timestamp, o do momento em que foi disparado): num evento
    /// "…Completed" com Duration preenchida, StartTime = timestamp do
    /// evento MENOS a duração; nos demais (sem Duration), StartTime = o
    /// próprio timestamp do evento, e EndTime fica null.
    /// </summary>
    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public int? DatabaseID { get; set; }
    public string? DatabaseName { get; set; }
    public string? HostName { get; set; }

    /// <summary>Linhas afetadas/retornadas (campo "row_count") — só existe em eventos "…Completed".</summary>
    public long? RowCounts { get; set; }

    /// <summary>
    /// Nome do servidor — na captura AO VIVO (aba "Executar"), é o servidor
    /// atualmente conectado (informado pela tela, não lido do evento em si
    /// — ver observação em Modulo_Profiler.ColetarEventosAsync); ao
    /// ANALISAR um arquivo (aba "Analisar arquivo"), fica em branco, porque
    /// o arquivo pode ter sido capturado em qualquer servidor e o evento
    /// de Extended Events não guarda essa informação — mesmo princípio já
    /// usado em Modulo_Deadlock (não inventar uma informação que a fonte
    /// não tem).
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Timestamp bruto (UTC, precisão total) do evento de Extended Events —
    /// usado só internamente pelo polling ao vivo para saber quais eventos
    /// já foram mostrados (dedupe entre uma leitura do ring buffer e a
    /// próxima) e para ordenar a grade; não é uma coluna exibida (ver
    /// StartTime/EndTime, já convertidos para hora local, para isso).
    /// </summary>
    public DateTimeOffset TimestampBruto { get; set; }
}
