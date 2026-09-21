using System.Data;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Regras da tela "Manutenção &gt; Limpeza de Arquivos" (submenu do novo menu
/// "Manutenção", de Query): shrink de arquivo de log (LDF) e do banco de
/// dados inteiro, listagem das 25 maiores tabelas, leitura do primeiro/
/// último registro de uma tabela selecionada e limpeza (DELETE) em lotes por
/// data ou por chave primária.
///
/// Decisões de design (definidas explicitamente pelo usuário antes da
/// implementação, ver AskUserQuestion na rodada que criou este módulo):
/// 1) O "ID" usado em "primeiro/último registro" e na limpeza "por ID" é
///    sempre a CHAVE PRIMÁRIA REAL da tabela, lida dos metadados do SQL
///    Server (<see cref="ObterChavePrimariaAsync"/>) — nunca um nome de
///    coluna adivinhado (tipo "PK_ID"). Só funciona quando a tabela tem
///    exatamente 1 coluna na chave primária; sem chave primária ou com
///    chave composta, a tela desabilita essas duas funcionalidades.
/// 2) A coluna usada na limpeza "por data" é escolhida manualmente pelo
///    usuário, a partir da lista de colunas de data/hora da tabela
///    (<see cref="ObterColunasDataAsync"/>) — nunca adivinhada por nome.
/// 3) Os dois DELETEs (por data e por chave) rodam em LOTES
///    (<see cref="TamanhoLotePadrao"/> linhas por vez), reportando progresso
///    via <see cref="IProgress{T}"/> e observando <see cref="CancellationToken"/>
///    a cada lote — a tela usa a mesma janela de progresso com Cancelar já
///    usada em Índice &gt; Fragmentação (rebuild/reorganize).
/// </summary>
public class Modulo_Manutencao
{
    private readonly Conectar_SQL _conexao;

    /// <summary>
    /// Quantidade de linhas apagadas por vez nos DELETEs em lote
    /// (<see cref="ExcluirRegistrosPorDataAsync"/>/<see cref="ExcluirRegistrosPorChaveAsync"/>).
    ///
    /// 4000, e não os 5000 usados antes, por um motivo bem específico: 5000 é
    /// EXATAMENTE o limiar de escalonamento de lock do SQL Server — ao passar
    /// desse número de locks numa mesma tabela o engine troca os locks de linha
    /// por um lock de TABELA, então o valor antigo tendia a escalar em TODO
    /// lote e a limpeza travava a tabela inteira para o restante da aplicação.
    /// Ficando abaixo do limiar, o DELETE em lote continua com locks de
    /// linha/página. Qualquer ajuste futuro aqui precisa continuar ABAIXO de
    /// 5000 — é o número que importa, não o tamanho do lote em si.
    /// </summary>
    private const int TamanhoLotePadrao = 4000;

    /// <summary>
    /// Tipos de coluna considerados "data/hora" para popular o combo de
    /// escolha manual da coluna usada na limpeza "por data"
    /// (<see cref="ObterColunasDataAsync"/>). Mesma lista usada em
    /// Modulo_Comparador (TiposDataHora) — duplicada aqui de propósito,
    /// seguindo a convenção do projeto de cada módulo não depender de
    /// detalhes internos de outro.
    /// </summary>
    private static readonly string[] TiposDataHora =
    {
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset"
    };

    /// <summary>Tipos de coluna aceitos como chave primária numérica — habilita a limpeza "por ID" (comparação "menor ou igual" numérica).</summary>
    private static readonly string[] TiposNumericosInteiros =
    {
        "tinyint", "smallint", "int", "bigint"
    };

    public Modulo_Manutencao(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>
    /// Reduz o(s) arquivo(s) de log (LDF) do banco selecionado — mesma
    /// técnica do script fornecido pelo usuário (baseado no ambiente
    /// CloudADM): alternar o Recovery Model para SIMPLE antes do
    /// DBCC SHRINKFILE(..., 0, TRUNCATEONLY) e voltar ao modelo original
    /// depois, forma padrão de truncar um log que cresceu demais mesmo com
    /// backups de log pendentes (o TRUNCATEONLY já assume que o espaço
    /// "livre" no fim do arquivo pode ser descartado, sem tentar mover
    /// dados dentro dele).
    ///
    /// Diferença em relação ao script original: em vez de fixar "Full" como
    /// modelo para voltar, o modelo ATUAL do banco é lido antes de mexer
    /// (sys.databases.recovery_model_desc) e restaurado EXATAMENTE a ele no
    /// fim — dentro de um try/finally, então mesmo se o SHRINKFILE falhar
    /// (ou a operação for cancelada) o Recovery Model original é restaurado
    /// antes de sair. Isso faz o método funcionar em qualquer banco (Simple,
    /// Bulk-Logged ou Full), não só em bancos Full como o CloudADM do script
    /// original — e evita o risco de "esquecer" o banco em Simple, que
    /// quebra a cadeia de backups de log até o próximo backup Full.
    /// </summary>
    public async Task ExecutarShrinkLogAsync(string? nomeBanco, IProgress<string>? mensagens, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        var nomeBancoAtual = conexao.Database;
        var bancoEscapado = IdentificadorSql.EscaparColchetes(nomeBancoAtual);

        var arquivosLog = new List<string>();
        await using (var comandoArquivos = conexao.CreateCommand())
        {
            // type = 1 é o código de sys.database_files para arquivo de LOG
            // (0 = ROWS/dados).
            comandoArquivos.CommandText = "SELECT name FROM sys.database_files WHERE type = 1 ORDER BY file_id;";
            await using SqlDataReader leitor = await comandoArquivos.ExecuteReaderAsync(ct);
            while (await leitor.ReadAsync(ct))
            {
                arquivosLog.Add(leitor.GetString(0));
            }
        }

        if (arquivosLog.Count == 0)
        {
            throw new InvalidOperationException("Nenhum arquivo de log (LDF) foi encontrado para este banco de dados.");
        }

        string recoveryModelOriginal;
        await using (var comandoRecovery = conexao.CreateCommand())
        {
            comandoRecovery.CommandText = "SELECT recovery_model_desc FROM sys.databases WHERE database_id = DB_ID();";
            var resultado = await comandoRecovery.ExecuteScalarAsync(ct);
            recoveryModelOriginal = resultado as string ?? "FULL";
        }

        var precisaAlternar = !string.Equals(recoveryModelOriginal, "SIMPLE", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (precisaAlternar)
            {
                mensagens?.Report($"Alterando o Recovery Model de \"{nomeBancoAtual}\" para SIMPLE (temporariamente)...");
                await using var comandoSimple = conexao.CreateCommand();
                comandoSimple.CommandTimeout = 0;
                comandoSimple.CommandText = $"ALTER DATABASE [{bancoEscapado}] SET RECOVERY SIMPLE WITH NO_WAIT;";
                await comandoSimple.ExecuteNonQueryAsync(ct);
            }

            foreach (var nomeArquivo in arquivosLog)
            {
                ct.ThrowIfCancellationRequested();
                mensagens?.Report($"Executando SHRINKFILE (TRUNCATEONLY) em \"{nomeArquivo}\"...");

                await using var comandoShrink = conexao.CreateCommand();
                comandoShrink.CommandTimeout = 0; // shrink pode demorar bastante — sem timeout
                comandoShrink.CommandText = $"DBCC SHRINKFILE(N'{IdentificadorSql.EscaparAspaSimples(nomeArquivo)}', 0, TRUNCATEONLY);";
                await comandoShrink.ExecuteNonQueryAsync(ct);

                mensagens?.Report($"Arquivo \"{nomeArquivo}\" reduzido.");
            }
        }
        finally
        {
            // CancellationToken.None de propósito: mesmo se a operação foi
            // cancelada (ou o shrink falhou), o Recovery Model original
            // TEM que ser restaurado antes de sair — nunca deixar o banco
            // preso em SIMPLE por causa de um cancelamento.
            if (precisaAlternar)
            {
                mensagens?.Report($"Restaurando o Recovery Model de \"{nomeBancoAtual}\" para {recoveryModelOriginal}...");

                // SEM "WITH NO_WAIT": com NO_WAIT o ALTER DATABASE falha na hora
                // se não conseguir o lock naquele instante — e bastava uma
                // transação de usuário começar bem nesse momento para o banco
                // FICAR EM SIMPLE, quebrando a cadeia de log de forma silenciosa
                // (todo backup de log passa a falhar até o próximo full). Sem
                // NO_WAIT o comando espera o lock, que é o comportamento
                // desejado aqui. Duas tentativas, e na segunda com ROLLBACK
                // IMMEDIATE para não depender das sessões alheias terminarem.
                Exception? falhaRestauracao = null;
                for (var tentativa = 1; tentativa <= 2; tentativa++)
                {
                    try
                    {
                        await using var comandoRestaurar = conexao.CreateCommand();
                        comandoRestaurar.CommandTimeout = 0;
                        comandoRestaurar.CommandText = tentativa == 1
                            ? $"ALTER DATABASE [{bancoEscapado}] SET RECOVERY {recoveryModelOriginal};"
                            : $"ALTER DATABASE [{bancoEscapado}] SET RECOVERY {recoveryModelOriginal} WITH ROLLBACK IMMEDIATE;";
                        await comandoRestaurar.ExecuteNonQueryAsync(CancellationToken.None);
                        falhaRestauracao = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        falhaRestauracao = ex;
                        mensagens?.Report($"Tentativa {tentativa} de restaurar o Recovery Model falhou: {ex.Message}");
                    }
                }

                if (falhaRestauracao is not null)
                {
                    // Não dá para engolir: o banco ficou em SIMPLE e a cadeia de
                    // log está quebrada. O usuário PRECISA saber, com o comando
                    // para corrigir à mão e o aviso de que um FULL novo é
                    // necessário para reiniciar a cadeia.
                    var aviso =
                        $"ATENÇÃO: o shrink terminou, mas NÃO foi possível restaurar o Recovery Model de " +
                        $"\"{nomeBancoAtual}\" para {recoveryModelOriginal} — o banco continua em SIMPLE e a cadeia " +
                        $"de backup de log está quebrada. Execute manualmente no SSMS: " +
                        $"ALTER DATABASE [{nomeBancoAtual}] SET RECOVERY {recoveryModelOriginal} WITH ROLLBACK IMMEDIATE; " +
                        $"e faça um BACKUP FULL novo para reiniciar a cadeia de log. Erro: {falhaRestauracao.Message}";
                    mensagens?.Report(aviso);
                    throw new InvalidOperationException(aviso, falhaRestauracao);
                }
            }
        }
    }

    /// <summary>
    /// Executa DBCC SHRINKDATABASE no banco selecionado (todos os arquivos
    /// de dados e log de uma vez) — mesmo comando do script fornecido pelo
    /// usuário (<c>DBCC SHRINKDATABASE(N'CloudADM')</c>), aqui com o nome do
    /// banco lido da própria conexão em vez de fixo.
    /// </summary>
    public async Task ExecutarShrinkBancoAsync(string? nomeBanco, IProgress<string>? mensagens, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        // Lido de volta da própria conexão (em vez de reusar nomeBanco
        // diretamente) para cobrir o caso de nomeBanco vazio — aí é o banco
        // padrão do login que está sendo reduzido, e queremos reportar o
        // nome real dele na mensagem.
        var nomeBancoAtual = conexao.Database;
        mensagens?.Report($"Executando SHRINKDATABASE em \"{nomeBancoAtual}\"...");

        await using var comando = conexao.CreateCommand();
        comando.CommandTimeout = 0;
        comando.CommandText = $"DBCC SHRINKDATABASE(N'{IdentificadorSql.EscaparAspaSimples(nomeBancoAtual)}');";
        await comando.ExecuteNonQueryAsync(ct);

        mensagens?.Report($"Banco de dados \"{nomeBancoAtual}\" reduzido.");
    }

    /// <summary>
    /// Retorna as 25 tabelas que mais ocupam espaço no banco selecionado
    /// (dados + índices), mesmo cálculo usado por sp_spaceused por tabela —
    /// agrupado numa única consulta em vez de rodado uma vez por tabela.
    /// Ignora tabelas do próprio SQL Server (is_ms_shipped = 1).
    /// </summary>
    public async Task<List<TabelaTamanhoDto>> ObterTop25MaioresTabelasAsync(string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT TOP 25 " +
            "    s.name AS Esquema, " +
            "    t.name AS Tabela, " +
            "    SUM(CASE WHEN i.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS TotalLinhas, " +
            "    CAST(SUM(CASE WHEN i.index_id IN (0, 1) THEN a.used_pages ELSE 0 END) * 8.0 / 1024 AS DECIMAL(18,2)) AS TamanhoDadosMB, " +
            "    CAST(SUM(CASE WHEN i.index_id NOT IN (0, 1) THEN a.used_pages ELSE 0 END) * 8.0 / 1024 AS DECIMAL(18,2)) AS TamanhoIndicesMB " +
            "FROM sys.tables AS t " +
            "JOIN sys.schemas AS s ON t.schema_id = s.schema_id " +
            "JOIN sys.indexes AS i ON t.object_id = i.object_id " +
            "JOIN sys.partitions AS p ON i.object_id = p.object_id AND i.index_id = p.index_id " +
            "JOIN sys.allocation_units AS a ON p.partition_id = a.container_id " +
            "WHERE t.is_ms_shipped = 0 " +
            "GROUP BY s.name, t.name " +
            "ORDER BY SUM(a.used_pages) DESC;";

        var resultado = new List<TabelaTamanhoDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new TabelaTamanhoDto
            {
                Esquema = leitor.GetString(leitor.GetOrdinal("Esquema")),
                Tabela = leitor.GetString(leitor.GetOrdinal("Tabela")),
                TotalLinhas = leitor.GetInt64(leitor.GetOrdinal("TotalLinhas")),
                TamanhoDadosMB = leitor.GetDecimal(leitor.GetOrdinal("TamanhoDadosMB")),
                TamanhoIndicesMB = leitor.GetDecimal(leitor.GetOrdinal("TamanhoIndicesMB"))
            });
        }

        return resultado;
    }

    /// <summary>
    /// Retorna as colunas que formam a chave primária REAL da tabela
    /// informada (metadados — sys.indexes/sys.index_columns/sys.columns),
    /// na ordem da chave (key_ordinal). Lista vazia = tabela sem chave
    /// primária. Mais de 1 item = chave primária composta.
    /// </summary>
    public async Task<List<ChavePrimariaColunaDto>> ObterChavePrimariaAsync(string nomeTabela, string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT c.name AS NomeColuna, ty.name AS TipoDado, ic.key_ordinal AS Posicao " +
            "FROM sys.indexes AS i " +
            "JOIN sys.index_columns AS ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "JOIN sys.columns AS c ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
            "JOIN sys.types AS ty ON c.user_type_id = ty.user_type_id " +
            "WHERE i.object_id = OBJECT_ID(@tabela) AND i.is_primary_key = 1 " +
            "ORDER BY ic.key_ordinal;";
        comando.Parameters.AddWithValue("@tabela", nomeTabela);

        var resultado = new List<ChavePrimariaColunaDto>();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new ChavePrimariaColunaDto
            {
                NomeColuna = leitor.GetString(leitor.GetOrdinal("NomeColuna")),
                TipoDado = leitor.GetString(leitor.GetOrdinal("TipoDado")),
                Posicao = leitor.GetByte(leitor.GetOrdinal("Posicao"))
            });
        }

        return resultado;
    }

    /// <summary>Diz se <paramref name="tipoDado"/> (retornado por <see cref="ObterChavePrimariaAsync"/>) é um tipo inteiro — condição para habilitar a limpeza "por ID" (comparação numérica "menor ou igual").</summary>
    public static bool EhTipoNumericoInteiro(string tipoDado) =>
        TiposNumericosInteiros.Contains(tipoDado, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Retorna as colunas de tipo data/hora (<see cref="TiposDataHora"/>) da
    /// tabela informada, para popular o combo de escolha manual da coluna
    /// usada na limpeza "por data" — nunca adivinhada por nome.
    /// </summary>
    public async Task<List<string>> ObterColunasDataAsync(string nomeTabela, string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT c.name " +
            "FROM sys.columns AS c " +
            "JOIN sys.types AS ty ON c.user_type_id = ty.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@tabela) " +
            "  AND ty.name IN ('date', 'datetime', 'datetime2', 'smalldatetime', 'datetimeoffset') " +
            "ORDER BY c.column_id;";
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
    /// Retorna 2 linhas (todas as colunas da tabela) — o primeiro e o
    /// último registro de <paramref name="nomeTabela"/>, ordenados por
    /// <paramref name="nomeColunaChave"/> (a chave primária real, vinda de
    /// <see cref="ObterChavePrimariaAsync"/>). Como o conjunto de colunas
    /// varia de tabela para tabela (não dá para tipar em um DTO fixo em tempo
    /// de compilação), o retorno é um <see cref="DataTable"/> — a única
    /// exceção no módulo ao padrão de DTOs tipados do restante da ferramenta,
    /// usado aqui de propósito para poder ligar a grade da UI diretamente
    /// (AutoGenerateColumns = true) a QUALQUER tabela do banco. A primeira
    /// coluna do resultado ("Registro") identifica cada linha como
    /// "Primeiro" ou "Último".
    /// </summary>
    public async Task<DataTable> ObterPrimeiroUltimoRegistroAsync(string nomeTabela, string nomeColunaChave, string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        // Nome QUALIFICADO ([schema].[tabela]): as listagens passaram a devolver
        // "schema.tabela" justamente porque montar só [tabela] fazia o SQL Server
        // resolver no schema padrão — e a exclusão caía na tabela errada quando
        // existiam duas com o mesmo nome em schemas diferentes.
        var tabelaQualificada = IdentificadorSql.Qualificar(nomeTabela);
        var colunaEscapada = IdentificadorSql.EscaparColchetes(nomeColunaChave);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            $"SELECT 'Primeiro' AS Registro, P.* FROM (SELECT TOP 1 * FROM {tabelaQualificada} ORDER BY [{colunaEscapada}] ASC) AS P " +
            "UNION ALL " +
            $"SELECT 'Último' AS Registro, U.* FROM (SELECT TOP 1 * FROM {tabelaQualificada} ORDER BY [{colunaEscapada}] DESC) AS U;";

        var tabela = new DataTable();
        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        tabela.Load(leitor);
        return tabela;
    }

    /// <summary>
    /// Apaga em lotes (<see cref="TamanhoLotePadrao"/> linhas por vez) todos
    /// os registros de <paramref name="nomeTabela"/> onde
    /// <paramref name="nomeColunaData"/> (escolhida manualmente pelo usuário
    /// — ver <see cref="ObterColunasDataAsync"/>) seja menor ou igual a
    /// <paramref name="dataLimite"/> — incluindo a HORA escolhida pelo
    /// usuário na tela (não mais só o dia). Reporta progresso a cada lote e
    /// observa cancelamento entre lotes (nunca no meio de um DELETE já em
    /// execução).
    ///
    /// NOTA: antes a UI só coletava dia/mês/ano (hora sempre meia-noite), o
    /// que exigia comparar contra o início do dia seguinte para não perder
    /// registros do mesmo dia com hora &gt; 00:00. Agora que a UI também
    /// coleta a hora (<c>numHora</c>), a comparação volta a ser direta
    /// ("&lt;=" contra <paramref name="dataLimite"/> exatamente como
    /// informado) — é responsabilidade da UI montar
    /// <paramref name="dataLimite"/> com a hora desejada (ex.: 23 para
    /// cobrir o dia inteiro, como antes).
    /// </summary>
    public async Task ExcluirRegistrosPorDataAsync(
        string nomeTabela, string nomeColunaData, DateTime dataLimite, string? nomeBanco,
        IProgress<string>? mensagens, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        // Nome QUALIFICADO ([schema].[tabela]): as listagens passaram a devolver
        // "schema.tabela" justamente porque montar só [tabela] fazia o SQL Server
        // resolver no schema padrão — e a exclusão caía na tabela errada quando
        // existiam duas com o mesmo nome em schemas diferentes.
        var tabelaQualificada = IdentificadorSql.Qualificar(nomeTabela);
        var colunaEscapada = IdentificadorSql.EscaparColchetes(nomeColunaData);
        // long, e não int: o acumulador estourava em silêncio (virava negativo)
        // passando de 2,1 bilhões de linhas — e tabela de log/auditoria com esse
        // volume é justamente o alvo desta tela.
        var totalExcluido = 0L;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var comando = conexao.CreateCommand();
            // Sem timeout (0), de propósito: quando a coluna do filtro NÃO tem
            // índice — o caso comum nas colunas de data que esta tela recebe —
            // cada lote varre a tabela inteira, e numa tabela de 50 milhões de
            // linhas um lote só já passa fácil dos 120 s que estavam aqui. O
            // efeito era o pior possível: a limpeza morria no meio, com vários
            // lotes JÁ COMITADOS e sem explicação nenhuma. Isso não torna a
            // operação impossível de abortar — o cancelamento continua
            // disponível pelo CancellationToken, observado pelo próprio
            // ExecuteScalarAsync e entre os lotes.
            comando.CommandTimeout = 0;
            // "DELETE ...; SELECT @@ROWCOUNT;" em vez do retorno de
            // ExecuteNonQuery: aquele retorno SOMA as linhas afetadas pelos
            // gatilhos AFTER da tabela, então em qualquer tabela auditada o
            // "N registro(s) excluído(s)" saía inflado (o dobro, com um gatilho
            // que grava uma linha de auditoria por linha apagada). @@ROWCOUNT
            // logo depois do DELETE é a contagem real da instrução.
            comando.CommandText =
                $"DELETE TOP (@lote) FROM {tabelaQualificada} WHERE [{colunaEscapada}] <= @limite; " +
                "SELECT @@ROWCOUNT;";
            comando.Parameters.AddWithValue("@lote", TamanhoLotePadrao);
            comando.Parameters.AddWithValue("@limite", dataLimite);

            var retorno = await comando.ExecuteScalarAsync(ct);
            var linhasAfetadas = retorno is null or DBNull ? 0 : Convert.ToInt32(retorno);
            totalExcluido += linhasAfetadas;
            mensagens?.Report($"{totalExcluido} registro(s) excluído(s) até agora...");

            // A condição de parada continua correta com a contagem real: o
            // DELETE TOP (n) apaga min(n, linhas que atendem ao filtro), então
            // um lote menor que o tamanho pedido significa que acabou.
            if (linhasAfetadas < TamanhoLotePadrao)
            {
                break;
            }
        }

        mensagens?.Report(totalExcluido == 0
            ? "Nenhum registro encontrado com data menor ou igual à informada."
            : $"Concluído: {totalExcluido} registro(s) excluído(s) no total.");
    }

    /// <summary>
    /// Apaga em lotes (<see cref="TamanhoLotePadrao"/> linhas por vez) todos
    /// os registros de <paramref name="nomeTabela"/> onde
    /// <paramref name="nomeColunaChave"/> (a chave primária real, quando
    /// numérica de 1 coluna só — ver <see cref="ObterChavePrimariaAsync"/> e
    /// <see cref="EhTipoNumericoInteiro"/>) seja menor ou igual a
    /// <paramref name="idLimite"/>. Mesmo comportamento de lote/progresso/
    /// cancelamento de <see cref="ExcluirRegistrosPorDataAsync"/>.
    /// </summary>
    public async Task ExcluirRegistrosPorChaveAsync(
        string nomeTabela, string nomeColunaChave, long idLimite, string? nomeBanco,
        IProgress<string>? mensagens, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        // Nome QUALIFICADO ([schema].[tabela]): as listagens passaram a devolver
        // "schema.tabela" justamente porque montar só [tabela] fazia o SQL Server
        // resolver no schema padrão — e a exclusão caía na tabela errada quando
        // existiam duas com o mesmo nome em schemas diferentes.
        var tabelaQualificada = IdentificadorSql.Qualificar(nomeTabela);
        var colunaEscapada = IdentificadorSql.EscaparColchetes(nomeColunaChave);
        // Mesmos motivos do acumulador em ExcluirRegistrosPorDataAsync: long
        // para não estourar em silêncio passando de 2,1 bilhões de linhas.
        var totalExcluido = 0L;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var comando = conexao.CreateCommand();
            // Sem timeout (0) pelo mesmo motivo detalhado em
            // ExcluirRegistrosPorDataAsync: com a coluna do filtro sem índice
            // cada lote varre a tabela inteira e os 120 s antigos matavam a
            // limpeza no meio, com lotes já comitados. O cancelamento continua
            // pelo CancellationToken.
            comando.CommandTimeout = 0;
            // @@ROWCOUNT em vez do retorno de ExecuteNonQuery — que soma as
            // linhas dos gatilhos AFTER e inflava a contagem exibida. Ver o
            // comentário equivalente em ExcluirRegistrosPorDataAsync.
            comando.CommandText =
                $"DELETE TOP (@lote) FROM {tabelaQualificada} WHERE [{colunaEscapada}] <= @limite; " +
                "SELECT @@ROWCOUNT;";
            comando.Parameters.AddWithValue("@lote", TamanhoLotePadrao);
            comando.Parameters.AddWithValue("@limite", idLimite);

            var retorno = await comando.ExecuteScalarAsync(ct);
            var linhasAfetadas = retorno is null or DBNull ? 0 : Convert.ToInt32(retorno);
            totalExcluido += linhasAfetadas;
            mensagens?.Report($"{totalExcluido} registro(s) excluído(s) até agora...");

            // Condição de parada com a contagem real — ver comentário em
            // ExcluirRegistrosPorDataAsync.
            if (linhasAfetadas < TamanhoLotePadrao)
            {
                break;
            }
        }

        mensagens?.Report(totalExcluido == 0
            ? "Nenhum registro encontrado com ID menor ou igual ao informado."
            : $"Concluído: {totalExcluido} registro(s) excluído(s) no total.");
    }

    /// <summary>
    /// Apaga NO MÁXIMO 1 registro de <paramref name="nomeTabela"/> onde
    /// <paramref name="nomeColunaData"/> seja menor ou igual a
    /// <paramref name="dataLimite"/> (dia + hora escolhidos pelo usuário) —
    /// pedido do usuário como forma de testar a condição antes de rodar a
    /// limpeza em lote (<see cref="ExcluirRegistrosPorDataAsync"/>). Mesmo
    /// filtro e mesma nota sobre a hora — ver o comentário em
    /// <see cref="ExcluirRegistrosPorDataAsync"/> — mas sem loop/progresso:
    /// é uma única instrução, praticamente instantânea. Retorna 1 se um
    /// registro foi excluído, 0 se nenhum atendia à condição.
    /// </summary>
    public async Task<int> ExcluirUmRegistroPorDataAsync(string nomeTabela, string nomeColunaData, DateTime dataLimite, string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using var comando = conexao.CreateCommand();
        // 600 s (e não os 60 s anteriores): "praticamente instantâneo" só vale
        // quando alguma linha atende ao filtro. Se NENHUMA atende — que é
        // exatamente o resultado que este teste existe para revelar — e a coluna
        // não tem índice, o DELETE TOP (1) varre a tabela INTEIRA antes de
        // devolver 0. Aqui não vale o "sem timeout" dos DELETEs em lote: como é
        // uma instrução única, não existe o risco de morrer no meio deixando
        // lotes comitados, então um limite generoso porém finito basta.
        comando.CommandTimeout = 600;
        // @@ROWCOUNT em vez do retorno de ExecuteNonQuery, que soma as linhas
        // dos gatilhos AFTER — numa tabela auditada este método devolvia 2 ou
        // mais para UM registro excluído, quebrando o contrato documentado
        // (1 = excluiu, 0 = nada atendia).
        comando.CommandText =
            $"DELETE TOP (1) FROM {IdentificadorSql.Qualificar(nomeTabela)} WHERE [{IdentificadorSql.EscaparColchetes(nomeColunaData)}] <= @limite; " +
            "SELECT @@ROWCOUNT;";
        comando.Parameters.AddWithValue("@limite", dataLimite);

        var retorno = await comando.ExecuteScalarAsync(ct);
        return retorno is null or DBNull ? 0 : Convert.ToInt32(retorno);
    }

    /// <summary>Mesma ideia de <see cref="ExcluirUmRegistroPorDataAsync"/>, aplicada à limpeza "por ID" (chave primária).</summary>
    public async Task<int> ExcluirUmRegistroPorChaveAsync(string nomeTabela, string nomeColunaChave, long idLimite, string? nomeBanco, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using var comando = conexao.CreateCommand();
        // Mesmos motivos de ExcluirUmRegistroPorDataAsync: timeout generoso
        // (varredura completa quando nada atende ao filtro) e @@ROWCOUNT para
        // não contar as linhas dos gatilhos AFTER.
        comando.CommandTimeout = 600;
        comando.CommandText =
            $"DELETE TOP (1) FROM {IdentificadorSql.Qualificar(nomeTabela)} WHERE [{IdentificadorSql.EscaparColchetes(nomeColunaChave)}] <= @limite; " +
            "SELECT @@ROWCOUNT;";
        comando.Parameters.AddWithValue("@limite", idLimite);

        var retorno = await comando.ExecuteScalarAsync(ct);
        return retorno is null or DBNull ? 0 : Convert.ToInt32(retorno);
    }

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

    // EscaparColchetes/EscaparAspaSimples eram privados aqui e idênticos aos de
    // FerramentasDBA.Classes.Infraestrutura.IdentificadorSql (mesmo Replace, sem
    // nenhuma diferença de comportamento). Foram removidos e as chamadas passaram
    // a usar o utilitário compartilhado — era o mesmo código repetido em quatro
    // módulos, e uma correção feita em um deles não chegava nos outros.
}
