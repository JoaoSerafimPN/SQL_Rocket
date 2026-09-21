using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Regras de manutenção de índices. Atende ao submenu "Manutenção > Índice":
/// Fragmentação, Índices nunca Utilizados, Índices sugeridos, Índices mais
/// utilizados.
///
/// IMPORTANTE: os métodos de <b>Fragmentação</b> (consulta + rebuild/reorganize
/// individual e em lote + atualização de estatísticas), <b>Índices nunca
/// Utilizados</b> (consulta + geração de script de recriação + exclusão),
/// <b>Índices sugeridos</b> (consulta + geração/execução do script de
/// criação) e <b>Índices mais utilizados</b> (consulta, reaproveitando o
/// rebuild/reorganize individual já existentes) já estão implementados.
/// </summary>
public class Modulo_Indices
{
    private readonly Conectar_SQL _conexao;

    public Modulo_Indices(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>
    /// Retorna o nível de fragmentação dos índices, usando o script fornecido
    /// pelo usuário (sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID(...),
    /// NULL, NULL, NULL) JOIN sys.indexes, ORDER BY avg_fragmentation_in_percent
    /// DESC) com três acréscimos pontuais:
    /// 1) <paramref name="nomeTabela"/> agora é OPCIONAL — quando informado,
    ///    filtra só aquela tabela (comportamento idêntico ao script
    ///    original); quando nulo/vazio, passa NULL pro parâmetro object_id da
    ///    função, que é o próprio jeito documentado do SQL Server de dizer
    ///    "todos os objetos do banco" — ou seja, carrega os índices de TODAS
    ///    as tabelas. Isso pode ser mais lento em bancos grandes, já que a
    ///    função varre todos os objetos em vez de um só.
    /// 2) <see cref="IndiceFragmentacaoDto.NomeTabela"/> (JOIN extra com
    ///    sys.objects) — necessário para identificar de qual tabela é cada
    ///    índice quando o carregamento é do banco inteiro, e também usado
    ///    para montar os comandos ALTER INDEX de cada linha individualmente;
    /// 3) <see cref="IndiceFragmentacaoDto.EhChavePrimaria"/> (IND.is_primary_key)
    ///    — só para o filtro "PK Indexes" da tela.
    /// Também mantido: "WHERE IND.name IS NOT NULL" — exclui o HEAP
    /// (index_id = 0, sem nome), que não é um índice de verdade (não dá pra
    /// rodar ALTER INDEX nele).
    /// Quando uma tabela específica é informada, confirma antes que ela
    /// existe (OBJECT_ID(@tabela) IS NOT NULL) — evita cair sem querer no
    /// caso "todas as tabelas" por causa de um nome digitado errado.
    /// </summary>
    /// <param name="nomeTabela">
    /// Nome (ou schema.nome) da tabela. Nulo/vazio = carrega os índices de
    /// todas as tabelas do banco.
    /// </param>
    /// <param name="nomeBanco">
    /// Banco de dados onde a tabela está — quando informado, a conexão troca
    /// para esse banco (<see cref="SqlConnection.ChangeDatabase"/>, equivalente
    /// a um "USE [banco]") antes de consultar. Quando nulo/vazio, usa o banco
    /// padrão da conexão configurada no login.
    /// </param>
    public async Task<List<IndiceFragmentacaoDto>> ObterFragmentacaoIndicesAsync(string? nomeTabela = null, string? nomeBanco = null, CancellationToken ct = default)
    {
        var tabelaInformada = !string.IsNullOrWhiteSpace(nomeTabela);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        int? objectId = null;
        if (tabelaInformada)
        {
            await using var comandoExiste = conexao.CreateCommand();
            comandoExiste.CommandText = "SELECT OBJECT_ID(@tabela);";
            comandoExiste.Parameters.AddWithValue("@tabela", nomeTabela);
            var resultadoExiste = await comandoExiste.ExecuteScalarAsync(ct);
            if (resultadoExiste is null || resultadoExiste is DBNull)
            {
                throw new InvalidOperationException($"Tabela \"{nomeTabela}\" não encontrada no banco de dados atual.");
            }

            objectId = (int)resultadoExiste;
        }

        await using SqlCommand comando = conexao.CreateCommand();
        // Leitura pesada: sys.dm_db_index_physical_stats / _operational_stats
        // varrem todos os objetos do banco e levam minutos em base de produção.
        // Sem isto valia o padrão de 30s do ADO.NET e a tela dava "Timeout
        // expired" justamente nos bancos para os quais ela existe (os caminhos
        // de ESCRITA deste módulo já usavam CommandTimeout = 0).
        comando.CommandTimeout = 0;
        comando.CommandText =
            "SELECT " +
            "    FRAG.index_id AS IndiceId, " +
            "    OBJ.name AS NomeTabela, " +
            "    IND.name AS NomeIndice, " +
            "    FRAG.avg_fragmentation_in_percent AS PercentualFragmentacao, " +
            "    IND.is_primary_key AS EhChavePrimaria " +
            "FROM sys.dm_db_index_physical_stats(DB_ID(), @objectId, NULL, NULL, NULL) AS FRAG " +
            "JOIN sys.indexes AS IND ON FRAG.object_id = IND.object_id AND FRAG.index_id = IND.index_id " +
            "JOIN sys.objects AS OBJ ON FRAG.object_id = OBJ.object_id " +
            "WHERE IND.name IS NOT NULL " +
            "ORDER BY FRAG.avg_fragmentation_in_percent DESC;";
        comando.Parameters.AddWithValue("@objectId", (object?)objectId ?? DBNull.Value);

        var resultado = new List<IndiceFragmentacaoDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new IndiceFragmentacaoDto
            {
                IndiceId = leitor.GetInt32(leitor.GetOrdinal("IndiceId")),
                NomeTabela = leitor.GetString(leitor.GetOrdinal("NomeTabela")),
                NomeIndice = leitor.GetString(leitor.GetOrdinal("NomeIndice")),
                PercentualFragmentacao = leitor.GetDouble(leitor.GetOrdinal("PercentualFragmentacao")),
                EhChavePrimaria = leitor.GetBoolean(leitor.GetOrdinal("EhChavePrimaria"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Reconstrói (REBUILD) um único índice — mesmo comando/opções WITH da
    /// stored procedure de referência fornecida pelo usuário
    /// (PROC_RECRIAORGANIZA_INDICE), só que executado diretamente pelo app
    /// via ADO.NET, sem precisar que a procedure exista no servidor.
    /// </summary>
    public async Task ReconstruirIndiceAsync(string nomeTabela, string nomeIndice, string? nomeBanco = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        var script =
            $"ALTER INDEX [{IdentificadorSql.EscaparColchetes(nomeIndice)}] ON {IdentificadorSql.Qualificar(nomeTabela)} " +
            "REBUILD PARTITION = ALL WITH ( PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, " +
            "ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, ONLINE = OFF, SORT_IN_TEMPDB = OFF );";

        mensagens?.Report($"Reconstruindo (REBUILD) o índice \"{nomeIndice}\"...");
        await ExecutarNonQueryAsync(script, nomeBanco, ct);
        mensagens?.Report($"Índice \"{nomeIndice}\" reconstruído com sucesso.");
    }

    /// <summary>
    /// Reorganiza (REORGANIZE) um único índice — mesmo comando/opções WITH
    /// da stored procedure de referência.
    /// </summary>
    public async Task ReorganizarIndiceAsync(string nomeTabela, string nomeIndice, string? nomeBanco = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        var script =
            $"ALTER INDEX [{IdentificadorSql.EscaparColchetes(nomeIndice)}] ON {IdentificadorSql.Qualificar(nomeTabela)} " +
            "REORGANIZE WITH ( LOB_COMPACTION = ON );";

        mensagens?.Report($"Reorganizando o índice \"{nomeIndice}\"...");
        await ExecutarNonQueryAsync(script, nomeBanco, ct);
        mensagens?.Report($"Índice \"{nomeIndice}\" reorganizado com sucesso.");
    }

    /// <summary>
    /// Reflete a mesma lógica de limiar da procedure de referência: índices
    /// com fragmentação MAIOR que 30% são reconstruídos. Diferente da
    /// procedure original (que decide REBUILD ou REORGANIZE numa única
    /// passada por índice), aqui a decisão é dividida em duas ações
    /// separadas para dar controle explícito ao usuário na tela — este
    /// método só cuida da parte de REBUILD; ver <see cref="ReorganizarTodosNoLimiarAsync"/>
    /// para a parte de REORGANIZE. Os limiares (30% / 5%) são exatamente os
    /// mesmos do script original. Usa <see cref="IndiceFragmentacaoDto.NomeTabela"/>
    /// de CADA linha (não um nome de tabela único) — a lista pode conter
    /// índices de várias tabelas quando carregada sem filtro de tabela.
    /// </summary>
    public async Task ReconstruirTodosAcimaDoLimiarAsync(
        IReadOnlyList<IndiceFragmentacaoDto> indices, string? nomeBanco = null,
        IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        var alvo = indices.Where(i => i.PercentualFragmentacao > 30).ToList();
        if (alvo.Count == 0)
        {
            mensagens?.Report("Nenhum índice com fragmentação acima de 30% nesta lista — nada para reconstruir.");
            return;
        }

        await ProcessarLoteDeIndicesAsync(
            alvo,
            indice => ReconstruirIndiceAsync(indice.NomeTabela, indice.NomeIndice, nomeBanco, mensagens, ct),
            "reconstruir (REBUILD)",
            "Reconstrução em lote",
            mensagens,
            ct);
    }

    /// <summary>
    /// Reflete a mesma lógica de limiar da procedure de referência: índices
    /// com fragmentação MAIOR que 5% e MENOR OU IGUAL a 30% são reorganizados
    /// (abaixo de 5% a própria procedure original não faz nada — não vale a
    /// pena — então esses índices são simplesmente ignorados aqui também).
    /// Usa <see cref="IndiceFragmentacaoDto.NomeTabela"/> de CADA linha, pelo
    /// mesmo motivo de <see cref="ReconstruirTodosAcimaDoLimiarAsync"/>.
    /// </summary>
    public async Task ReorganizarTodosNoLimiarAsync(
        IReadOnlyList<IndiceFragmentacaoDto> indices, string? nomeBanco = null,
        IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        var alvo = indices.Where(i => i.PercentualFragmentacao > 5 && i.PercentualFragmentacao <= 30).ToList();
        if (alvo.Count == 0)
        {
            mensagens?.Report("Nenhum índice com fragmentação entre 5% e 30% nesta lista — nada para reorganizar.");
            return;
        }

        await ProcessarLoteDeIndicesAsync(
            alvo,
            indice => ReorganizarIndiceAsync(indice.NomeTabela, indice.NomeIndice, nomeBanco, mensagens, ct),
            "reorganizar",
            "Reorganização em lote",
            mensagens,
            ct);
    }

    /// <summary>
    /// Percorre a lista de índices de um lote (REBUILD ou REORGANIZE)
    /// aplicando <paramref name="acao"/> a cada um, SEM deixar que a falha de
    /// um índice interrompa os demais.
    ///
    /// Defeito que isto corrige: o laço anterior era um <c>foreach</c> solto.
    /// Bastava UM índice excluído por outra sessão entre o "Carregar Índices"
    /// e o clique no botão (ou um filegroup offline, ou uma permissão
    /// faltando) para a exceção subir e os outros 200 índices fragmentados
    /// serem silenciosamente pulados. E como os índices já processados
    /// continuavam reconstruídos, a tela mostrava um único erro sem nenhuma
    /// pista de até onde a operação tinha chegado — o usuário não tinha como
    /// saber o que rodar de novo.
    /// </summary>
    /// <remarks>
    /// Sobre lançar ou não no final (decisão deliberada):
    /// - se TODOS falharam, a operação não fez absolutamente nada e isso
    ///   precisa chegar à tela como ERRO (a tela só destaca falha quando há
    ///   exceção) — então lança;
    /// - se apenas ALGUNS falharam, o trabalho feito é real e não deve ser
    ///   apresentado como operação malsucedida. O resumo e a lista dos índices
    ///   que falharam vão pelo <paramref name="mensagens"/>, que é o mesmo
    ///   canal que a tela já grava em log, e o método retorna normalmente.
    /// </remarks>
    private static async Task ProcessarLoteDeIndicesAsync(
        IReadOnlyList<IndiceFragmentacaoDto> alvo,
        Func<IndiceFragmentacaoDto, Task> acao,
        string verboAcao,
        string tituloResumo,
        IProgress<string>? mensagens,
        CancellationToken ct)
    {
        var sucessos = 0;
        var falhas = new List<string>();

        foreach (var indice in alvo)
        {
            ct.ThrowIfCancellationRequested();

            var identificacao = $"[{indice.NomeTabela}].[{indice.NomeIndice}]";

            try
            {
                await acao(indice);
                sucessos++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelamento PEDIDO PELO USUÁRIO não é falha de um índice:
                // tem que parar o lote inteiro. O filtro "when" é o que separa
                // esse caso de um OperationCanceledException vindo de outro
                // token (ex.: timeout interno do driver), que é tratado como
                // falha daquele índice pelo catch abaixo e não impede os
                // demais.
                throw;
            }
            catch (Exception ex)
            {
                falhas.Add($"{identificacao} ({ex.Message})");
                mensagens?.Report(
                    $"FALHA ao {verboAcao} {identificacao}: {ex.Message}. " +
                    "Continuando com os demais índices do lote...");
            }
        }

        var resumo =
            $"{tituloResumo}: {sucessos} de {alvo.Count} índice(s) concluído(s) com sucesso, " +
            $"{falhas.Count} com falha.";

        if (falhas.Count == 0)
        {
            mensagens?.Report(resumo);
            return;
        }

        mensagens?.Report($"{resumo} Índices que falharam: {string.Join(" | ", falhas)}");

        if (sucessos == 0)
        {
            throw new InvalidOperationException(
                $"{tituloResumo}: nenhum dos {alvo.Count} índice(s) pôde ser processado. " +
                $"Falhas: {string.Join(" | ", falhas)}");
        }
    }

    /// <summary>
    /// Atualiza estatísticas com WITH FULLSCAN — mesma ideia do script
    /// fornecido pelo usuário (cursor percorrendo sys.tables/sys.schemas e
    /// executando UPDATE STATISTICS ... WITH FULLSCAN em cada tabela).
    /// <paramref name="nomeTabela"/> agora é OPCIONAL, igual ao Carregar
    /// Índices/Rebuild/Reorganize: informado, atualiza só aquela tabela;
    /// nulo/vazio, atualiza TODAS as tabelas do banco (schema.tabela
    /// resolvido via sys.tables/sys.schemas, sem depender de cursor T-SQL —
    /// o laço é feito aqui em C#, um UPDATE STATISTICS por tabela, o que
    /// também permite reportar progresso tabela a tabela e permite cancelar
    /// no meio). FULLSCAN pode ser bem mais lento que a amostragem padrão em
    /// tabelas grandes, especialmente rodando para o banco inteiro.
    /// </summary>
    /// <param name="nomeTabela">
    /// Nome (ou schema.nome) da tabela. Nulo/vazio = atualiza as estatísticas
    /// de todas as tabelas do banco.
    /// </param>
    public async Task AtualizarEstatisticasAsync(string? nomeTabela, string? nomeBanco = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(nomeTabela))
        {
            mensagens?.Report($"Atualizando estatísticas (FULLSCAN) da tabela \"{nomeTabela}\"...");
            var script = $"UPDATE STATISTICS {IdentificadorSql.Qualificar(nomeTabela)} WITH FULLSCAN;";
            await ExecutarNonQueryAsync(script, nomeBanco, ct);
            mensagens?.Report("Estatísticas atualizadas com sucesso.");
            return;
        }

        var tabelas = await ObterTodasAsTabelasAsync(nomeBanco, ct);
        if (tabelas.Count == 0)
        {
            mensagens?.Report("Nenhuma tabela encontrada no banco de dados.");
            return;
        }

        mensagens?.Report($"Atualizando estatísticas (FULLSCAN) de {tabelas.Count} tabela(s)...");
        var atual = 0;
        foreach (var tabelaQualificada in tabelas)
        {
            ct.ThrowIfCancellationRequested();
            atual++;
            mensagens?.Report($"({atual}/{tabelas.Count}) Atualizando estatísticas de {tabelaQualificada}...");
            var script = $"UPDATE STATISTICS {tabelaQualificada} WITH FULLSCAN;";
            await ExecutarNonQueryAsync(script, nomeBanco, ct);
        }

        mensagens?.Report($"Estatísticas atualizadas com sucesso em {tabelas.Count} tabela(s).");
    }

    /// <summary>
    /// Lista os nomes (sem schema, igual a <see cref="IndiceFragmentacaoDto.NomeTabela"/>)
    /// de todas as tabelas do banco — usado para popular o combo "Nome da
    /// Tabela" da tela de Fragmentação, deixando o usuário escolher a tabela
    /// numa lista em vez de digitar de cabeça.
    /// </summary>
    public async Task<List<string>> ObterNomesTabelasAsync(string? nomeBanco = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        // Nome QUALIFICADO (schema.tabela): sem o schema, um banco com
        // dbo.Movimento e auditoria.Movimento mostrava "Movimento" duas vezes
        // no combo, sem como distinguir, e o comando montado como [Movimento]
        // era resolvido no schema padrão — a operação (inclusive a exclusão em
        // lote da tela de Limpeza) caía na tabela errada. Ver IdentificadorSql.
        comando.CommandText =
            "SELECT s.name + '.' + t.name " +
            "FROM sys.tables AS t " +
            "JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "ORDER BY s.name, t.name;";

        var resultado = new List<string>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(leitor.GetString(0));
        }

        return resultado;
    }

    /// <summary>
    /// Lista os nomes dos índices de uma tabela específica — usado para
    /// popular o combo "Nome do Índice" da tela de Fragmentação assim que o
    /// usuário escolhe uma tabela em "Nome da Tabela", em vez de exigir que
    /// ele já saiba o nome exato do índice. Exclui o HEAP (índice sem nome),
    /// igual a <see cref="ObterFragmentacaoIndicesAsync"/>.
    /// </summary>
    /// <param name="nomeTabela">Nome da tabela (sem schema). Obrigatório — vazio retorna lista vazia sem consultar.</param>
    public async Task<List<string>> ObterNomesIndicesAsync(string nomeTabela, string? nomeBanco = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomeTabela))
        {
            return new List<string>();
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT IND.name " +
            "FROM sys.indexes AS IND " +
            "JOIN sys.objects AS OBJ ON IND.object_id = OBJ.object_id " +
            // OBJECT_ID(@tabela) em vez de comparar por OBJ.name/TB.name: resolve
            // corretamente tanto "Tabela" quanto "schema.Tabela" e elimina a
            // AMBIGUIDADE de nome — antes, com dbo.Log e auditoria.Log tendo
            // ambos um IX_Data, a consulta casava as duas e o código usava a
            // primeira linha que voltasse (podendo mexer no índice errado).
            "WHERE IND.object_id = OBJECT_ID(@tabela) AND IND.name IS NOT NULL " +
            "ORDER BY IND.name;";
        comando.Parameters.AddWithValue("@tabela", nomeTabela);

        var resultado = new List<string>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(leitor.GetString(0));
        }

        return resultado;
    }

    /// <summary>
    /// Lista todas as tabelas do banco já qualificadas e entre colchetes
    /// (QUOTENAME(schema) + '.' + QUOTENAME(tabela)) — mesma consulta do
    /// script do usuário (sys.tables JOIN sys.schemas), usada para percorrer
    /// "todas as tabelas do banco" em <see cref="AtualizarEstatisticasAsync"/>.
    /// </summary>
    private async Task<List<string>> ObterTodasAsTabelasAsync(string? nomeBanco, CancellationToken ct)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name) " +
            "FROM sys.tables AS t " +
            "JOIN sys.schemas AS s ON t.schema_id = s.schema_id " +
            "ORDER BY s.name, t.name;";

