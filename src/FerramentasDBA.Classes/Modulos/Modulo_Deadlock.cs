using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.XEvent.XELite;
using FerramentasDBA.Classes.Models;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Análise offline de deadlocks E de head blocking (bloqueio sem matar
/// ninguém) do SQL Server, a partir de um arquivo já exportado pelo
/// usuário — sem nenhuma conexão com o banco de dados (por isso, ao
/// contrário dos outros módulos, este não recebe um Conectar_SQL no
/// construtor). Cobre o menu Manutenção &gt; Headblock e DeadLock: leitura
/// de arquivos .xel (Extended Events, via o pacote
/// Microsoft.SqlServer.XEvent.XELite) e de arquivos .xml — em ambos os
/// casos podendo conter eventos "xml_deadlock_report" (deadlock graph,
/// elemento &lt;deadlock&gt; — tem vítima/vencedor) e/ou
/// "blocked_process_report" (head blocking, elemento
/// &lt;blocked-process-report&gt; — tem processo bloqueado/bloqueador, sem
/// nenhum ser cancelado).
///
/// Em todos os casos o resultado final é a mesma lista de
/// <see cref="DeadlockEventoDto"/> (o campo <see cref="DeadlockEventoDto.TipoEvento"/>
/// diz qual dos dois é), já com "Bloqueio Retido"/"Bloqueio Solicitado"
/// resolvidos quando o tipo de evento permite — a tela só precisa exibir,
/// não reprocessar XML.
/// </summary>
public class Modulo_Deadlock
{
    /// <summary>
    /// Tradução dos modos de lock do SQL Server (atributo "mode" dos nós
    /// &lt;owner&gt;/&lt;waiter&gt; do deadlock graph) para uma descrição em
    /// português. Modo não mapeado aqui aparece com o próprio código bruto
    /// (ver <see cref="ObterDescricaoModo"/>).
    /// </summary>
    private static readonly Dictionary<string, string> DescricoesModoLock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S"] = "Lock de Leitura (S)",
        ["X"] = "Lock Exclusivo (X)",
        ["U"] = "Lock de Atualização (U)",
        ["IS"] = "Lock de Intenção de Leitura (IS)",
        ["IX"] = "Lock de Intenção Exclusiva (IX)",
        ["SIX"] = "Lock de Intenção Exclusiva/Leitura (SIX)",
        ["Sch-S"] = "Lock de Estabilidade de Esquema (Sch-S)",
        ["Sch-M"] = "Lock de Modificação de Esquema (Sch-M)",
        ["BU"] = "Lock de Bulk Update (BU)",
        ["RangeS-S"] = "Lock de Intervalo Leitura-Leitura (RangeS-S)",
        ["RangeS-U"] = "Lock de Intervalo Leitura-Atualização (RangeS-U)",
        ["RangeX-X"] = "Lock de Intervalo Exclusivo (RangeX-X)",
    };

    /// <summary>
    /// Lê um arquivo .xml já contendo um ou mais eventos de deadlock e/ou
    /// blocked process report. Aceita vários formatos, para cobrir os
    /// jeitos mais comuns do usuário chegar nesse XML: um &lt;deadlock&gt;
    /// ou &lt;blocked-process-report&gt; solto (ex.: "Save As" no SSMS); um
    /// &lt;event&gt; completo, do jeito que
    /// <c>sys.fn_xe_file_target_read_file</c> devolve (com &lt;data&gt;/
    /// &lt;value&gt; em volta do relatório, e o atributo "timestamp" do
    /// evento, usado quando presente); ou vários &lt;event&gt; concatenados
    /// sem um elemento raiz único envolvendo todos (comum ao colar direto o
    /// resultado de uma query com várias linhas) — nesse caso um &lt;events&gt;
    /// sintético é criado por baixo dos panos só para conseguir fazer o parse.
    /// </summary>
    public async Task<List<DeadlockEventoDto>> AnalisarArquivoXmlAsync(string caminhoArquivo, CancellationToken ct = default)
    {
        GarantirTamanhoAnalisavel(caminhoArquivo, "XML");

        var texto = await File.ReadAllTextAsync(caminhoArquivo, ct);
        var resultado = AnalisarXmlBruto(texto, timestampPadrao: null);
        if (resultado.Count == 0)
        {
            throw new InvalidOperationException(
                "Nenhum \"deadlock\" nem \"blocked_process_report\" foi encontrado neste arquivo XML.");
        }
        return resultado;
    }

    /// <summary>
    /// Lê um arquivo .xel (Extended Events) — como o gerado pela sessão de
    /// sistema "system_health" ou por uma sessão de captura dedicada — e
    /// extrai todo evento "xml_deadlock_report" (campo "xml_report") e/ou
    /// "blocked_process_report" (campo "blocked_process") nele contido. Usa
    /// o pacote Microsoft.SqlServer.XEvent.XELite, que lê o binário do .xel
    /// diretamente (sem precisar de uma instância do SQL Server instalada
    /// na máquina).
    ///
    /// Arquivos .xel de uma sessão ainda ATIVA (a sessão continua gravando
    /// nele no momento do envio) podem terminar incompletos e derrubar a
    /// leitura no meio — nesse caso, se algum evento já tiver sido lido com
    /// sucesso antes da falha, ele é devolvido mesmo assim (melhor esforço,
    /// em vez de descartar tudo); só quando NADA foi lido é que o erro
    /// original vira uma mensagem amigável sugerindo tentar de novo com um
    /// arquivo já fechado (ex.: depois que a sessão rolar para o próximo
    /// arquivo).
    /// </summary>
    public async Task<List<DeadlockEventoDto>> AnalisarArquivoXelAsync(string caminhoArquivo, CancellationToken ct = default)
    {
        var resultado = new List<DeadlockEventoDto>();
        var xeStream = new XEFileEventStreamer(caminhoArquivo);

        try
        {
            await xeStream.ReadEventStream(xevent =>
            {
                string? nomeCampo = null;
                if (string.Equals(xevent.Name, "xml_deadlock_report", StringComparison.OrdinalIgnoreCase))
                {
                    nomeCampo = "xml_report";
                }
                else if (string.Equals(xevent.Name, "blocked_process_report", StringComparison.OrdinalIgnoreCase))
                {
                    nomeCampo = "blocked_process";
                }

                if (nomeCampo is not null && xevent.Fields.TryGetValue(nomeCampo, out var valorCampo))
                {
                    var xmlTexto = valorCampo as string ?? valorCampo?.ToString();
                    if (!string.IsNullOrWhiteSpace(xmlTexto))
                    {
                        var timestamp = ObterTimestampSeguro(xevent);
                        resultado.AddRange(AnalisarXmlBruto(xmlTexto, timestamp));
                    }
                }
                return Task.CompletedTask;
            }, ct);
        }
        // ORDEM IMPORTA: o cancelamento vem ANTES do "catch (Exception) when
        // (resultado.Count > 0)". Estava depois, e o filtro engolia também a
        // OperationCanceledException — cancelar a leitura de um arquivo grande
        // devolvia os eventos parciais COMO SE fossem o arquivo inteiro.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (resultado.Count > 0)
        {
            // Já conseguimos ler pelo menos alguns eventos antes de uma
            // falha no meio do arquivo — ver nota no comentário do método.
            // Segue com o que já foi lido em vez de descartar tudo.
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Não foi possível ler este arquivo .xel até o fim (erro: \"" + ex.Message + "\"). O motivo mais " +
                "provável é o arquivo estar incompleto — por exemplo, a sessão de Extended Events ainda está " +
                "ativa e gravando nele neste momento. Tente novamente depois que o arquivo \"fechar\" (a sessão " +
                "rolar para o próximo arquivo, ou for parada), ou envie um arquivo anterior já finalizado. " +
                "Alternativa enquanto isso: use o botão \"Enviar arquivo XML\" com o resultado de " +
                "sys.fn_xe_file_target_read_file (SELECT ... FOR XML PATH(''), ROOT('events'), TYPE) — o " +
                "SQL Server já sabe ler esse .xel sem depender desta biblioteca.", ex);
        }

        if (resultado.Count == 0)
        {
            throw new InvalidOperationException(
                "Nenhum evento \"xml_deadlock_report\" nem \"blocked_process_report\" foi encontrado neste arquivo .xel.");
        }

        return resultado;
    }

    /// <summary>
    /// Lê o campo "Timestamp" de um IXEvent do XELite de forma defensiva.
    /// A documentação pública do pacote não deixa claro se essa propriedade
    /// é um DateTime ou um DateTimeOffset (evidência encontrada em código de
    /// terceiros que consome a mesma API mostra só ".ToLocalTime()", que
    /// existe nos dois tipos) — por isso o acesso é feito via "dynamic",
    /// que resolve o membro em tempo de execução em vez de travar a
    /// compilação caso o tipo real não seja o esperado. Se mesmo assim algo
    /// não bater (tipo totalmente inesperado, campo ausente etc.), o evento
    /// simplesmente fica sem timestamp em vez de derrubar a análise inteira
    /// do arquivo .xel.
    /// </summary>
    private static DateTime? ObterTimestampSeguro(dynamic xevent)
    {
        try
        {
            dynamic bruto = xevent.Timestamp;
            return bruto is DateTimeOffset comOffset ? comOffset.LocalDateTime : (DateTime)bruto;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ponto único de parsing de XML, compartilhado pelas duas entradas
    /// (.xel e .xml) e pelos dois tipos de evento (deadlock e blocked
    /// process report) — procura por &lt;deadlock&gt; e por
    /// &lt;blocked-process-report&gt; em qualquer nível do documento,
    /// tratando cada ocorrência como um evento independente. Isso cobre o
    /// nó solto, o nó embrulhado num &lt;event&gt; (formato de
    /// <c>sys.fn_xe_file_target_read_file</c>) e múltiplos eventos no mesmo
    /// documento — inclusive um &lt;deadlock&gt; e um
    /// &lt;blocked-process-report&gt; juntos, caso existam.
    /// </summary>
    private List<DeadlockEventoDto> AnalisarXmlBruto(string xmlTexto, DateTime? timestampPadrao)
    {
        var resultado = new List<DeadlockEventoDto>();

        XElement raiz;
        try
        {
            raiz = XElement.Parse(xmlTexto);
        }
        catch (Exception)
        {
            // Provável causa: vários <event>...</event> concatenados sem um
            // elemento raiz único (comum ao colar direto várias linhas de
            // resultado de uma query) — tenta de novo embrulhando tudo num
            // <events> sintético antes de desistir.
            try
            {
                raiz = XElement.Parse($"<events>{xmlTexto}</events>");
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException("O conteúdo não é um XML válido.", ex2);
            }
        }

        foreach (var deadlockEl in BuscarElementos(raiz, "deadlock"))
        {
            var evento = ParsearDeadlock(deadlockEl, ObterTimestampDoEvento(deadlockEl) ?? timestampPadrao);
            if (evento is not null)
                resultado.Add(evento);
        }

        foreach (var bprEl in BuscarElementos(raiz, "blocked-process-report"))
        {
            var evento = ParsearBlockedProcessReport(bprEl, ObterTimestampDoEvento(bprEl) ?? timestampPadrao);
            if (evento is not null)
                resultado.Add(evento);
        }

        return resultado;
    }

    /// <summary>Acha todo elemento chamado <paramref name="nomeLocal"/> no documento — a própria raiz, se for ela, mais qualquer descendente (cobre nó solto e nó embrulhado em outras tags).</summary>
    private static IEnumerable<XElement> BuscarElementos(XElement raiz, string nomeLocal)
    {
        if (raiz.Name.LocalName.Equals(nomeLocal, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { raiz };
        }
        return raiz.Descendants().Where(el => el.Name.LocalName.Equals(nomeLocal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Quando o elemento (deadlock ou blocked-process-report) está dentro
    /// de um &lt;event name="..." timestamp="..."&gt; — o formato que
    /// <c>sys.fn_xe_file_target_read_file</c> devolve — usa esse timestamp,
    /// que é mais preciso que o vindo do .xel (quando disponível) e é a
    /// ÚNICA fonte de timestamp para um XML solto (sem passar por .xel).
    /// </summary>
    private static DateTime? ObterTimestampDoEvento(XElement elemento)
    {
        var eventoAncestral = elemento.AncestorsAndSelf()
            .FirstOrDefault(el => el.Name.LocalName.Equals("event", StringComparison.OrdinalIgnoreCase));
        var timestampAttr = eventoAncestral?.Attribute("timestamp")?.Value;
        if (string.IsNullOrWhiteSpace(timestampAttr))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            timestampAttr, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.LocalDateTime
            : null;
    }

    /// <summary>
    /// Monta um <see cref="DeadlockEventoDto"/> a partir de um único nó
    /// &lt;deadlock&gt;: identifica vítima(s) em victim-list, monta um
    /// DeadlockProcessoDto por processo em process-list, e cruza cada
    /// processo com resource-list para preencher "Bloqueio Retido"
    /// (aparece como owner de algum recurso) e "Bloqueio Solicitado"
    /// (aparece como waiter de algum recurso).
    /// </summary>
    private DeadlockEventoDto? ParsearDeadlock(XElement deadlockEl, DateTime? timestamp)
    {
        var processListEl = deadlockEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("process-list", StringComparison.OrdinalIgnoreCase));
        var resourceListEl = deadlockEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("resource-list", StringComparison.OrdinalIgnoreCase));
        var victimListEl = deadlockEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("victim-list", StringComparison.OrdinalIgnoreCase));

        if (processListEl is null)
            return null;

        var idsVitimas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (victimListEl is not null)
        {
            foreach (var victimProc in victimListEl.Elements().Where(e => e.Name.LocalName.Equals("victimProcess", StringComparison.OrdinalIgnoreCase)))
            {
                var idAttr = victimProc.Attribute("id")?.Value;
                if (!string.IsNullOrEmpty(idAttr))
                    idsVitimas.Add(idAttr);
            }
        }

        var processos = new List<DeadlockProcessoDto>();
        foreach (var processoEl in processListEl.Elements().Where(e => e.Name.LocalName.Equals("process", StringComparison.OrdinalIgnoreCase)))
        {
            var processId = processoEl.Attribute("id")?.Value ?? string.Empty;

            var frameEl = processoEl.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("frame", StringComparison.OrdinalIgnoreCase));
            var inputBufEl = processoEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("inputbuf", StringComparison.OrdinalIgnoreCase));

            var textoComando = !string.IsNullOrWhiteSpace(frameEl?.Value)
                ? frameEl!.Value
                : (inputBufEl?.Value ?? string.Empty);

            var procname = frameEl?.Attribute("procname")?.Value ?? string.Empty;

            var dto = new DeadlockProcessoDto
            {
                ProcessId = processId,
                Spid = processoEl.Attribute("spid")?.Value ?? string.Empty,
                EhVitima = !string.IsNullOrEmpty(processId) && idsVitimas.Contains(processId),
                OperacaoPrimaria = DescreverOperacao(textoComando),
                GatilhoOuCodigo = DescreverOrigemCodigo(procname),
                HostName = processoEl.Attribute("hostname")?.Value ?? string.Empty,
                LoginName = processoEl.Attribute("loginname")?.Value ?? string.Empty,
                ClientApp = processoEl.Attribute("clientapp")?.Value ?? string.Empty,
            };

            processos.Add(dto);
        }

        if (resourceListEl is not null)
        {
            foreach (var recursoEl in resourceListEl.Elements())
            {
                var descricaoRecurso = DescreverRecurso(recursoEl);
                if (descricaoRecurso is null)
                    continue;

                var ownerListEl = recursoEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("owner-list", StringComparison.OrdinalIgnoreCase));
                var waiterListEl = recursoEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("waiter-list", StringComparison.OrdinalIgnoreCase));

                if (ownerListEl is not null)
                {
                    foreach (var ownerEl in ownerListEl.Elements().Where(e => e.Name.LocalName.Equals("owner", StringComparison.OrdinalIgnoreCase)))
                    {
                        var idProcessoOwner = ownerEl.Attribute("id")?.Value;
                        var processo = processos.FirstOrDefault(p => string.Equals(p.ProcessId, idProcessoOwner, StringComparison.OrdinalIgnoreCase));
                        if (processo is not null && string.IsNullOrEmpty(processo.BloqueioRetidoDescricao))
                            processo.BloqueioRetidoDescricao = descricaoRecurso;
                    }
                }

                if (waiterListEl is not null)
                {
                    foreach (var waiterEl in waiterListEl.Elements().Where(e => e.Name.LocalName.Equals("waiter", StringComparison.OrdinalIgnoreCase)))
                    {
                        var idProcessoWaiter = waiterEl.Attribute("id")?.Value;
                        var processo = processos.FirstOrDefault(p => string.Equals(p.ProcessId, idProcessoWaiter, StringComparison.OrdinalIgnoreCase));
                        if (processo is not null && string.IsNullOrEmpty(processo.BloqueioSolicitadoDescricao))
                            processo.BloqueioSolicitadoDescricao = descricaoRecurso;
                    }
                }
            }
        }

        return new DeadlockEventoDto
        {
            Timestamp = timestamp,
            Processos = processos,
            XmlBruto = deadlockEl.ToString(),
            TipoEvento = "Deadlock",
        };
    }

    /// <summary>
    /// Monta um <see cref="DeadlockEventoDto"/> a partir de um único nó
    /// &lt;blocked-process-report&gt; (evento "blocked_process_report" —
    /// head blocking: uma sessão trava esperando um lock que outra sessão
    /// está segurando, sem ninguém ser cancelado — bem diferente de um
    /// deadlock). Estrutura bem mais simples que o deadlock graph: sempre
    /// exatamente 2 processos, um &lt;blocked-process&gt; (o que está
    /// esperando) e um &lt;blocking-process&gt; (o que está segurando o
    /// recurso e causando a espera).
    ///
    /// IMPORTANTE — limitação deste tipo de evento (não é bug, é a
    /// informação que o SQL Server disponibiliza): diferente do deadlock
    /// graph, aqui não existe um &lt;resource-list&gt; cruzando quem tem
    /// cada lock. O processo BLOQUEADO informa, nos próprios atributos
    /// (waitresource/lockMode), o recurso que ele está esperando — isso vai
    /// para "Bloqueio Solicitado". Já o processo BLOQUEADOR não tem, neste
    /// relatório, nenhuma indicação de qual lock especificamente ele está
    /// segurando (só que é ele quem está causando o bloqueio) — por isso os
    /// dois campos de bloqueio dele ficam vazios (a tela mostra "-").
    /// </summary>
    private DeadlockEventoDto? ParsearBlockedProcessReport(XElement bprEl, DateTime? timestamp)
    {
        var processoBloqueadoEl = bprEl.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("blocked-process", StringComparison.OrdinalIgnoreCase))
            ?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("process", StringComparison.OrdinalIgnoreCase));
        var processoBloqueadorEl = bprEl.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("blocking-process", StringComparison.OrdinalIgnoreCase))
            ?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("process", StringComparison.OrdinalIgnoreCase));

        if (processoBloqueadoEl is null && processoBloqueadorEl is null)
        {
            return null;
        }

        var processos = new List<DeadlockProcessoDto>();
        if (processoBloqueadoEl is not null)
        {
            processos.Add(MontarProcessoBloqueio(processoBloqueadoEl, ehBloqueado: true));
        }
        if (processoBloqueadorEl is not null)
        {
            processos.Add(MontarProcessoBloqueio(processoBloqueadorEl, ehBloqueado: false));
        }

        return new DeadlockEventoDto
        {
            Timestamp = timestamp,
            Processos = processos,
            XmlBruto = bprEl.ToString(),
            TipoEvento = "BlockedProcess",
        };
    }

    /// <summary>
    /// Monta o <see cref="DeadlockProcessoDto"/> de um dos dois lados de um
    /// blocked process report. Reaproveita <see cref="DeadlockProcessoDto.EhVitima"/>
    /// com o sentido de "processo prejudicado" (aqui, o que ficou esperando
    /// — não há cancelamento neste tipo de evento) para que a mesma coluna
    /// da grade sirva tanto para deadlock quanto para blocked process; a
    /// tela decide o texto do cabeçalho ("Bloqueado"/"Bloqueador" vs.
    /// "Vitimado"/"Vencedor") a partir de <see cref="DeadlockEventoDto.TipoEvento"/>.
    ///
    /// Diferente do deadlock graph, o &lt;frame&gt; do T-SQL aqui fica
    /// dentro de &lt;executionStack&gt; — junto do processo também existe um
    /// &lt;stackFrames&gt; com frames de código NATIVO (ntdll/sqlmin/...),
    /// sem relação com o comando SQL, que não pode ser confundido com o
    /// frame certo (por isso a busca é restrita a "dentro de
    /// executionStack", não "qualquer frame descendente" como no deadlock).
    /// </summary>
    private static DeadlockProcessoDto MontarProcessoBloqueio(XElement processoEl, bool ehBloqueado)
    {
        var frameEl = processoEl.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("executionStack", StringComparison.OrdinalIgnoreCase))
            ?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("frame", StringComparison.OrdinalIgnoreCase));
        var inputBufEl = processoEl.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("inputbuf", StringComparison.OrdinalIgnoreCase));

        var textoComando = !string.IsNullOrWhiteSpace(frameEl?.Value)
            ? frameEl!.Value
            : (inputBufEl?.Value ?? string.Empty);

        var procname = frameEl?.Attribute("procname")?.Value ?? string.Empty;

        var dto = new DeadlockProcessoDto
        {
            ProcessId = processoEl.Attribute("id")?.Value ?? string.Empty,
            Spid = processoEl.Attribute("spid")?.Value ?? string.Empty,
            EhVitima = ehBloqueado,
            OperacaoPrimaria = DescreverOperacao(textoComando),
            GatilhoOuCodigo = DescreverOrigemCodigo(procname),
            HostName = processoEl.Attribute("hostname")?.Value ?? string.Empty,
            LoginName = processoEl.Attribute("loginname")?.Value ?? string.Empty,
            ClientApp = processoEl.Attribute("clientapp")?.Value ?? string.Empty,
        };

        if (ehBloqueado)
        {
            var waitResource = processoEl.Attribute("waitresource")?.Value;
            if (!string.IsNullOrWhiteSpace(waitResource))
            {
                var lockMode = processoEl.Attribute("lockMode")?.Value;
                dto.BloqueioSolicitadoDescricao = string.IsNullOrWhiteSpace(lockMode)
                    ? waitResource
                    : $"{ObterDescricaoModo(lockMode)} em {waitResource}";
            }
        }

        return dto;
    }

    /// <summary>
    /// Melhor esforço: identifica o verbo DML no início do comando e, com
    /// uma expressão regular específica do verbo, a tabela envolvida. Não é
    /// um parser de T-SQL de verdade — serve só para dar um resumo curto
    /// como "SELECT de Pedidos" / "INSERT na VD_PEDIDOSSTATUS". Quando o
    /// comando não casa com nenhum padrão reconhecido, cai de volta para a
    /// primeira linha do texto bruto (truncada).
    ///
    /// Antes de procurar o verbo, remove comentários <c>/* ... */</c> —
    /// comuns em <c>inputbuf</c> de aplicações que anotam a origem da
    /// chamada logo no início (ex.: "/* TIPO_SOLICITANTE: ... */") — e uma
    /// eventual declaração de parâmetros entre parênteses no começo do
    /// texto (ex.: "(@p0 int, @p1 datetime)", de chamadas parametrizadas
    /// tipo <c>sp_executesql</c>). Sem isso, o comando de verdade nunca
    /// ficaria no início do texto e o verbo nunca seria reconhecido.
    /// </summary>
    private static string DescreverOperacao(string sqlTexto)
    {
        if (string.IsNullOrWhiteSpace(sqlTexto))
            return "(comando não disponível)";

        var textoLimpo = Regex.Replace(sqlTexto, @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();
        // A declaração de parâmetros pode ter parênteses aninhados (ex.:
        // "(@p0 varchar(1), @p1 int)") — por isso o grupo permite até 1
        // nível de aninhamento em vez de simplesmente "sem parênteses".
        textoLimpo = Regex.Replace(textoLimpo, @"^\((?:[^()]|\([^()]*\))*\)\s*", string.Empty).TrimStart();

        var mapaVerbos = new (string Verbo, string Rotulo, string Padrao)[]
        {
            ("SELECT", "SELECT de {0}", @"FROM\s+([^\s(),;]+)"),
            ("INSERT", "INSERT na {0}", @"INTO\s+([^\s(),;]+)"),
            ("UPDATE", "UPDATE na {0}", @"UPDATE\s+(?:TOP\s*\([^)]*\)\s+)?([^\s(),;]+)"),
            ("DELETE", "DELETE na {0}", @"FROM\s+([^\s(),;]+)"),
            ("MERGE", "MERGE na {0}", @"MERGE\s+(?:INTO\s+)?([^\s(),;]+)"),
        };

        foreach (var (verbo, rotulo, padrao) in mapaVerbos)
        {
            if (Regex.IsMatch(textoLimpo, $@"^\s*{verbo}\b", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(textoLimpo, padrao, RegexOptions.IgnoreCase);
                var tabela = match.Success ? SimplificarNomeObjeto(match.Groups[1].Value) : "(tabela não identificada)";
                return string.Format(rotulo, tabela);
            }
        }

        var primeiraLinha = textoLimpo.Split('\n')[0].Trim();
        return primeiraLinha.Length > 80 ? primeiraLinha[..80] + "..." : primeiraLinha;
    }

    /// <summary>
    /// Distingue "Ad-hoc query (Consulta direta)" de "Trigger x"/"Procedure
    /// x" a partir do atributo procname do frame. A distinção entre Trigger
    /// e Procedure é uma HEURÍSTICA baseada em convenção de nome (contém
    /// "TR_") — o deadlock graph não indica o tipo real do objeto; para
    /// saber com certeza seria preciso consultar sys.objects no banco onde
    /// o deadlock ocorreu, o que esta análise não faz (ela é 100% offline,
    /// a partir do arquivo enviado pelo usuário).
    /// </summary>
    private static string DescreverOrigemCodigo(string procname)
    {
        if (string.IsNullOrWhiteSpace(procname) || procname.Equals("adhoc", StringComparison.OrdinalIgnoreCase))
            return "Ad-hoc query (Consulta direta)";

        var nomeSimplificado = SimplificarNomeObjeto(procname);

        return nomeSimplificado.Contains("TR_", StringComparison.OrdinalIgnoreCase)
            ? $"Trigger {nomeSimplificado}"
            : $"Procedure {nomeSimplificado}";
    }

    /// <summary>
    /// Formata um nó de recurso (keylock/objectlock/pagelock/ridlock/...)
    /// como "Lock de Leitura (S) na VD_PEDIDOS". Retorna null para tipos de
    /// recurso sem um "objectname" reconhecível (ex.: latches internas),
    /// que não fazem sentido mostrar ao usuário.
    /// </summary>
    private static string? DescreverRecurso(XElement recursoEl)
    {
        var objectName = recursoEl.Attribute("objectname")?.Value;
        if (string.IsNullOrWhiteSpace(objectName))
            return null;

        var modo = recursoEl.Attribute("mode")?.Value ?? string.Empty;
        var descricaoModo = ObterDescricaoModo(modo);
        var nomeSimplificado = SimplificarNomeObjeto(objectName);

        return $"{descricaoModo} na {nomeSimplificado}";
    }

    /// <summary>
    /// Remove colchetes e prefixo de esquema/banco de um nome de objeto do
    /// SQL Server (ex.: "[CloudADM].[dbo].[VD_PEDIDOS]" → "VD_PEDIDOS"),
    /// deixando só o último segmento — o que o usuário reconhece como "a
    /// tabela".
    /// </summary>
    private static string SimplificarNomeObjeto(string nomeObjeto)
    {
        if (string.IsNullOrWhiteSpace(nomeObjeto))
            return nomeObjeto;

        var semColchetes = nomeObjeto.Replace("[", string.Empty).Replace("]", string.Empty);
        var partes = semColchetes.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length > 0 ? partes[^1] : semColchetes;
    }

    private static string ObterDescricaoModo(string modo)
    {
        if (string.IsNullOrWhiteSpace(modo))
            return "Lock";

        return DescricoesModoLock.TryGetValue(modo, out var descricao)
            ? descricao
            : $"Lock ({modo})";
    }
    /// <summary>
    /// Tamanho máximo (em bytes) de um arquivo aceito para análise offline.
    ///
    /// A análise carrega o arquivo INTEIRO em memória e ainda monta uma árvore
    /// XML por cima (que ocupa várias vezes o tamanho do texto), então um
    /// arquivo muito grande derruba o processo por falta de memória — sem
    /// mensagem nenhuma, no meio da análise. Melhor recusar na entrada,
    /// explicando o motivo e o que fazer, do que morrer no meio.
    /// </summary>
    private const long TamanhoMaximoArquivoBytes = 300L * 1024 * 1024;

    private static void GarantirTamanhoAnalisavel(string caminhoArquivo, string tipoArquivo)
    {
        var info = new FileInfo(caminhoArquivo);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Arquivo não encontrado: {caminhoArquivo}", caminhoArquivo);
        }

        if (info.Length > TamanhoMaximoArquivoBytes)
        {
            var tamanhoMb = info.Length / 1024d / 1024d;
            var limiteMb = TamanhoMaximoArquivoBytes / 1024d / 1024d;
            throw new InvalidOperationException(
                $"O arquivo {tipoArquivo} tem {tamanhoMb:N0} MB, acima do limite de {limiteMb:N0} MB que a análise " +
                "offline consegue carregar em memória com segurança. Gere um arquivo menor (recorte o período de " +
                "interesse na consulta que exportou o XML, ou use uma sessão de Extended Events com arquivos " +
                "menores/rotativos) e tente de novo.");
        }
    }

}