        var resultado = new List<string>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(leitor.GetString(0));
        }

        return resultado;
    }

    private async Task ExecutarNonQueryAsync(string script, string? nomeBanco, CancellationToken ct)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = script;
        comando.CommandTimeout = 0; // rebuild/reorganize podem demorar em tabelas grandes; sem timeout de comando
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Troca a conexão já aberta para o banco informado (equivalente a um
    /// "USE [banco]", via <see cref="SqlConnection.ChangeDatabase"/>) — usado
    /// pelo combo "Banco de Dados" da tela de Fragmentação, já que a conexão
    /// abre por padrão no banco definido no login, não necessariamente no
    /// banco da tabela que o usuário quer inspecionar. Não faz nada quando
    /// <paramref name="nomeBanco"/> vem nulo/vazio (mantém o banco padrão).
    /// </summary>
    private static void MudarBancoSeInformado(SqlConnection conexao, string? nomeBanco)
    {
        if (string.IsNullOrWhiteSpace(nomeBanco))
        {
            return;
        }

        try
        {
            conexao.ChangeDatabase(nomeBanco);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Não foi possível acessar o banco de dados \"{nomeBanco}\": {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Retorna os índices sem nenhum uso registrado (seeks/scans/lookups, de
    /// usuário ou do sistema) — script exato fornecido pelo usuário
    /// (sys.dm_db_index_usage_stats + JOIN sys.objects/sys.indexes + JOIN
    /// sys.dm_db_index_operational_stats para os contadores leaf/non-leaf de
    /// insert/delete/update), com dois acréscimos pontuais:
    /// 1) "WHERE IX.name IS NOT NULL" — mesmo motivo de sempre, exclui o
    ///    HEAP (sem nome, não dá pra excluir "índice" nenhum nele);
    /// 2) <see cref="IndiceNaoUtilizadoDto.EhChavePrimaria"/>/<see cref="IndiceNaoUtilizadoDto.EhRestricaoUnica"/>
    ///    — não pedidos no script original, usados só para avisar o usuário
    ///    antes de excluir (chave primária/restrição única não são
    ///    removidas com DROP INDEX, e sim ALTER TABLE ... DROP CONSTRAINT —
    ///    ver <see cref="ExcluirIndiceAsync"/>).
    ///
    /// IMPORTANTE (limitação da própria DMV, documentada aqui para não
    /// pegar ninguém de surpresa): sys.dm_db_index_usage_stats só acumula
    /// desde o último restart do serviço SQL Server — um índice aparecer
    /// aqui significa "sem uso registrado desde o último restart", não
    /// necessariamente "nunca usado desde que foi criado". Vale conferir o
    /// tempo de atividade da instância (sys.dm_os_sys_info.sqlserver_start_time)
    /// antes de excluir algo com base só nesta lista.
    /// </summary>
    public async Task<List<IndiceNaoUtilizadoDto>> ObterIndicesNuncaUtilizadosAsync(string? nomeBanco = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        // Leitura pesada: sys.dm_db_index_physical_stats / _operational_stats
        // varrem todos os objetos do banco e levam minutos em base de produção.
        // Sem isto valia o padrão de 30s do ADO.NET e a tela dava "Timeout
        // expired" justamente nos bancos para os quais ela existe (os caminhos
        // de ESCRITA deste módulo já usavam CommandTimeout = 0).
        comando.CommandTimeout = 0;
        comando.CommandText =
            "SELECT " +
            "    TB.name AS NomeTabela, " +
            "    IX.name AS NomeIndice, " +
            "    IX.type_desc AS TipoIndice, " +
            "    VWX.leaf_insert_count AS LeafInsertCount, " +
            "    VWX.leaf_delete_count AS LeafDeleteCount, " +
            "    VWX.leaf_update_count AS LeafUpdateCount, " +
            "    VWX.nonleaf_insert_count AS NonLeafInsertCount, " +
            "    VWX.nonleaf_delete_count AS NonLeafDeleteCount, " +
            "    VWX.nonleaf_update_count AS NonLeafUpdateCount, " +
            "    IX.is_primary_key AS EhChavePrimaria, " +
            "    IX.is_unique_constraint AS EhRestricaoUnica " +
            "FROM sys.dm_db_index_usage_stats AS VW " +
            "JOIN sys.objects AS TB ON TB.object_id = VW.object_id " +
            "JOIN sys.indexes AS IX ON IX.index_id = VW.index_id AND IX.object_id = TB.object_id " +
            "JOIN sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS VWX " +
            "    ON VWX.object_id = TB.object_id AND VWX.index_id = IX.index_id " +
            "WHERE VW.database_id = DB_ID() AND VW.user_seeks = 0 AND VW.user_scans = 0 " +
            "    AND VW.user_lookups = 0 AND VW.system_seeks = 0 AND VW.system_scans = 0 AND VW.system_lookups = 0 " +
            "    AND IX.name IS NOT NULL " +
            "ORDER BY VWX.leaf_insert_count DESC, TB.name ASC, IX.name ASC;";

        var resultado = new List<IndiceNaoUtilizadoDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new IndiceNaoUtilizadoDto
            {
                NomeTabela = leitor.GetString(leitor.GetOrdinal("NomeTabela")),
                NomeIndice = leitor.GetString(leitor.GetOrdinal("NomeIndice")),
                TipoIndice = leitor.GetString(leitor.GetOrdinal("TipoIndice")),
                // leaf_*/nonleaf_*_count são bigint em sys.dm_db_index_operational_stats — GetInt64, não GetInt32.
                LeafInsertCount = leitor.GetInt64(leitor.GetOrdinal("LeafInsertCount")),
                LeafDeleteCount = leitor.GetInt64(leitor.GetOrdinal("LeafDeleteCount")),
                LeafUpdateCount = leitor.GetInt64(leitor.GetOrdinal("LeafUpdateCount")),
                NonLeafInsertCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafInsertCount")),
                NonLeafDeleteCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafDeleteCount")),
                NonLeafUpdateCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafUpdateCount")),
                EhChavePrimaria = leitor.GetBoolean(leitor.GetOrdinal("EhChavePrimaria")),
                EhRestricaoUnica = leitor.GetBoolean(leitor.GetOrdinal("EhRestricaoUnica"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Tipos de índice para os quais sabemos montar um CREATE INDEX/ALTER
    /// TABLE ADD CONSTRAINT de recriação confiável (rowstore comum). Tipos
    /// especiais (COLUNSTORE, XML, SPATIAL, índices de tabela memory-optimized
    /// com HASH etc.) têm sintaxe de criação bem diferente — gerar um script
    /// "genérico" errado para eles seria pior do que não gerar nenhum, já
    /// que o usuário pediu justamente o script como garantia de segurança
    /// antes de excluir. Nesses casos, <see cref="GerarScriptRecriacaoIndiceAsync"/>
    /// recusa e orienta a gerar o script manualmente pelo SSMS.
    /// </summary>
    private static readonly HashSet<string> TiposIndiceComScriptSuportado =
        new(StringComparer.OrdinalIgnoreCase) { "CLUSTERED", "NONCLUSTERED" };

    /// <summary>
    /// Monta um script T-SQL para recriar o índice informado, a partir dos
    /// metadados atuais dele (sys.indexes/sys.index_columns/sys.columns) —
    /// pedido explicitamente pelo usuário como garantia de segurança antes
    /// de excluir um índice sem uso: "salvar um script na máquina local caso
    /// queira criar novamente o índice". Cobre índice comum (CREATE [UNIQUE]
    /// [NON]CLUSTERED INDEX, com colunas-chave ASC/DESC, colunas incluídas
    /// via INCLUDE e índice filtrado via WHERE) e também chave
    /// primária/restrição única (ALTER TABLE ADD CONSTRAINT ... PRIMARY
    /// KEY/UNIQUE — que não aceitam INCLUDE/WHERE, então esses dois são
    /// ignorados nesse caso, coerente com a própria sintaxe do SQL Server).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Índice/tabela não encontrado, sem colunas-chave, ou tipo de índice
    /// não suportado pela geração automática (ver <see cref="TiposIndiceComScriptSuportado"/>).
    /// </exception>
    public async Task<string> GerarScriptRecriacaoIndiceAsync(string nomeTabela, string nomeIndice, string? nomeBanco = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        var metadados = await ObterMetadadosIndiceAsync(conexao, nomeTabela, nomeIndice, ct);

        if (!TiposIndiceComScriptSuportado.Contains(metadados.TipoDesc))
        {
            throw new InvalidOperationException(
                $"O índice \"{nomeIndice}\" é do tipo \"{metadados.TipoDesc}\", que não é suportado pela geração " +
                "automática de script de recriação (só ROWSTORE CLUSTERED/NONCLUSTERED). Para não gerar um script " +
                "incorreto, a exclusão foi bloqueada aqui — gere o script manualmente pelo SSMS antes de excluir.");
        }

        var (colunasChave, colunasIncluidas) = await ObterColunasIndiceAsync(conexao, nomeTabela, nomeIndice, ct);
        if (colunasChave.Count == 0)
        {
            throw new InvalidOperationException($"Não foi possível determinar as colunas-chave do índice \"{nomeIndice}\".");
        }

        var colunasChaveTexto = string.Join(", ",
            colunasChave.Select(c => $"[{IdentificadorSql.EscaparColchetes(c.Nome)}] {(c.Descendente ? "DESC" : "ASC")}"));

        var cabecalho =
            "-- Script de recriação gerado automaticamente pelo SQL Rocket\n" +
            $"-- Tabela: [{metadados.Schema}].[{nomeTabela}]    Índice: [{nomeIndice}]\n" +
            $"-- Gerado em: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\n" +
            "-- Guarde este arquivo ANTES de excluir o índice — é a forma de recriá-lo se precisar reverter.\n\n";

        string comando;
        if (metadados.EhChavePrimaria || metadados.EhRestricaoUnica)
        {
            var tipoRestricao = metadados.EhChavePrimaria ? "PRIMARY KEY" : "UNIQUE";
            comando =
                $"ALTER TABLE [{IdentificadorSql.EscaparColchetes(metadados.Schema)}].[{IdentificadorSql.EscaparColchetes(nomeTabela)}] " +
                $"ADD CONSTRAINT [{IdentificadorSql.EscaparColchetes(nomeIndice)}] {tipoRestricao} {metadados.TipoDesc} ({colunasChaveTexto});";
        }
        else
        {
            var unico = metadados.EhUnico ? "UNIQUE " : "";
            comando =
                $"CREATE {unico}{metadados.TipoDesc} INDEX [{IdentificadorSql.EscaparColchetes(nomeIndice)}] " +
                $"ON [{IdentificadorSql.EscaparColchetes(metadados.Schema)}].[{IdentificadorSql.EscaparColchetes(nomeTabela)}] ({colunasChaveTexto})";

            if (colunasIncluidas.Count > 0)
            {
                var incluidasTexto = string.Join(", ", colunasIncluidas.Select(c => $"[{IdentificadorSql.EscaparColchetes(c)}]"));
                comando += $"\nINCLUDE ({incluidasTexto})";
            }

            if (!string.IsNullOrWhiteSpace(metadados.FiltroDefinicao))
            {
                comando += $"\nWHERE {metadados.FiltroDefinicao}";
            }

            comando += ";";
        }

        return cabecalho + comando + "\n";
    }

    /// <summary>
    /// Exclui o índice informado — DROP INDEX para índice comum, ou ALTER
    /// TABLE ... DROP CONSTRAINT quando é uma chave primária/restrição
    /// única (o SQL Server não aceita DROP INDEX nesses dois casos). NÃO
    /// gera o script de recriação sozinho — isso é responsabilidade de
    /// <see cref="GerarScriptRecriacaoIndiceAsync"/>, chamado e salvo em
    /// disco pela tela ANTES de chamar este método, para garantir que o
    /// usuário sempre tenha como recriar o índice se precisar reverter.
    /// </summary>
    public async Task ExcluirIndiceAsync(string nomeTabela, string nomeIndice, string? nomeBanco = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        var metadados = await ObterMetadadosIndiceAsync(conexao, nomeTabela, nomeIndice, ct);

        string script;
        if (metadados.EhChavePrimaria || metadados.EhRestricaoUnica)
        {
            script =
                $"ALTER TABLE [{IdentificadorSql.EscaparColchetes(metadados.Schema)}].[{IdentificadorSql.EscaparColchetes(nomeTabela)}] " +
                $"DROP CONSTRAINT [{IdentificadorSql.EscaparColchetes(nomeIndice)}];";
            mensagens?.Report($"Excluindo a restrição \"{nomeIndice}\" (ALTER TABLE DROP CONSTRAINT)...");
        }
        else
        {
            script =
                $"DROP INDEX [{IdentificadorSql.EscaparColchetes(nomeIndice)}] ON [{IdentificadorSql.EscaparColchetes(metadados.Schema)}].[{IdentificadorSql.EscaparColchetes(nomeTabela)}];";
            mensagens?.Report($"Excluindo o índice \"{nomeIndice}\" (DROP INDEX)...");
        }

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = script;
        comando.CommandTimeout = 0;
        await comando.ExecuteNonQueryAsync(ct);

        mensagens?.Report($"Índice \"{nomeIndice}\" excluído com sucesso.");
    }

    private sealed record MetadadosIndice(
        string Schema, bool EhUnico, bool EhChavePrimaria, bool EhRestricaoUnica, string TipoDesc, string? FiltroDefinicao);

    private static async Task<MetadadosIndice> ObterMetadadosIndiceAsync(SqlConnection conexao, string nomeTabela, string nomeIndice, CancellationToken ct)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    OBJECT_SCHEMA_NAME(TB.object_id) AS Schema_, " +
            "    IX.is_unique AS EhUnico, " +
            "    IX.is_primary_key AS EhChavePrimaria, " +
            "    IX.is_unique_constraint AS EhRestricaoUnica, " +
            "    IX.type_desc AS TipoDesc, " +
            "    IX.filter_definition AS FiltroDefinicao " +
            "FROM sys.indexes AS IX " +
            "JOIN sys.objects AS TB ON TB.object_id = IX.object_id " +
            // OBJECT_ID(@tabela) em vez de comparar por OBJ.name/TB.name: resolve
            // corretamente tanto "Tabela" quanto "schema.Tabela" e elimina a
            // AMBIGUIDADE de nome — antes, com dbo.Log e auditoria.Log tendo
            // ambos um IX_Data, a consulta casava as duas e o código usava a
            // primeira linha que voltasse (podendo mexer no índice errado).
            "WHERE IX.object_id = OBJECT_ID(@tabela) AND IX.name = @indice;";
        comando.Parameters.AddWithValue("@tabela", nomeTabela);
        comando.Parameters.AddWithValue("@indice", nomeIndice);

        await using var leitor = await comando.ExecuteReaderAsync(ct);
        if (!await leitor.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Índice \"{nomeIndice}\" não encontrado na tabela \"{nomeTabela}\".");
        }

        return new MetadadosIndice(
            leitor.GetString(leitor.GetOrdinal("Schema_")),
            leitor.GetBoolean(leitor.GetOrdinal("EhUnico")),
            leitor.GetBoolean(leitor.GetOrdinal("EhChavePrimaria")),
            leitor.GetBoolean(leitor.GetOrdinal("EhRestricaoUnica")),
            leitor.GetString(leitor.GetOrdinal("TipoDesc")),
            leitor.IsDBNull(leitor.GetOrdinal("FiltroDefinicao")) ? null : leitor.GetString(leitor.GetOrdinal("FiltroDefinicao")));
    }

    private static async Task<(List<(string Nome, bool Descendente)> Chave, List<string> Incluidas)> ObterColunasIndiceAsync(
        SqlConnection conexao, string nomeTabela, string nomeIndice, CancellationToken ct)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    C.name AS NomeColuna, " +
            "    IC.is_descending_key AS Descendente, " +
            "    IC.is_included_column AS Incluida " +
            "FROM sys.index_columns AS IC " +
            "JOIN sys.columns AS C ON C.object_id = IC.object_id AND C.column_id = IC.column_id " +
            "JOIN sys.indexes AS IX ON IX.object_id = IC.object_id AND IX.index_id = IC.index_id " +
            "JOIN sys.objects AS TB ON TB.object_id = IX.object_id " +
            // OBJECT_ID(@tabela) em vez de comparar por OBJ.name/TB.name: resolve
            // corretamente tanto "Tabela" quanto "schema.Tabela" e elimina a
            // AMBIGUIDADE de nome — antes, com dbo.Log e auditoria.Log tendo
            // ambos um IX_Data, a consulta casava as duas e o código usava a
            // primeira linha que voltasse (podendo mexer no índice errado).
            "WHERE IX.object_id = OBJECT_ID(@tabela) AND IX.name = @indice " +
            "ORDER BY IC.is_included_column, IC.key_ordinal, IC.index_column_id;";
        comando.Parameters.AddWithValue("@tabela", nomeTabela);
        comando.Parameters.AddWithValue("@indice", nomeIndice);

        var chave = new List<(string, bool)>();
        var incluidas = new List<string>();

        await using var leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var nome = leitor.GetString(leitor.GetOrdinal("NomeColuna"));
            var incluida = leitor.GetBoolean(leitor.GetOrdinal("Incluida"));
            if (incluida)
            {
                incluidas.Add(nome);
            }
            else
            {
                var descendente = leitor.GetBoolean(leitor.GetOrdinal("Descendente"));
                chave.Add((nome, descendente));
            }
        }

        return (chave, incluidas);
    }

    /// <summary>
    /// Retorna as sugestões de índice apontadas pelo otimizador do SQL
    /// Server — script exato fornecido pelo usuário ("Listagem 2": TOP 15
    /// sys.dm_db_missing_index_group_stats JOIN sys.dm_db_missing_index_groups
    /// JOIN sys.dm_db_missing_index_details, IMPACTO = avg_total_user_cost *
    /// avg_user_impact * (user_seeks + user_scans), ORDER BY IMPACTO DESC),
    /// com dois acréscimos pontuais:
    /// 1) <paramref name="nomeTabela"/> reativa o filtro que estava
    ///    comentado no script original ("--AND mid.object_id = OBJECT_ID('TABELA')
    ///    -- APENAS PARA UMA TABELA ESPECIFICA") — pedido do usuário como
    ///    "uma box caso queira visualizar de uma tabela": informado, filtra
    ///    só as sugestões daquela tabela; nulo/vazio, mantém o comportamento
    ///    original (todas as tabelas do banco);
    /// 2) <see cref="IndiceSugeridoDto.NomeTabela"/> (OBJECT_NAME(mid.object_id))
    ///    e <see cref="IndiceSugeridoDto.ScriptCriacaoSugerido"/> — não
    ///    pedidos no script original; NomeTabela só pra exibir/filtrar de
    ///    forma legível na grade, e ScriptCriacaoSugerido é o CREATE
    ///    NONCLUSTERED INDEX já pronto pra rodar (mesma fórmula que o SSMS
    ///    usa: colunas de igualdade + desigualdade como chave, colunas
    ///    incluídas via INCLUDE), usado pelo botão "Criar Índice" — o que o
    ///    usuário vê na tela é exatamente o que roda ao confirmar.
    /// </summary>
    public async Task<List<IndiceSugeridoDto>> ObterIndicesSugeridosAsync(string? nomeBanco = null, string? nomeTabela = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        var tabelaInformada = !string.IsNullOrWhiteSpace(nomeTabela);

        await using SqlCommand comando = conexao.CreateCommand();
        // Leitura pesada: sys.dm_db_index_physical_stats / _operational_stats
        // varrem todos os objetos do banco e levam minutos em base de produção.
        // Sem isto valia o padrão de 30s do ADO.NET e a tela dava "Timeout
        // expired" justamente nos bancos para os quais ela existe (os caminhos
        // de ESCRITA deste módulo já usavam CommandTimeout = 0).
        comando.CommandTimeout = 0;
        comando.CommandText =
            "SELECT TOP 15 " +
            "    (MIGS.avg_total_user_cost * MIGS.avg_user_impact * (MIGS.user_seeks + MIGS.user_scans)) AS Impacto, " +
            "    MIGS.group_handle AS GroupHandle, " +
            "    MID.index_handle AS IndexHandle, " +
            "    MIGS.user_seeks AS UserSeeks, " +
            "    MIGS.user_scans AS UserScans, " +
            "    OBJECT_NAME(MID.object_id) AS NomeTabela, " +
            "    MID.statement AS Statement, " +
            "    MID.equality_columns AS ColunasIgualdade, " +
            "    MID.inequality_columns AS ColunasDesigualdade, " +
            "    MID.included_columns AS ColunasIncluidas " +
            "FROM sys.dm_db_missing_index_group_stats AS MIGS " +
            "JOIN sys.dm_db_missing_index_groups AS MIG ON MIGS.group_handle = MIG.index_group_handle " +
            "JOIN sys.dm_db_missing_index_details AS MID ON MIG.index_handle = MID.index_handle AND MID.database_id = DB_ID() " +
            (tabelaInformada ? "AND MID.object_id = OBJECT_ID(@tabela) " : "") +
            "ORDER BY Impacto DESC;";

        if (tabelaInformada)
        {
            comando.Parameters.AddWithValue("@tabela", nomeTabela);
        }

        var resultado = new List<IndiceSugeridoDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var statement = leitor.GetString(leitor.GetOrdinal("Statement"));
            var colunasIgualdade = leitor.IsDBNull(leitor.GetOrdinal("ColunasIgualdade")) ? null : leitor.GetString(leitor.GetOrdinal("ColunasIgualdade"));
            var colunasDesigualdade = leitor.IsDBNull(leitor.GetOrdinal("ColunasDesigualdade")) ? null : leitor.GetString(leitor.GetOrdinal("ColunasDesigualdade"));
            var colunasIncluidas = leitor.IsDBNull(leitor.GetOrdinal("ColunasIncluidas")) ? null : leitor.GetString(leitor.GetOrdinal("ColunasIncluidas"));
            var indexHandle = leitor.GetInt32(leitor.GetOrdinal("IndexHandle"));
            var nomeTabelaLinha = leitor.IsDBNull(leitor.GetOrdinal("NomeTabela")) ? "(desconhecida)" : leitor.GetString(leitor.GetOrdinal("NomeTabela"));

            resultado.Add(new IndiceSugeridoDto
            {
                // avg_total_user_cost/avg_user_impact são float — Impacto (produto) fica double.
                Impacto = leitor.GetDouble(leitor.GetOrdinal("Impacto")),
                GroupHandle = leitor.GetInt32(leitor.GetOrdinal("GroupHandle")),
                IndexHandle = indexHandle,
                // user_seeks/user_scans são bigint em sys.dm_db_missing_index_group_stats — GetInt64, não GetInt32.
                UserSeeks = leitor.GetInt64(leitor.GetOrdinal("UserSeeks")),
                UserScans = leitor.GetInt64(leitor.GetOrdinal("UserScans")),
                NomeTabela = nomeTabelaLinha,
                Statement = statement,
                ColunasIgualdade = colunasIgualdade,
                ColunasDesigualdade = colunasDesigualdade,
                ColunasIncluidas = colunasIncluidas,
                ScriptCriacaoSugerido = MontarScriptIndiceSugerido(
                    nomeTabelaLinha, statement, colunasIgualdade, colunasDesigualdade, colunasIncluidas)
            });
        }

        return resultado;
    }

    /// <summary>
    /// Monta o CREATE NONCLUSTERED INDEX sugerido a partir dos metadados de
    /// uma linha de <see cref="ObterIndicesSugeridosAsync"/> — mesma fórmula
    /// clássica que o SSMS usa para sugerir o texto de um índice em falta:
    /// colunas de igualdade seguidas das de desigualdade como chave, e
    /// colunas incluídas via INCLUDE.
    ///
    /// Nome do índice no padrão pedido pelo usuário: "IDX_Tabela_Coluna"
    /// (uma coluna) ou "IDX_Tabela_Coluna1_Coluna2..." (várias colunas de
    /// chave, uma por igualdade/desigualdade) e, havendo colunas incluídas,
    /// só o sufixo "_Include" (sem listar os campos do include no nome,
    /// como pedido — eles já aparecem no corpo do script, no INCLUDE).
    /// </summary>
    private static string MontarScriptIndiceSugerido(
        string nomeTabela, string statement,
        string? colunasIgualdade, string? colunasDesigualdade, string? colunasIncluidas)
    {
        var colunasChave = string.Join(", ",
            new[] { colunasIgualdade, colunasDesigualdade }.Where(c => !string.IsNullOrWhiteSpace(c)));

        var temInclude = !string.IsNullOrWhiteSpace(colunasIncluidas);

        var nomeIndice = $"IDX_{nomeTabela}_{MontarSegmentoNomeColunas(colunasChave)}";
        if (temInclude)
        {
            nomeIndice += "_Include";
        }

        var script =
            $"CREATE NONCLUSTERED INDEX [{IdentificadorSql.EscaparColchetes(nomeIndice)}]\n" +
            $"ON {statement} ({colunasChave})";

        if (temInclude)
        {
            script += $"\nINCLUDE ({colunasIncluidas})";
        }

        return script + ";";
    }

    /// <summary>
    /// Transforma uma lista de colunas entre colchetes vinda das DMVs (ex.:
    /// "[Nome], [DataCriacao]") no segmento "Nome_DataCriacao" usado para
    /// montar o nome do índice sugerido — sem colchetes, vírgulas ou
    /// espaços, só os nomes das colunas separados por "_".
    /// </summary>
    private static string MontarSegmentoNomeColunas(string colunasComColchetes)
    {
        var nomes = colunasComColchetes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Trim('[', ']', ' '));

        return string.Join("_", nomes);
    }

    /// <summary>
    /// Executa o script de criação de um índice sugerido — reaproveita
    /// exatamente o texto gerado em <see cref="ObterIndicesSugeridosAsync"/>
    /// e mostrado ao usuário antes de confirmar (ver tela "Índice >
    /// Índices sugeridos"), então o que aparece na tela é exatamente o que
    /// roda no servidor.
    /// </summary>
    public async Task CriarIndiceSugeridoAsync(string scriptCriacao, string? nomeBanco = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        mensagens?.Report("Criando índice sugerido...");
        await ExecutarNonQueryAsync(scriptCriacao, nomeBanco, ct);
        mensagens?.Report("Índice criado com sucesso.");
    }

    /// <summary>
    /// Retorna o uso de cada índice (seeks/scans/lookups de usuário e de
    /// sistema, mais os contadores leaf/non-leaf de insert/delete/update) —
    /// script exato fornecido pelo usuário (sys.dm_db_index_usage_stats
    /// JOIN sys.objects/sys.indexes + JOIN sys.dm_db_index_operational_stats),
    /// com dois acréscimos pontuais:
    /// 1) "AND IX.name IS NOT NULL" — mesmo motivo de sempre, exclui o HEAP
    ///    (sem nome, Rebuild/Reorganize não se aplicam a ele);
    /// 2) "ORDER BY (user_seeks + user_scans + user_lookups) DESC" — o
    ///    script original não define ordenação, mas como a tela é um
    ///    ranking ("Índices mais utilizados"), faz sentido já vir ordenado
    ///    pelos mais usados primeiro.
    /// Usada junto com <see cref="ReconstruirIndiceAsync"/>/<see cref="ReorganizarIndiceAsync"/>
    /// (mesmos métodos da tela de Fragmentação) para os botões Rebuild/
    /// Reorganize do índice selecionado na grade.
    /// </summary>
    public async Task<List<IndiceUtilizadoDto>> ObterIndicesMaisUtilizadosAsync(string? nomeBanco = null, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        // Leitura pesada: sys.dm_db_index_physical_stats / _operational_stats
        // varrem todos os objetos do banco e levam minutos em base de produção.
        // Sem isto valia o padrão de 30s do ADO.NET e a tela dava "Timeout
        // expired" justamente nos bancos para os quais ela existe (os caminhos
        // de ESCRITA deste módulo já usavam CommandTimeout = 0).
        comando.CommandTimeout = 0;
        comando.CommandText =
            "SELECT " +
            "    TB.name AS NomeTabela, " +
            "    IX.name AS NomeIndice, " +
            "    VW.user_seeks AS UserSeeks, " +
            "    VW.user_scans AS UserScans, " +
            "    VW.user_lookups AS UserLookups, " +
            "    VW.system_seeks AS SystemSeeks, " +
            "    VW.system_scans AS SystemScans, " +
            "    VW.system_lookups AS SystemLookups, " +
            "    VWX.leaf_insert_count AS LeafInsertCount, " +
            "    VWX.leaf_delete_count AS LeafDeleteCount, " +
            "    VWX.leaf_update_count AS LeafUpdateCount, " +
            "    VWX.nonleaf_insert_count AS NonLeafInsertCount, " +
            "    VWX.nonleaf_delete_count AS NonLeafDeleteCount, " +
            "    VWX.nonleaf_update_count AS NonLeafUpdateCount " +
            "FROM sys.dm_db_index_usage_stats AS VW " +
            "JOIN sys.objects AS TB ON TB.object_id = VW.object_id " +
            "JOIN sys.indexes AS IX ON IX.index_id = VW.index_id AND IX.object_id = TB.object_id " +
            "JOIN sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS VWX " +
            "    ON VWX.object_id = TB.object_id AND VWX.index_id = IX.index_id " +
            "WHERE VW.database_id = DB_ID() AND IX.name IS NOT NULL " +
            "ORDER BY (VW.user_seeks + VW.user_scans + VW.user_lookups) DESC;";

        var resultado = new List<IndiceUtilizadoDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new IndiceUtilizadoDto
            {
                NomeTabela = leitor.GetString(leitor.GetOrdinal("NomeTabela")),
                NomeIndice = leitor.GetString(leitor.GetOrdinal("NomeIndice")),
                // Todas as colunas abaixo são bigint (sys.dm_db_index_usage_stats/
                // sys.dm_db_index_operational_stats) — GetInt64, não GetInt32.
                UserSeeks = leitor.GetInt64(leitor.GetOrdinal("UserSeeks")),
                UserScans = leitor.GetInt64(leitor.GetOrdinal("UserScans")),
                UserLookups = leitor.GetInt64(leitor.GetOrdinal("UserLookups")),
                SystemSeeks = leitor.GetInt64(leitor.GetOrdinal("SystemSeeks")),
                SystemScans = leitor.GetInt64(leitor.GetOrdinal("SystemScans")),
                SystemLookups = leitor.GetInt64(leitor.GetOrdinal("SystemLookups")),
                LeafInsertCount = leitor.GetInt64(leitor.GetOrdinal("LeafInsertCount")),
                LeafDeleteCount = leitor.GetInt64(leitor.GetOrdinal("LeafDeleteCount")),
                LeafUpdateCount = leitor.GetInt64(leitor.GetOrdinal("LeafUpdateCount")),
                NonLeafInsertCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafInsertCount")),
                NonLeafDeleteCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafDeleteCount")),
                NonLeafUpdateCount = leitor.GetInt64(leitor.GetOrdinal("NonLeafUpdateCount"))
            });
        }

        return resultado;
    }
}
