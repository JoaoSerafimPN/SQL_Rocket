using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Administração e segurança. Atende aos submenus "Manutenção > Collation",
/// "Manutenção > Segurança" (Logs, Usuários) e "Manutenção > Backup"
/// (Full, Restore).
///
/// IMPORTANTE: <see cref="ObterBancosDadosAsync"/>, <see cref="ObterEdicaoAsync"/>,
/// <see cref="ExecutarBackupFullAsync"/>, <see cref="ObterArquivosBackupAsync"/>,
/// <see cref="ExecutarRestoreAsync"/> e todo o grupo de collation
/// (<see cref="ObterCollationDatabaseAsync"/>, <see cref="ObterComparativoCollationColunasAsync"/>,
/// <see cref="ObterDetalheColunasAsync"/>, <see cref="ObterCollationsServidorAsync"/>,
/// <see cref="AlterarCollationColunasAsync"/> ("das colunas", com recriação
/// automática de índice), <see cref="AlterarCollationTabelaAsync"/> ("das
/// tabelas", lote único sem recriação de índice — script próprio fornecido
/// pelo usuário), <see cref="AlterarCollationBancoAsync"/>) já estão implementados;
/// <see cref="ObterLogsErroAsync"/>/<see cref="ObterArquivosLogErroAsync"/>
/// (xp_readerrorlog/xp_enumerrorlogs) também já estão implementados.
/// "Segurança &gt; Usuários" também está implementado: criação de login
/// (<see cref="CriarLoginSqlAsync"/>/<see cref="CriarLoginWindowsAsync"/>),
/// criação de usuário no banco (<see cref="CriarUsuarioBancoAsync"/>) e
/// gestão de permissões de servidor
/// (<see cref="ObterPapeisServidorAsync"/>/<see cref="AtualizarPapeisServidorAsync"/>,
/// <see cref="ObterPermissoesServidorGranularesAsync"/>/<see cref="AtualizarPermissoesServidorGranularesAsync"/>)
/// e de banco de dados, incluindo tabelas/views separadamente
/// (<see cref="ObterPapeisBancoAsync"/>/<see cref="AtualizarPapeisBancoAsync"/>,
/// <see cref="ObterPermissoesBancoGranularesAsync"/>/<see cref="AtualizarPermissoesBancoGranularesAsync"/>,
/// <see cref="ObterPermissoesObjetosAsync"/>/<see cref="AtualizarPermissoesObjetosAsync"/>).
/// </summary>
public class Modulo_Admin
{
    private readonly Conectar_SQL _conexao;

    // Usado só pelas ações "Mudar a collation das colunas"/"das tabelas"
    // (ver AlterarCollationColunasAsync): quando uma coluna a alterar
    // participa de um índice, reaproveita a geração/exclusão de índice já
    // existente em Modulo_Indices (mesmo código usado pela tela "Índice >
    // Fragmentação" antes de excluir um índice sem uso) em vez de duplicar
    // essa lógica aqui.
    private readonly Modulo_Indices _moduloIndices;

    public Modulo_Admin(Conectar_SQL conexao, Modulo_Indices moduloIndices)
    {
        _conexao = conexao;
        _moduloIndices = moduloIndices;
    }

    /// <summary>
    /// Retorna a lista de bancos de dados existentes na instância conectada.
    /// Usado para popular o combo de seleção nas telas de Backup/Restore.
    /// </summary>
    public async Task<List<string>> ObterBancosDadosAsync(CancellationToken ct = default)
    {
        var bancos = new List<string>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT name FROM sys.databases ORDER BY name;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            bancos.Add(leitor.GetString(0));
        }

        return bancos;
    }

    /// <summary>
    /// Retorna a edição do SQL Server conectado (SERVERPROPERTY('Edition')),
    /// ex: "Express Edition", "Web Edition", "Enterprise Edition (64-bit)".
    /// Usado para decidir se a opção de compressão de backup pode ser
    /// oferecida (Express/Web não suportam BACKUP ... WITH COMPRESSION).
    /// </summary>
    public async Task<string> ObterEdicaoAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT SERVERPROPERTY('Edition') AS Edition;";

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado as string ?? string.Empty;
    }

    /// <summary>
    /// Indica se a edição informada (retorno de <see cref="ObterEdicaoAsync"/>)
    /// suporta BACKUP ... WITH COMPRESSION — não suportado em Express/Web.
    /// </summary>
    public static bool SuportaCompressaoDeBackup(string edicao) =>
        !edicao.Contains("Express", StringComparison.OrdinalIgnoreCase) &&
        !edicao.Contains("Web", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Indica se a instância SQL Server conectada está rodando numa máquina
    /// DIFERENTE desta (cliente rodando a SQL Rocket) — pedido do
    /// usuário (funcionário testou e reportou que as telas de "Backup"
    /// mostram informação de disco da máquina onde a FERRAMENTA roda, não
    /// do SERVIDOR: os botões "Add..."/"Procurar..." abrem um
    /// SaveFileDialog/OpenFileDialog do Windows, que só enxerga os discos
    /// DESTE computador — mas o caminho escolhido ali vira o
    /// "TO DISK = N'...'"/"FROM DISK = N'...'" do T-SQL, que é resolvido
    /// pelo SERVIÇO do SQL Server, na máquina do SERVIDOR). Comparação
    /// best-effort: <c>SERVERPROPERTY('MachineName')</c> (o nome de máquina
    /// que o PRÓPRIO SERVIDOR reporta de si mesmo — não a string de conexão
    /// usada pelo cliente, que poderia ser um alias/IP/nome de instância
    /// nomeada) contra <see cref="Environment.MachineName"/> (o nome desta
    /// máquina, onde a ferramenta está rodando). Não distingue "servidor
    /// remoto, mas com o mesmo nome de host por coincidência" nem
    /// "servidor rodando dentro de um container/VM com hostname próprio,
    /// mas fisicamente no mesmo hardware" — para o objetivo aqui (decidir
    /// se vale a pena avisar o usuário e tentar consultar o disco do
    /// servidor via SQL em vez de confiar no diálogo local do Windows),
    /// esses casos-limite não importam.
    /// </summary>
    public async Task<bool> InstanciaEhRemotaAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128));";

        var resultado = await comando.ExecuteScalarAsync(ct);
        var nomeMaquinaServidor = resultado as string;
        return !string.IsNullOrWhiteSpace(nomeMaquinaServidor)
            && !string.Equals(nomeMaquinaServidor.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lista as unidades de disco FIXAS e o espaço livre (em MB) reportados
    /// pelo próprio SERVIDOR SQL Server, via <c>xp_fixeddrives</c> — mesma
    /// extended stored procedure que o SSMS usa internamente para sugerir
    /// caminhos nas telas de Backup/Restore. Ao contrário de
    /// <c>sys.dm_os_volume_stats</c> (já usado dentro de
    /// <c>Modulo_Informacoes.ObterInformacoesSqlAsync</c>, mas limitado às
    /// unidades onde algum banco já tem arquivo), isto lista TODAS as
    /// unidades fixas do servidor, o que é mais útil aqui: o usuário ainda
    /// não escolheu o caminho de destino do backup, então não há arquivo
    /// nenhum para ancorar a consulta. Requer só permissão de EXECUTE em
    /// <c>xp_fixeddrives</c> (concedida a <c>public</c> por padrão — não
    /// precisa de sysadmin), mas pode falhar em instâncias com essa
    /// extended procedure desabilitada/removida por política de segurança;
    /// quem chama deve tratar a exceção e degradar de forma honesta (ver
    /// uso em Menu_Raiz).
    /// </summary>
    public async Task<List<UnidadeDiscoServidorDto>> ObterUnidadesDiscoServidorAsync(CancellationToken ct = default)
    {
        var unidades = new List<UnidadeDiscoServidorDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "EXEC master.dbo.xp_fixeddrives;";
        comando.CommandTimeout = 15;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        var idxUnidade = leitor.GetOrdinal("drive");
        var idxLivreMb = leitor.GetOrdinal("MB free");
        while (await leitor.ReadAsync(ct))
        {
            unidades.Add(new UnidadeDiscoServidorDto
            {
                Unidade = Convert.ToString(leitor.GetValue(idxUnidade)) ?? string.Empty,
                LivreMb = Convert.ToInt64(leitor.GetValue(idxLivreMb))
            });
        }

        return unidades;
    }

    /// <summary>
    /// Verifica, no próprio SERVIDOR SQL Server, se um caminho (arquivo OU
    /// pasta) existe — via <c>xp_fileexist</c> (mesma família de extended
    /// stored procedures que <see cref="ObterUnidadesDiscoServidorAsync"/>,
    /// mesma exigência de permissão modesta: EXECUTE, não precisa de
    /// sysadmin). Pedido do usuário (funcionário testou, com screenshot):
    /// depois do aviso de instância remota (ver
    /// <c>Menu_Raiz.ObterAvisoInstanciaRemotaAsync</c>), o diálogo nativo do
    /// Windows abre e mostra os discos/unidades de rede mapeadas DESTE
    /// computador — que podem, ou não, ter qualquer relação com o disco do
    /// servidor (dependendo se a empresa mapeia uma pasta de rede que
    /// aponta pro mesmo lugar). Não dá pra saber isso só olhando o
    /// diálogo — só perguntando pro próprio servidor. Esta consulta é o
    /// jeito de perguntar: chamada pela tela ANTES de rodar o
    /// BACKUP/RESTORE de verdade, para avisar (sem bloquear) quando o
    /// caminho digitado/escolhido claramente não existe no servidor.
    ///
    /// Retorna null quando não foi possível determinar (erro de conexão,
    /// falta de permissão para executar <c>xp_fileexist</c>, ou caminho
    /// vazio) — quem chama não deve tratar null como "não existe", só como
    /// "não deu pra confirmar" (a operação segue em frente sem aviso extra
    /// nesse caso, em vez de bloquear por um falso negativo).
    /// </summary>
    public async Task<(bool Existe, bool EhDiretorio)?> VerificarCaminhoNoServidorAsync(string caminho, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caminho))
        {
            return null;
        }

        try
        {
            await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = "EXEC master.dbo.xp_fileexist @caminho;";
            comando.CommandTimeout = 15;
            comando.Parameters.AddWithValue("@caminho", caminho);

            await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
            if (!await leitor.ReadAsync(ct))
            {
                return null;
            }

            var idxArquivoExiste = leitor.GetOrdinal("File Exists");
            var idxEhDiretorio = leitor.GetOrdinal("File is a Directory");
            var existe = Convert.ToInt32(leitor.GetValue(idxArquivoExiste)) == 1;
            var ehDiretorio = Convert.ToInt32(leitor.GetValue(idxEhDiretorio)) == 1;
            return (existe, ehDiretorio);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executa um BACKUP DATABASE FULL para o banco informado e, opcionalmente,
    /// valida a integridade do arquivo gerado com RESTORE VERIFYONLY.
    /// </summary>
    /// <param name="opcoes">
    /// Opções refletindo os checkboxes da tela (Copy-only, Compressão,
    /// Verificar integridade). Quando omitido, nenhuma opção extra é aplicada.
    /// A opção de compressão é ignorada (removida do script) em edições
    /// Express/Web, mesmo que <see cref="BackupOpcoes.Compressao"/> esteja
    /// marcada — essas edições não suportam BACKUP ... WITH COMPRESSION.
    /// </param>
    /// <param name="mensagens">
    /// Recebe, em tempo real, as mensagens informativas que o SQL Server
    /// envia durante a execução (ex: "5 percent processed.", "BACKUP DATABASE
    /// successfully processed..."), as mesmas exibidas na aba "Messages" do
    /// SSMS. Opcional — quando omitido, as mensagens são apenas descartadas.
    /// </param>
    public async Task<BackupOperacaoResult> ExecutarBackupFullAsync(
        string nomeBanco,
        string caminhoDestino,
        BackupOpcoes? opcoes = null,
        IProgress<string>? mensagens = null,
        CancellationToken ct = default)
    {
        var cronometro = Stopwatch.StartNew();
        try
        {
            var edicao = await ObterEdicaoAsync(ct);
            var copyOnly = opcoes?.CopyOnly ?? false;
            var compressao = (opcoes?.Compressao ?? false) && SuportaCompressaoDeBackup(edicao);
            // Padrão: NÃO sobrescrever (anexa ao arquivo). Ver BackupOpcoes.SobrescreverArquivo.
            var sobrescrever = opcoes?.SobrescreverArquivo ?? false;
            mensagens?.Report(sobrescrever
                ? "Modo SOBRESCREVER (WITH INIT): os backups já existentes neste arquivo serão apagados."
                : "Modo ANEXAR (WITH NOINIT): o backup será acrescentado ao arquivo, preservando os anteriores.");

            var scriptBackup = MontarScriptBackupFull(nomeBanco, caminhoDestino, copyOnly, compressao, sobrescrever);
            await ExecutarComandoComMensagensAsync(scriptBackup, mensagens, ct);

            if (opcoes?.VerificarIntegridade == true)
            {
                var scriptVerificacao = MontarScriptVerificarIntegridade(nomeBanco, caminhoDestino);
                await ExecutarComandoComMensagensAsync(scriptVerificacao, mensagens, ct);
            }

            cronometro.Stop();
            mensagens?.Report($"Completion time: {DateTimeOffset.Now:O}");

            return new BackupOperacaoResult
            {
                Sucesso = true,
                Duracao = cronometro.Elapsed,
                CaminhoArquivo = caminhoDestino
            };
        }
        catch (Exception ex)
        {
            cronometro.Stop();
            mensagens?.Report($"Erro: {ex.Message}");
            return new BackupOperacaoResult
            {
                Sucesso = false,
                Duracao = cronometro.Elapsed,
                MensagemDetalhe = ex.Message,
                CaminhoArquivo = caminhoDestino
            };
        }
    }

    /// <summary>
    /// Executa um comando T-SQL repassando, em tempo real via
    /// <paramref name="mensagens"/>, as mensagens informativas que o SQL
    /// Server envia durante a execução (evento <see cref="SqlConnection.InfoMessage"/>
    /// — é assim que chegam as linhas "N percent processed." de um
    /// BACKUP/RESTORE com STATS).
    /// </summary>
    private async Task ExecutarComandoComMensagensAsync(string scriptSql, IProgress<string>? mensagens, CancellationToken ct)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);

        void AoReceberMensagem(object? sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError erro in e.Errors)
            {
                mensagens?.Report(erro.Message);
            }
        }

        conexao.InfoMessage += AoReceberMensagem;
        try
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = scriptSql;
            comando.CommandTimeout = 0; // backup/verify podem demorar bastante; sem timeout de comando
            await comando.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            conexao.InfoMessage -= AoReceberMensagem;
        }
    }

    /// <summary>
    /// Monta o script de BACKUP DATABASE FULL, incluindo/removendo COPY_ONLY
    /// e COMPRESSION conforme os parâmetros (mesma estrutura gerada pelo
    /// assistente de backup do SSMS).
    /// </summary>
    private static string MontarScriptBackupFull(string nomeBanco, string caminhoDestino, bool copyOnly, bool compressao, bool sobrescreverArquivo)
    {
        var opcoesWith = new List<string>();
        if (copyOnly)
        {
            opcoesWith.Add("COPY_ONLY");
        }

        opcoesWith.Add("NOFORMAT");
        // NOINIT (anexar) por padrão; INIT (apagar o que já existe no arquivo)
        // só quando o usuário pediu explicitamente. Ver BackupOpcoes.SobrescreverArquivo.
        opcoesWith.Add(sobrescreverArquivo ? "INIT" : "NOINIT");
        opcoesWith.Add($"NAME = N'{IdentificadorSql.EscaparAspaSimples(nomeBanco)}-Full Database Backup'");
        // SKIP desliga a checagem de expiração/nome da mídia; só faz sentido
        // junto do INIT (é o que permite sobrescrever). Com NOINIT usamos
        // NOSKIP, que mantém a verificação padrão do SQL Server.
        opcoesWith.Add(sobrescreverArquivo ? "SKIP" : "NOSKIP");
        opcoesWith.Add("NOREWIND");
        opcoesWith.Add("NOUNLOAD");

        if (compressao)
        {
            opcoesWith.Add("COMPRESSION");
        }

        opcoesWith.Add("STATS = 5");

        return
            $"BACKUP DATABASE [{IdentificadorSql.EscaparColchetes(nomeBanco)}] " +
            $"TO DISK = N'{IdentificadorSql.EscaparAspaSimples(caminhoDestino)}' " +
            $"WITH {string.Join(", ", opcoesWith)};";
    }

    /// <summary>
    /// Monta o script de verificação de integridade (RESTORE VERIFYONLY)
    /// contra o backup mais recente do banco no arquivo de destino.
    /// </summary>
    private static string MontarScriptVerificarIntegridade(string nomeBanco, string caminhoDestino)
    {
        var nomeEscapado = IdentificadorSql.EscaparAspaSimples(nomeBanco);
        var caminhoEscapado = IdentificadorSql.EscaparAspaSimples(caminhoDestino);

        return
            "DECLARE @backupSetId AS INT;\n" +
            "SELECT @backupSetId = position FROM msdb..backupset " +
            $"WHERE database_name = N'{nomeEscapado}' AND backup_set_id = " +
            $"(SELECT MAX(backup_set_id) FROM msdb..backupset WHERE database_name = N'{nomeEscapado}');\n" +
            "IF @backupSetId IS NULL " +
            $"BEGIN RAISERROR(N'Verify failed. Backup information for database ''{nomeEscapado}'' not found.', 16, 1) END\n" +
            $"RESTORE VERIFYONLY FROM DISK = N'{caminhoEscapado}' WITH FILE = @backupSetId, NOUNLOAD, NOREWIND;";
    }

    /// <summary>
    /// Lê o "cabeçalho" de um arquivo de backup (RESTORE FILELISTONLY) e
    /// retorna a lista de arquivos originais do banco (dados e log), com o
    /// nome lógico de cada um. O nome lógico é definido no banco de origem
    /// no momento em que o arquivo físico foi criado — não há como adivinhar
    /// ou digitar esse valor manualmente na tela, por isso ele é sempre lido
    /// diretamente do arquivo de backup antes do RESTORE.
    /// </summary>
    public async Task<List<ArquivoBackupInfo>> ObterArquivosBackupAsync(string caminhoArquivoBackup, CancellationToken ct = default)
    {
        var arquivos = new List<ArquivoBackupInfo>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{IdentificadorSql.EscaparAspaSimples(caminhoArquivoBackup)}';";
        // Ler o cabeçalho de um .bak grande (ou numa pasta de rede lenta) passa
        // fácil dos 30s padrão do ADO.NET, e aí o restore falhava antes mesmo
        // de começar.
        comando.CommandTimeout = 0;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        var idxNomeLogico = leitor.GetOrdinal("LogicalName");
        var idxNomeFisico = leitor.GetOrdinal("PhysicalName");
        var idxTipo = leitor.GetOrdinal("Type");

        while (await leitor.ReadAsync(ct))
        {
            arquivos.Add(new ArquivoBackupInfo
            {
                NomeLogico = leitor.GetString(idxNomeLogico),
                NomeFisicoOriginal = leitor.GetString(idxNomeFisico),
                Tipo = leitor.IsDBNull(idxTipo) ? string.Empty : leitor.GetString(idxTipo)
            });
        }

        return arquivos;
    }

    /// <summary>
    /// Executa um RESTORE DATABASE completo a partir de um arquivo de backup,
    /// redirecionando o arquivo de dados (MDF) e o de log (LDF) para os
    /// caminhos informados na tela (equivalente às cláusulas MOVE geradas
    /// pelo assistente de restore do SSMS).
    /// </summary>
    /// <remarks>
    /// Os nomes lógicos exigidos pelas cláusulas MOVE não são digitados pelo
    /// usuário: são obtidos automaticamente via <see cref="ObterArquivosBackupAsync"/>
    /// (RESTORE FILELISTONLY) a partir do próprio arquivo de backup, já que
    /// esse valor é definido no banco de origem e pode ser diferente em cada backup.
    /// </remarks>
    /// <param name="mensagens">
    /// Recebe, em tempo real, as mensagens informativas que o SQL Server
    /// envia durante a execução (ex: "5 percent processed.", "RESTORE DATABASE
    /// successfully processed..."), as mesmas exibidas na aba "Messages" do
    /// SSMS. Opcional — quando omitido, as mensagens são apenas descartadas.
    /// </param>
    public async Task<BackupOperacaoResult> ExecutarRestoreAsync(
        string nomeBanco,
        string caminhoArquivoBackup,
        string caminhoArquivoMdf,
        string caminhoArquivoLdf,
        IProgress<string>? mensagens = null,
        CancellationToken ct = default)
    {
        var cronometro = Stopwatch.StartNew();
        try
        {
            var arquivosBackup = await ObterArquivosBackupAsync(caminhoArquivoBackup, ct);
            var arquivoDados = arquivosBackup.FirstOrDefault(a => a.Tipo.Equals("D", StringComparison.OrdinalIgnoreCase));
            var arquivoLog = arquivosBackup.FirstOrDefault(a => a.Tipo.Equals("L", StringComparison.OrdinalIgnoreCase));

            if (arquivoDados is null || arquivoLog is null)
            {
                throw new InvalidOperationException(
                    "Não foi possível identificar os arquivos de dados (MDF) e de log (LDF) dentro do backup informado " +
                    "(RESTORE FILELISTONLY não retornou um arquivo de cada tipo).");
            }

            // Conjunto de backup MAIS RECENTE do arquivo (antes era sempre o
            // primeiro/mais antigo — ver ObterPosicaoUltimoBackupAsync).
            var posicaoBackup = await ObterPosicaoUltimoBackupAsync(caminhoArquivoBackup, ct);
            mensagens?.Report($"Restaurando o conjunto de backup na posição {posicaoBackup} do arquivo.");

            var movimentacoes = MontarMovimentacoesRestore(arquivosBackup, caminhoArquivoMdf, caminhoArquivoLdf);

            // Lista o destino de CADA arquivo lógico antes de começar. O erro
            // típico do restore ("...cannot be overwritten...") aponta para o
            // caminho físico, e sem esta lista o usuário não tinha como saber
            // qual arquivo lógico do backup estava indo para lá.
            foreach (var (arquivo, destino) in movimentacoes)
            {
                var descricaoTipo = arquivo.Tipo.ToUpperInvariant() switch
                {
                    "D" => "dados",
                    "L" => "log",
                    "S" => "FILESTREAM",
                    _ => $"tipo {arquivo.Tipo}"
                };
                mensagens?.Report(
                    $"MOVE: arquivo lógico \"{arquivo.NomeLogico}\" ({descricaoTipo}) — " +
                    $"original \"{arquivo.NomeFisicoOriginal}\" será restaurado em \"{destino}\".");
            }

            var scriptRestore = MontarScriptRestore(
                nomeBanco, caminhoArquivoBackup,
                movimentacoes,
                posicaoBackup);

            await ExecutarComandoComMensagensAsync(scriptRestore, mensagens, ct);

            cronometro.Stop();
            mensagens?.Report($"Completion time: {DateTimeOffset.Now:O}");

            return new BackupOperacaoResult
            {
                Sucesso = true,
                Duracao = cronometro.Elapsed,
                CaminhoArquivo = caminhoArquivoBackup
            };
        }
        catch (Exception ex)
        {
            cronometro.Stop();
            mensagens?.Report($"Erro: {ex.Message}");
            return new BackupOperacaoResult
            {
                Sucesso = false,
                Duracao = cronometro.Elapsed,
                MensagemDetalhe = ex.Message,
                CaminhoArquivo = caminhoArquivoBackup
            };
        }
    }

    /// <summary>
    /// Descobre a POSIÇÃO (coluna "Position" do RESTORE HEADERONLY) do conjunto
    /// de backup mais recente dentro do arquivo informado.
    ///
    /// Existe por causa de um defeito real da v1.0: o restore fixava
    /// "WITH FILE = 1", que é o conjunto MAIS ANTIGO da mídia. Num .bak gravado
    /// com NOINIT (append) — o padrão de qualquer job que escreve sempre no
    /// mesmo arquivo — isso restaurava silenciosamente uma versão de semanas
    /// atrás em vez da última, sem erro nenhum na tela.
    ///
    /// Lido em C# (e não com INSERT ... EXEC dentro do T-SQL) de propósito: a
    /// lista de colunas do RESTORE HEADERONLY muda entre versões do SQL Server,
    /// então declarar a tabela destino no script quebraria conforme o servidor.
    /// Aqui basta ler as colunas pelo nome.
    /// </summary>
    public async Task<int> ObterPosicaoUltimoBackupAsync(string caminhoArquivoBackup, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = $"RESTORE HEADERONLY FROM DISK = N'{IdentificadorSql.EscaparAspaSimples(caminhoArquivoBackup)}';";
        comando.CommandTimeout = 0;

        var posicao = 0;
        DateTime? maisRecente = null;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var ordinalPosicao = leitor.GetOrdinal("Position");
            if (leitor.IsDBNull(ordinalPosicao))
            {
                continue;
            }

            var posicaoAtual = Convert.ToInt32(leitor.GetValue(ordinalPosicao));

            DateTime? inicioAtual = null;
            var ordinalInicio = leitor.GetOrdinal("BackupStartDate");
            if (!leitor.IsDBNull(ordinalInicio))
            {
                inicioAtual = leitor.GetDateTime(ordinalInicio);
            }

            // Critério: a data de início mais recente; empate (ou arquivo sem
            // data) desempata pela maior posição, que é a ordem de gravação.
            var ehMaisRecente = maisRecente is null
                || (inicioAtual is not null && inicioAtual > maisRecente)
                || (inicioAtual == maisRecente && posicaoAtual > posicao);

            if (ehMaisRecente)
            {
                maisRecente = inicioAtual ?? maisRecente;
                posicao = posicaoAtual;
            }
        }

        if (posicao <= 0)
        {
            throw new InvalidOperationException(
                "Não foi possível ler o cabeçalho do arquivo de backup (RESTORE HEADERONLY não retornou nenhum " +
                "conjunto de backup). Confirme se o caminho aponta para um arquivo de backup válido e acessível " +
                "pelo serviço do SQL Server.");
        }

        return posicao;
    }

    /// <summary>
    /// Separadores de caminho aceitos ao interpretar um caminho FÍSICO DO
    /// SERVIDOR. Os dois são tratados porque o caminho não é da máquina que
    /// roda o programa e sim da máquina onde o serviço do SQL Server está
    /// instalado — por isso nada aqui usa <c>System.IO.Path</c>, que resolveria
    /// pelas regras do sistema operacional LOCAL (e na prática o servidor pode
    /// ser Linux, com "/", mesmo com a tela rodando no Windows).
    /// </summary>
    private static readonly char[] SeparadoresCaminhoServidor = new[] { '\\', '/' };

    /// <summary>
    /// Quebra um caminho físico do servidor em (pasta COM o separador final,
    /// nome do arquivo). Quando não há separador nenhum, devolve pasta vazia.
    /// </summary>
    private static (string Pasta, string NomeArquivo) SepararCaminhoServidor(string caminho)
    {
        var corte = caminho.LastIndexOfAny(SeparadoresCaminhoServidor);
        return corte < 0
            ? (string.Empty, caminho)
            : (caminho[..(corte + 1)], caminho[(corte + 1)..]);
    }

    /// <summary>
    /// Decide para onde vai CADA arquivo lógico do backup.
    ///
    /// Defeito que isto corrige: antes o restore emitia exatamente duas
    /// cláusulas MOVE — o primeiro arquivo de dados e o primeiro de log. Num
    /// banco com mais de um arquivo (qualquer banco com NDF secundário, ou com
    /// mais de um LDF), os demais arquivos eram restaurados no CAMINHO FÍSICO
    /// ORIGINAL gravado dentro do .bak. Se a origem estava viva no mesmo
    /// servidor, o RESTORE tentava sobrescrever os arquivos do banco em uso e
    /// falhava com "...cannot be overwritten..."; se o backup veio de outro
    /// servidor, a pasta original podia simplesmente não existir no destino.
    /// Em nenhum dos dois casos a mensagem de erro indicava que a causa era um
    /// arquivo secundário sem MOVE.
    ///
    /// Regra: a tela informa UM caminho de destino para os dados e UM para o
    /// log (assinatura pública de ExecutarRestoreAsync, que não muda). O
    /// PRIMEIRO arquivo de dados ('D') vai para o caminho de dados informado e
    /// o PRIMEIRO de log ('L') para o de log; todos os demais (NDF extra, LDF
    /// extra, FILESTREAM 'S', full-text 'F'…) vão para a MESMA PASTA do
    /// destino correspondente, mantendo o próprio nome de arquivo original —
    /// assim dois arquivos nunca caem no mesmo caminho só por estarem na mesma
    /// pasta.
    /// </summary>
    private static List<(ArquivoBackupInfo Arquivo, string CaminhoDestino)> MontarMovimentacoesRestore(
        List<ArquivoBackupInfo> arquivosBackup, string caminhoArquivoMdf, string caminhoArquivoLdf)
    {
        var (pastaDados, _) = SepararCaminhoServidor(caminhoArquivoMdf);
        var (pastaLog, _) = SepararCaminhoServidor(caminhoArquivoLdf);

        var movimentacoes = new List<(ArquivoBackupInfo Arquivo, string CaminhoDestino)>();

        // Caminho físico do SQL Server não diferencia maiúscula de minúscula no
        // Windows, então a checagem de colisão também não pode diferenciar.
        var destinosUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primeiroDadosPendente = true;
        var primeiroLogPendente = true;

        foreach (var arquivo in arquivosBackup)
        {
            var ehDados = arquivo.Tipo.Equals("D", StringComparison.OrdinalIgnoreCase);
            var ehLog = arquivo.Tipo.Equals("L", StringComparison.OrdinalIgnoreCase);

            string destinoDesejado;
            if (ehDados && primeiroDadosPendente)
            {
                destinoDesejado = caminhoArquivoMdf;
                primeiroDadosPendente = false;
            }
            else if (ehLog && primeiroLogPendente)
            {
                destinoDesejado = caminhoArquivoLdf;
                primeiroLogPendente = false;
            }
            else
            {
                // Demais arquivos: pasta do destino correspondente + nome de
                // arquivo original. Tipos que não são 'D' nem 'L' (FILESTREAM,
                // full-text) acompanham a pasta de dados, que é onde fazem
                // sentido.
                var (_, nomeOriginal) = SepararCaminhoServidor(arquivo.NomeFisicoOriginal);
                destinoDesejado = (ehLog ? pastaLog : pastaDados) + nomeOriginal;
            }

            movimentacoes.Add((arquivo, TornarDestinoUnico(destinoDesejado, destinosUsados)));
        }

        return movimentacoes;
    }

    /// <summary>
    /// Garante que dois arquivos lógicos nunca sejam restaurados no MESMO
    /// caminho físico. Acontece de verdade quando o banco tem arquivos de
    /// mesmo nome em pastas (ou discos) diferentes na origem — por exemplo um
    /// "Vendas_02.ndf" no disco D: e outro "Vendas_02.ndf" no disco E: — e
    /// todos passam a apontar
    /// para a pasta única escolhida na tela. Sem isto, o RESTORE gravaria um
    /// arquivo por cima do outro (ou falharia), então o nome repetido ganha um
    /// sufixo "_2", "_3"… em vez de sobrescrever.
    /// </summary>
    private static string TornarDestinoUnico(string destinoDesejado, HashSet<string> destinosUsados)
    {
        if (destinosUsados.Add(destinoDesejado))
        {
            return destinoDesejado;
        }

        var (pasta, nomeArquivo) = SepararCaminhoServidor(destinoDesejado);

        // "> 0" (e não ">= 0") de propósito: um nome que COMEÇA com ponto é
        // nome, não extensão.
        var posicaoPonto = nomeArquivo.LastIndexOf('.');
        var nomeSemExtensao = posicaoPonto > 0 ? nomeArquivo[..posicaoPonto] : nomeArquivo;
        var extensao = posicaoPonto > 0 ? nomeArquivo[posicaoPonto..] : string.Empty;

        for (var sufixo = 2; sufixo <= 1000; sufixo++)
        {
            var candidato = $"{pasta}{nomeSemExtensao}_{sufixo}{extensao}";
            if (destinosUsados.Add(candidato))
            {
                return candidato;
            }
        }

        throw new InvalidOperationException(
            $"Não foi possível definir um caminho de destino livre para \"{destinoDesejado}\" " +
            "(mais de 1000 arquivos do backup disputam o mesmo nome).");
    }

    /// <summary>
    /// Monta o script de RESTORE DATABASE com uma cláusula MOVE para CADA
    /// arquivo lógico do backup (ver <see cref="MontarMovimentacoesRestore"/>),
    /// mesma estrutura gerada pelo assistente de restore do SSMS.
    /// </summary>
    private static string MontarScriptRestore(
        string nomeBanco, string caminhoArquivoBackup,
        List<(ArquivoBackupInfo Arquivo, string CaminhoDestino)> movimentacoes,
        int posicaoBackup)
    {
        var clausulasMove = string.Join(
            ", ",
            movimentacoes.Select(m =>
                $"MOVE N'{IdentificadorSql.EscaparAspaSimples(m.Arquivo.NomeLogico)}' TO N'{IdentificadorSql.EscaparAspaSimples(m.CaminhoDestino)}'"));

        return
            $"RESTORE DATABASE [{IdentificadorSql.EscaparColchetes(nomeBanco)}] " +
            $"FROM DISK = N'{IdentificadorSql.EscaparAspaSimples(caminhoArquivoBackup)}' " +
            // Posição do conjunto de backup escolhido (o mais recente do
            // arquivo) — antes era "FILE = 1" fixo, o mais ANTIGO da mídia.
            // Ver ObterPosicaoUltimoBackupAsync.
            $"WITH FILE = {posicaoBackup}, " +
            $"{clausulasMove}, " +
            "NOUNLOAD, STATS = 5;";
    }

    /// <summary>
    /// Retorna a collation do banco de dados informado — script 1 fornecido
    /// pelo usuário para a tela "Collation &gt; Collation Manager" (SELECT
    /// name, collation_name FROM sys.databases WHERE name = ...),
    /// parametrizado em vez do WHERE name = DB_NAME() original: assim
    /// funciona para qualquer banco escolhido no combo "Database" da tela,
    /// não só o banco em que a conexão abriu.
    /// </summary>
    public async Task<CollationInfoDto> ObterCollationDatabaseAsync(string nomeBanco, CancellationToken ct = default)
    {
        var resultado = new CollationInfoDto { NomeBanco = nomeBanco };

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT name, collation_name FROM sys.databases WHERE name = @nomeBanco;";
        comando.Parameters.AddWithValue("@nomeBanco", nomeBanco);

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            var idxCollation = leitor.GetOrdinal("collation_name");
            resultado.CollationBanco = leitor.IsDBNull(idxCollation) ? string.Empty : leitor.GetString(idxCollation);
        }

        return resultado;
    }

    /// <summary>
    /// Retorna, para cada coluna de texto de cada tabela de usuário do banco
    /// informado, a collation da coluna e o status comparado com a collation
    /// do próprio banco — script 2 ("VISÃO COMPARATIVA") fornecido pelo
    /// usuário, sem alterações. Executa no contexto do banco escolhido (ver
    /// <see cref="MudarBancoSeInformado"/>), já que sys.tables/sys.columns
    /// são catálogos por banco. Usado para popular a grade "Collations
    /// found" (agrupada por collation, com contagem — ver
    /// Menu_Raiz.MostrarPaginaCollation) e para decidir, mais adiante, quais
    /// colunas realmente precisam de ALTER.
    /// </summary>
    public async Task<List<CollationColunaDto>> ObterComparativoCollationColunasAsync(string nomeBanco, CancellationToken ct = default)
    {
        var linhas = new List<CollationColunaDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    s.name AS Esquema, " +
            "    t.name AS Tabela, " +
            "    c.name AS Coluna, " +
            "    c.collation_name AS CollationDaColuna, " +
            "    DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS CollationDoBanco, " +
            "    CASE " +
            "        WHEN c.collation_name <> CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(128)) " +
            "        THEN 'DIVERGENTE' " +
            "        ELSE 'IGUAL' " +
            "    END AS StatusCollation " +
            "FROM sys.tables t " +
            "INNER JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "INNER JOIN sys.columns c ON t.object_id = c.object_id " +
            "WHERE c.collation_name IS NOT NULL " +
            "ORDER BY StatusCollation DESC, s.name, t.name;";
        comando.CommandTimeout = 60;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        var idxEsquema = leitor.GetOrdinal("Esquema");
        var idxTabela = leitor.GetOrdinal("Tabela");
        var idxColuna = leitor.GetOrdinal("Coluna");
        var idxCollationColuna = leitor.GetOrdinal("CollationDaColuna");
        var idxCollationBanco = leitor.GetOrdinal("CollationDoBanco");
        var idxStatus = leitor.GetOrdinal("StatusCollation");

        while (await leitor.ReadAsync(ct))
        {
            linhas.Add(new CollationColunaDto
            {
                Esquema = leitor.GetString(idxEsquema),
                Tabela = leitor.GetString(idxTabela),
                Coluna = leitor.GetString(idxColuna),
                CollationDaColuna = leitor.IsDBNull(idxCollationColuna) ? string.Empty : leitor.GetString(idxCollationColuna),
                CollationDoBanco = leitor.IsDBNull(idxCollationBanco) ? string.Empty : leitor.GetString(idxCollationBanco),
                StatusCollation = leitor.IsDBNull(idxStatus) ? string.Empty : leitor.GetString(idxStatus)
            });
        }

        return linhas;
    }

    /// <summary>
    /// Retorna, para TODAS as colunas de TODAS as tabelas de usuário do
    /// banco informado (inclusive as que não são de texto), o tipo de dado
    /// e a collation — script fornecido pelo usuário para a grade "Detalhe
    /// de colunas" (logo abaixo de "Collations found" na tela "Collation
    /// &gt; Collation Manager"), sem alterações. Ao contrário de
    /// <see cref="ObterComparativoCollationColunasAsync"/>, não filtra por
    /// <c>c.collation_name IS NOT NULL</c> nem compara com a collation do
    /// banco — é a listagem crua de colunas, com "N/A (Não-Texto)" no lugar
    /// da collation quando a coluna não é de tipo texto. Executa no
    /// contexto do banco escolhido (ver <see cref="MudarBancoSeInformado"/>).
    /// </summary>
    public async Task<List<DetalheColunaDto>> ObterDetalheColunasAsync(string nomeBanco, CancellationToken ct = default)
    {
        var linhas = new List<DetalheColunaDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    t.name AS Tabela, " +
            "    c.name AS NomeDaColuna, " +
            "    type_name(c.user_type_id) AS TipoDeDado, " +
            "    ISNULL(c.collation_name, 'N/A (Não-Texto)') AS CollationDaColuna " +
            "FROM sys.tables t " +
            "INNER JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "INNER JOIN sys.columns c ON t.object_id = c.object_id " +
            "ORDER BY s.name, t.name, c.column_id;";
        comando.CommandTimeout = 60;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        var idxTabela = leitor.GetOrdinal("Tabela");
        var idxColuna = leitor.GetOrdinal("NomeDaColuna");
        var idxTipo = leitor.GetOrdinal("TipoDeDado");
        var idxCollation = leitor.GetOrdinal("CollationDaColuna");

        while (await leitor.ReadAsync(ct))
        {
            linhas.Add(new DetalheColunaDto
            {
                Tabela = leitor.GetString(idxTabela),
                NomeDaColuna = leitor.GetString(idxColuna),
                TipoDeDado = leitor.IsDBNull(idxTipo) ? string.Empty : leitor.GetString(idxTipo),
                CollationDaColuna = leitor.IsDBNull(idxCollation) ? string.Empty : leitor.GetString(idxCollation)
            });
        }

        return linhas;
    }

    /// <summary>
    /// Retorna as collations "Latin1" instaladas no servidor
    /// (sys.fn_helpcollations, filtrado por nome — o catálogo completo passa
    /// de 5 mil linhas, praticamente todas irrelevantes para um app em
    /// português/Brasil; o filtro pode ser removido/ajustado se um dia for
    /// preciso trabalhar com outra família de collation). Usado para
    /// popular o combo "Collation (default-PWI)" — a collation de destino
    /// das 3 ações da tela "Collation &gt; Collation Manager".
    /// </summary>
    public async Task<List<string>> ObterCollationsServidorAsync(CancellationToken ct = default)
    {
        var collations = new List<string>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "SELECT name FROM sys.fn_helpcollations() WHERE name LIKE '%Latin1%' ORDER BY name;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            collations.Add(leitor.GetString(0));
        }

        return collations;
    }

    /// <summary>
    /// Gera, para cada coluna de texto de tabela de usuário do banco
    /// informado (ou só de <paramref name="nomeTabela"/>, quando informado,
    /// e/ou só de <paramref name="nomeColuna"/> dentro dela, quando também
    /// informado), o script "ALTER TABLE ... ALTER COLUMN ... COLLATE
    /// {novaCollation}" — script gerador de scripts de alteração fornecido
    /// pelo usuário (mesma consulta usada tanto para "Mudar a collation das
    /// colunas", sem filtro nenhum, quanto para "Mudar a collation das
    /// tabelas", com <paramref name="nomeTabela"/> informado, quanto para
    /// "Mudar a collation da coluna selecionada", com os dois informados —
    /// o usuário forneceu os primeiros dois scripts em rodadas diferentes,
    /// mas são a mesma consulta com filtros a mais; o filtro por coluna foi
    /// adicionado depois, a pedido do usuário, porque "Mudar a collation
    /// das colunas" sem filtro nenhum estava alterando TODAS as colunas do
    /// banco quando o usuário só queria mudar uma), com "SUA_NOVA_COLLATION"
    /// substituído pela collation escolhida no combo da tela.
    /// <paramref name="novaCollation"/> é validada contra uma whitelist de
    /// caracteres antes de entrar concatenada no T-SQL: o combo de origem é
    /// DropDownList (só aceita valores da lista), mas como o valor vira
    /// parte de uma computed column dentro do script original (não dá pra
    /// usar um parâmetro de comando ali), a validação aqui evita qualquer
    /// risco de injeção.
    /// </summary>
    public async Task<List<ScriptAlteracaoColunaDto>> GerarScriptsAlteracaoCollationColunasAsync(
        string nomeBanco, string novaCollation, string? nomeTabela = null, string? nomeColuna = null, CancellationToken ct = default)
    {
        if (!RegexNomeCollationValido.IsMatch(novaCollation))
        {
            throw new ArgumentException($"Nome de collation inválido: \"{novaCollation}\".", nameof(novaCollation));
        }

        var linhas = new List<ScriptAlteracaoColunaDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    s.name AS Esquema, " +
            "    t.name AS Tabela, " +
            "    c.name AS Coluna, " +
            "    'ALTER TABLE [' + s.name + '].[' + t.name + '] ALTER COLUMN [' + c.name + '] ' + " +
            "    type_name(c.user_type_id) + " +
            "    CASE " +
            "        WHEN type_name(c.user_type_id) IN ('varchar', 'char', 'varbinary') " +
            "            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS VARCHAR(10)) END + ')' " +
            "        WHEN type_name(c.user_type_id) IN ('nvarchar', 'nchar') " +
            "            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS VARCHAR(10)) END + ')' " +
            "        ELSE '' " +
            "    END + " +
            $"    ' COLLATE {novaCollation} ' + " +
            "    CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END + ';' AS ScriptAlteracao " +
            "FROM sys.tables t " +
            "INNER JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "INNER JOIN sys.columns c ON t.object_id = c.object_id " +
            "WHERE c.collation_name IS NOT NULL " +
            // Coluna computada tem collation_name preenchida (a collation do
            // resultado da expressão), então ela entrava nesta lista — mas
            // "ALTER TABLE ... ALTER COLUMN [computada]" SEMPRE falha: quem
            // manda na coluna é a expressão, não o tipo. Pior: em
            // AlterarCollationColunasAsync essa falha só aparecia DEPOIS de os
            // índices dependentes já terem sido excluídos, ou seja, o programa
            // fazia todo o ciclo caro e bloqueante de drop/recreate de índice
            // para terminar num erro garantido. Para mudar a collation do
            // resultado de uma computed column é preciso reescrever a
            // expressão (ALTER TABLE DROP/ADD da coluna) — fora do escopo
            // desta tela.
            "  AND c.is_computed = 0 " +
            "  AND t.is_ms_shipped = 0 " +
            "  AND (@nomeTabela IS NULL OR t.name = @nomeTabela) " +
            "  AND (@nomeColuna IS NULL OR c.name = @nomeColuna);";
        comando.CommandTimeout = 60;
        comando.Parameters.AddWithValue("@nomeTabela", (object?)nomeTabela ?? DBNull.Value);
        comando.Parameters.AddWithValue("@nomeColuna", (object?)nomeColuna ?? DBNull.Value);

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        var idxEsquema = leitor.GetOrdinal("Esquema");
        var idxTabela = leitor.GetOrdinal("Tabela");
        var idxColuna = leitor.GetOrdinal("Coluna");
        var idxScript = leitor.GetOrdinal("ScriptAlteracao");

        while (await leitor.ReadAsync(ct))
        {
            linhas.Add(new ScriptAlteracaoColunaDto
            {
                Esquema = leitor.GetString(idxEsquema),
                Tabela = leitor.GetString(idxTabela),
                Coluna = leitor.GetString(idxColuna),
                ScriptAlteracao = leitor.GetString(idxScript)
            });
        }

        return linhas;
    }

    /// <summary>
    /// Retorna os nomes dos índices (comuns, chave primária ou restrição
    /// única) que referenciam a coluna informada — chave ou incluída, tanto
    /// faz, já que os dois casos impedem um ALTER COLUMN de collation
    /// enquanto o índice existir. Usado por
    /// <see cref="AlterarCollationColunasAsync"/> para decidir quais
    /// índices precisam ser removidos e recriados em volta do ALTER.
    /// </summary>
    public async Task<List<string>> ObterIndicesQueReferenciamColunaAsync(
        string nomeBanco, string nomeTabela, string nomeColuna, CancellationToken ct = default, string? nomeEsquema = null)
    {
        var indices = new List<string>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT DISTINCT i.name " +
            "FROM sys.index_columns ic " +
            "INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "INNER JOIN sys.tables t ON t.object_id = ic.object_id " +
            "INNER JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            // O filtro por esquema é opcional só para não quebrar quem já
            // chamava sem ele; mas sem esquema a consulta casa por NOME em
            // TODOS os esquemas, e quem vai excluir os índices devolvidos aqui
            // acaba mexendo em tabela homônima de outro esquema.
            "WHERE t.name = @nomeTabela AND c.name = @nomeColuna AND i.name IS NOT NULL " +
            "  AND (@nomeEsquema IS NULL OR s.name = @nomeEsquema);";
        comando.Parameters.AddWithValue("@nomeTabela", nomeTabela);
        comando.Parameters.AddWithValue("@nomeColuna", nomeColuna);
        comando.Parameters.AddWithValue("@nomeEsquema", (object?)nomeEsquema ?? DBNull.Value);

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            indices.Add(leitor.GetString(0));
        }

        return indices;
    }

    /// <summary>
    /// Executa, uma a uma, as alterações de collation geradas por
    /// <see cref="GerarScriptsAlteracaoCollationColunasAsync"/> (todas as
    /// tabelas do banco, quando <paramref name="nomeTabela"/> vem nulo —
    /// botão "Mudar a collation das colunas" —, só as colunas de
    /// <paramref name="nomeTabela"/> — botão "Mudar a collation das
    /// tabelas" —, ou só a coluna <paramref name="nomeColuna"/> dentro de
    /// <paramref name="nomeTabela"/> quando os dois vêm informados — botão
    /// "Mudar a collation da coluna selecionada") — continua para a próxima
    /// coluna mesmo se uma falhar,
    /// registrando sucesso/falha de cada uma para o .txt de log que a tela
    /// gera ao final (ver Menu_Raiz.MostrarPaginaCollation).
    ///
    /// Quando a coluna participa de um índice (comum, chave primária ou
    /// restrição única — ver <see cref="ObterIndicesQueReferenciamColunaAsync"/>),
    /// o SQL Server recusa o ALTER COLUMN enquanto o índice existir; pedido
    /// do usuário: "para as colunas se for preciso pode recriar o índice".
    /// Por coluna, em 3 fases, reaproveitando Modulo_Indices (mesmo código
    /// já usado antes de excluir um índice sem uso):
    /// 1) gera o script de recriação de CADA índice dependente ANTES de
    ///    excluir qualquer um (<see cref="Modulo_Indices.GerarScriptRecriacaoIndiceAsync"/>)
    ///    — se algum tipo de índice não for suportado pela geração
    ///    automática (COLUMNSTORE/XML/SPATIAL etc.), a coluna inteira é
    ///    marcada como falha aqui, SEM excluir nada;
    /// 2) só então exclui os índices (<see cref="Modulo_Indices.ExcluirIndiceAsync"/>);
    /// 3) executa o ALTER COLUMN e, num finally, recria os índices
    ///    removidos — mesmo se o ALTER falhar, sempre tenta devolver os
    ///    índices que existiam (nunca deixa a coluna sem os índices que
    ///    tinha por causa de uma alteração de collation que não deu certo).
    ///
    /// IMPORTANTE — o que este método NÃO faz: não executa nenhum ALTER
    /// DATABASE ... SET OFFLINE/ONLINE (isso é <see cref="AlterarCollationBancoAsync"/>,
    /// que o usuário pediu separadamente só para a collation do banco em
    /// si). Cada ALTER COLUMN individual só bloqueia a própria tabela
    /// durante sua execução (comportamento padrão do comando).
    /// </summary>
    public async Task<List<ResultadoAlteracaoColunaDto>> AlterarCollationColunasAsync(
        string nomeBanco, string novaCollation, string? nomeTabela = null, string? nomeColuna = null, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        var scripts = await GerarScriptsAlteracaoCollationColunasAsync(nomeBanco, novaCollation, nomeTabela, nomeColuna, ct);
        var resultados = new List<ResultadoAlteracaoColunaDto>();

        foreach (var item in scripts)
        {
            var resultado = new ResultadoAlteracaoColunaDto
            {
                Esquema = item.Esquema,
                Tabela = item.Tabela,
                Coluna = item.Coluna,
                Script = item.ScriptAlteracao
            };

            try
            {
                // item.Esquema junto: sem ele a busca casava por NOME de tabela
                // em qualquer schema, então alterar "vendas.Cliente.Nome" também
                // derrubava e recriava os índices de "dbo.Cliente" — caro,
                // bloqueante e inútil (e, se a recriação falhasse, o estrago
                // acontecia numa tabela que o usuário nem tinha escolhido).
                var indicesDependentes = await ObterIndicesQueReferenciamColunaAsync(nomeBanco, item.Tabela, item.Coluna, ct, item.Esquema);

                // Nome qualificado para todas as operações de índice desta
                // coluna — as consultas de metadados do Modulo_Indices resolvem
                // o objeto por OBJECT_ID, que aceita "schema.tabela".
                var tabelaQualificada = string.IsNullOrWhiteSpace(item.Esquema)
                    ? item.Tabela
                    : $"{item.Esquema}.{item.Tabela}";

                // Fase 1: gera o script de recriação de TODOS os índices
                // dependentes antes de excluir qualquer um — se algum tipo
                // não for suportado, aborta aqui (nada foi excluído ainda).
                var scriptsRecriacao = new Dictionary<string, string>();
                foreach (var nomeIndice in indicesDependentes)
                {
                    try
                    {
                        scriptsRecriacao[nomeIndice] = await _moduloIndices.GerarScriptRecriacaoIndiceAsync(tabelaQualificada, nomeIndice, nomeBanco, ct);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"a coluna está no índice \"{nomeIndice}\", que não pôde ser preparado para recriação automática (nada foi alterado): {ex.Message}", ex);
                    }
                }

                // Fase 2: só agora exclui os índices (já com o script de
                // recriação de cada um em mãos).
                //
                // O script de recriação de CADA índice vai para o log ANTES de
                // o índice ser excluído. Isso é deliberado: os scripts só
                // existiam num dicionário em memória, então uma recriação
                // malsucedida deixava o índice perdido sem nenhum registro de
                // como refazê-lo. Como a tela grava estas mensagens num .txt, o
                // DDL passa a sobreviver a qualquer falha (inclusive a um
                // encerramento abrupto do programa no meio da operação).
                foreach (var (nomeIndice, scriptRecriacao) in scriptsRecriacao)
                {
                    mensagens?.Report(
                        $"[{item.Tabela}].[{item.Coluna}]: script para recriar o índice \"{nomeIndice}\" " +
                        $"(guarde esta linha — é o que refaz o índice caso algo dê errado): {scriptRecriacao}");
                    mensagens?.Report($"[{item.Tabela}].[{item.Coluna}]: removendo temporariamente o índice \"{nomeIndice}\"...");
                    await _moduloIndices.ExcluirIndiceAsync(tabelaQualificada, nomeIndice, nomeBanco, ct: ct);
                }

                var indicesRecriados = new List<string>();
                try
                {
                    // Fase 3: o ALTER COLUMN em si.
                    await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
                    MudarBancoSeInformado(conexao, nomeBanco);
                    await using SqlCommand comando = conexao.CreateCommand();
                    comando.CommandText = item.ScriptAlteracao;
                    comando.CommandTimeout = 0;
                    await comando.ExecuteNonQueryAsync(ct);
                }
                finally
                {
                    // Sempre tenta recriar os índices removidos, mesmo se o
                    // ALTER COLUMN acima tiver falhado — nunca deixa a coluna
                    // sem os índices que tinha por causa de uma alteração de
                    // collation malsucedida.
                    //
                    // Dois cuidados que faltavam e custavam índices de verdade:
                    //
                    // 1) try/catch POR ÍNDICE. Antes era um foreach solto: se a
                    //    recriação do primeiro índice falhasse, os demais nem
                    //    chegavam a ser TENTADOS e ficavam perdidos junto. O
                    //    caso clássico é sair de _CS_AS para _CI_AI e o índice
                    //    UNIQUE (ou a PK) falhar com "duplicate key" porque
                    //    'ABC' e 'abc' passaram a ser iguais — nada disso pode
                    //    impedir os outros índices de voltarem.
                    //
                    // 2) CancellationToken.None. Antes usava o "ct" vivo: um
                    //    cancelamento no meio interrompia justamente a etapa de
                    //    recriação, ou seja, cancelar a operação garantia a
                    //    perda dos índices já removidos.
                    var falhasRecriacao = new List<string>();

                    foreach (var (nomeIndice, scriptRecriacao) in scriptsRecriacao)
                    {
                        try
                        {
                            mensagens?.Report($"[{item.Tabela}].[{item.Coluna}]: recriando o índice \"{nomeIndice}\"...");
                            await using SqlConnection conexaoRecriar = await _conexao.AbrirConexaoAsync(CancellationToken.None);
                            MudarBancoSeInformado(conexaoRecriar, nomeBanco);
                            await using SqlCommand comandoRecriar = conexaoRecriar.CreateCommand();
                            comandoRecriar.CommandText = scriptRecriacao;
                            comandoRecriar.CommandTimeout = 0;
                            await comandoRecriar.ExecuteNonQueryAsync(CancellationToken.None);
                            indicesRecriados.Add(nomeIndice);
                        }
                        catch (Exception exRecriacao)
                        {
                            falhasRecriacao.Add($"\"{nomeIndice}\" ({exRecriacao.Message}) — script: {scriptRecriacao}");
                            mensagens?.Report(
                                $"FALHA ao recriar o índice \"{nomeIndice}\" em [{item.Tabela}]: {exRecriacao.Message}. " +
                                $"O índice continua REMOVIDO. Script para recriá-lo à mão: {scriptRecriacao}");
                        }
                    }

                    if (falhasRecriacao.Count > 0)
                    {
                        // Erro em destaque: a coluna pode até ter sido alterada
                        // com sucesso, mas a tabela ficou sem índice(s) — e num
                        // caso desses pode ser a própria chave primária.
                        var aviso =
                            $"ATENÇÃO: a tabela [{item.Tabela}] ficou SEM {falhasRecriacao.Count} índice(s) que foram " +
                            $"removidos para a alteração de collation e não puderam ser recriados: " +
                            $"{string.Join(" | ", falhasRecriacao)}";
                        mensagens?.Report(aviso);
                        resultado.IndicesNaoRecriados = string.Join(" | ", falhasRecriacao);
                    }
                }

                resultado.Sucesso = true;
                resultado.IndicesRecriados = indicesRecriados.Count > 0 ? string.Join(", ", indicesRecriados) : null;
                mensagens?.Report($"OK: [{item.Esquema}].[{item.Tabela}].[{item.Coluna}]");
            }
            catch (Exception ex)
            {
                resultado.Sucesso = false;
                resultado.MensagemErro = ex.Message;
                mensagens?.Report($"FALHA: [{item.Esquema}].[{item.Tabela}].[{item.Coluna}] — {ex.Message}");
            }

            resultados.Add(resultado);
        }

        return resultados;
    }

    /// <summary>
    /// Altera a collation de todas as colunas de texto de UMA tabela usando
    /// um único lote de SQL dinâmico montado e executado inteiramente no
    /// servidor — script fornecido pelo usuário para o botão "Mudar a
    /// collation das tabelas" (substitui a versão anterior, que reaproveitava
    /// <see cref="AlterarCollationColunasAsync"/> coluna por coluna com
    /// recriação automática de índice). Concatena um "ALTER TABLE ... ALTER
    /// COLUMN ... COLLATE ..." por coluna de texto da tabela numa variável
    /// NVARCHAR(MAX) e executa tudo de uma vez via <c>sp_executesql</c>. Os
    /// dois valores que o script original declarava com DECLARE @X = 'literal'
    /// (nome da tabela e collation de destino) entram aqui como parâmetros de
    /// comando, não concatenados — evita qualquer risco de injeção sem mudar
    /// o comportamento do script.
    /// </summary>
    /// <remarks>
    /// Diferença deliberada em relação a <see cref="AlterarCollationColunasAsync"/>:
    /// este lote NÃO detecta nem recria índices automaticamente. Se qualquer
    /// coluna da tabela participar de um índice (comum, chave primária ou
    /// restrição única), o SQL Server rejeita o ALTER daquela coluna e, como
    /// todas as instruções do lote rodam dentro de uma única chamada
    /// <c>sp_executesql</c> sem TRY/CATCH por coluna, a exceção interrompe o
    /// restante do lote — colunas que viriam depois da que falhou não chegam
    /// a ser tentadas, e as que já rodaram antes da falha permanecem
    /// alteradas (não há transação envolvendo o lote inteiro). Pedido
    /// explícito do usuário: usar este script tal como fornecido.
    /// </remarks>
    public async Task AlterarCollationTabelaAsync(string nomeBanco, string nomeTabela, string novaCollation, CancellationToken ct = default)
    {
        if (!RegexNomeCollationValido.IsMatch(novaCollation))
        {
            throw new ArgumentException($"Nome de collation inválido: \"{novaCollation}\".", nameof(novaCollation));
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "DECLARE @NomeTabela NVARCHAR(128) = @nomeTabelaParametro; " +
            "DECLARE @NovaCollation NVARCHAR(128) = @novaCollationParametro; " +
            "DECLARE @Sql NVARCHAR(MAX) = ''; " +
            "SELECT @Sql += 'ALTER TABLE [' + s.name + '].[' + t.name + '] ALTER COLUMN [' + c.name + '] ' + " +
            "    type_name(c.user_type_id) + " +
            "    CASE " +
            "        WHEN type_name(c.user_type_id) IN ('varchar', 'char', 'varbinary') " +
            "            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS VARCHAR(10)) END + ')' " +
            "        WHEN type_name(c.user_type_id) IN ('nvarchar', 'nchar') " +
            "            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS VARCHAR(10)) END + ')' " +
            "        ELSE '' " +
            "    END + " +
            "    ' COLLATE ' + @NovaCollation + ' ' + " +
            "    CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END + ';' + CHAR(13) " +
            "FROM sys.tables t " +
            "INNER JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "INNER JOIN sys.columns c ON t.object_id = c.object_id " +
            "WHERE t.name = @NomeTabela " +
            "  AND c.collation_name IS NOT NULL " +
            // Mesmo motivo do filtro em GerarScriptsAlteracaoCollationColunasAsync:
            // coluna computada tem collation_name preenchida mas não aceita
            // ALTER COLUMN. Aqui o estrago era ainda maior, porque o lote roda
            // inteiro num único sp_executesql sem TRY/CATCH por coluna: a
            // primeira computada abortava o lote e as colunas seguintes nem
            // chegavam a ser tentadas.
            "  AND c.is_computed = 0; " +
            "EXEC sp_executesql @Sql;";
        comando.CommandTimeout = 0;
        comando.Parameters.AddWithValue("@nomeTabelaParametro", nomeTabela);
        comando.Parameters.AddWithValue("@novaCollationParametro", novaCollation);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Altera a collation do BANCO DE DADOS em si — script fornecido pelo
    /// usuário para o botão "Mudar a collation do banco de dados": coloca o
    /// banco em SINGLE_USER WITH ROLLBACK IMMEDIATE (desconecta os demais
    /// usuários — é o "banco ficará OFFLINE" do aviso da tela), executa o
    /// ALTER DATABASE ... COLLATE, e devolve o banco para MULTI_USER.
    /// "GO" do script original não existe em ADO.NET — os 3 comandos são
    /// executados em sequência NA MESMA conexão (trocar de conexão entre
    /// eles falharia: depois do SINGLE_USER só a conexão que emitiu o
    /// comando continua podendo usar o banco).
    /// </summary>
    /// <remarks>
    /// Diferente do script original do usuário (que não trata erro), o
    /// MULTI_USER final roda num <c>finally</c> — mesmo se o ALTER DATABASE
    /// ... COLLATE falhar (ex.: ainda existe alguma dependência de
    /// collation, como um computed column ou CHECK CONSTRAINT), o banco
    /// nunca fica preso em SINGLE_USER por causa desta operação.
    ///
    /// IMPORTANTE: isto NÃO altera a collation de colunas/tabelas já
    /// existentes — só passa a valer para objetos novos criados sem
    /// collation explícita. Para colunas/tabelas existentes, ver
    /// <see cref="AlterarCollationColunasAsync"/>.
    /// </remarks>
    public async Task AlterarCollationBancoAsync(string nomeBanco, string novaCollation, IProgress<string>? mensagens = null, CancellationToken ct = default)
    {
        if (!RegexNomeCollationValido.IsMatch(novaCollation))
        {
            throw new ArgumentException($"Nome de collation inválido: \"{novaCollation}\".", nameof(novaCollation));
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        conexao.ChangeDatabase("master"); // "USE master;" do script do usuário.

        var bancoEscapado = IdentificadorSql.EscaparColchetes(nomeBanco);

        // "timeoutSegundos" existe porque o MULTI_USER da limpeza NÃO pode
        // esperar para sempre (ver o finally abaixo): com CommandTimeout = 0 e
        // outra sessão ocupando a vaga única, a chamada ficava pendurada
        // indefinidamente e a tela travava num await que nunca voltava — com o
        // banco preso em SINGLE_USER, recusando todo mundo.
        async Task ExecutarAsync(string sql, int timeoutSegundos, CancellationToken token)
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = sql;
            comando.CommandTimeout = timeoutSegundos;
            await comando.ExecuteNonQueryAsync(token);
        }

        mensagens?.Report($"Colocando \"{nomeBanco}\" em modo SINGLE_USER (desconecta os demais usuários)...");
        await ExecutarAsync($"ALTER DATABASE [{bancoEscapado}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 120, ct);

        try
        {
            mensagens?.Report($"Alterando a collation de \"{nomeBanco}\" para \"{novaCollation}\"...");

            // Até 2 tentativas: a conexão desta ferramenta está em "master", e
            // portanto NÃO ocupa a vaga única liberada pelo SINGLE_USER — uma
            // aplicação que reconecte no intervalo entre os dois comandos toma
            // essa vaga e faz o COLLATE falhar com "database is in use". Nesse
            // caso vale reexecutar o SINGLE_USER (derrubando o intruso) e
            // tentar de novo, em vez de abortar a operação inteira.
            for (var tentativa = 1; ; tentativa++)
            {
                try
                {
                    await ExecutarAsync($"ALTER DATABASE [{bancoEscapado}] COLLATE {novaCollation};", 0, ct);
                    break;
                }
                catch (Exception) when (tentativa == 1)
                {
                    mensagens?.Report(
                        "A alteração falhou (provavelmente outra conexão ocupou o banco no intervalo). " +
                        "Reaplicando SINGLE_USER e tentando mais uma vez...");
                    await ExecutarAsync($"ALTER DATABASE [{bancoEscapado}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 120, ct);
                }
            }

            mensagens?.Report("Collation do banco alterada com sucesso.");
        }
        finally
        {
            // Devolver o banco para MULTI_USER é obrigatório — se isto falhar, o
            // banco fica INACESSÍVEL para todo mundo. Por isso:
            //  - WITH ROLLBACK IMMEDIATE: derruba quem tiver tomado a vaga única,
            //    em vez de esperar indefinidamente por ela;
            //  - timeout finito (120s) em vez de 0, para nunca pendurar a tela;
            //  - CancellationToken.None: mesmo que a operação tenha sido
            //    cancelada, a volta para MULTI_USER TEM que ser tentada;
            //  - 2 tentativas e, se ainda assim falhar, um erro em destaque com
            //    o comando exato para o usuário rodar à mão no SSMS.
            mensagens?.Report($"Retornando \"{nomeBanco}\" para MULTI_USER...");

            Exception? falhaMultiUser = null;
            for (var tentativa = 1; tentativa <= 2; tentativa++)
            {
                try
                {
                    await ExecutarAsync(
                        $"ALTER DATABASE [{bancoEscapado}] SET MULTI_USER WITH ROLLBACK IMMEDIATE;",
                        120, CancellationToken.None);
                    falhaMultiUser = null;
                    break;
                }
                catch (Exception ex)
                {
                    falhaMultiUser = ex;
                    mensagens?.Report($"Tentativa {tentativa} de retornar para MULTI_USER falhou: {ex.Message}");
                }
            }

            if (falhaMultiUser is not null)
            {
                var aviso =
                    $"ATENÇÃO: o banco \"{nomeBanco}\" continua em SINGLE_USER e está recusando novas conexões. " +
                    $"Execute manualmente no SSMS, conectado ao master: " +
                    $"ALTER DATABASE [{nomeBanco}] SET MULTI_USER WITH ROLLBACK IMMEDIATE; " +
                    $"Erro: {falhaMultiUser.Message}";
                mensagens?.Report(aviso);
                throw new InvalidOperationException(aviso, falhaMultiUser);
            }
        }
    }

    /// <summary>
    /// Só aceita letras, dígitos e underscore — todo nome de collation do
    /// SQL Server segue esse padrão (ex.: SQL_Latin1_General_CP1_CI_AI,
    /// Latin1_General_100_CI_AI_SC). Usado para validar
    /// <see cref="GerarScriptsAlteracaoCollationColunasAsync"/> e
    /// <see cref="AlterarCollationBancoAsync"/> antes de concatenar o valor
    /// dentro do T-SQL gerado.
    /// </summary>
    private static readonly Regex RegexNomeCollationValido = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Troca a conexão já aberta para o banco informado (equivalente a um
    /// "USE [banco]", via <see cref="SqlConnection.ChangeDatabase"/>) —
    /// mesmo padrão usado em Modulo_Indices para o combo "Banco de Dados" da
    /// tela de Fragmentação, necessário aqui porque sys.tables/sys.columns
    /// são catálogos por banco: sem trocar de contexto, sempre refletiriam o
    /// banco em que a conexão abriu, não o banco escolhido no combo
    /// "Database" da tela de Collation.
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
    /// Lista os arquivos de log de erro do SQL Server disponíveis — o log
    /// atual (<see cref="ArquivoLogErroDto.Numero"/> == 0, em uso) e os
    /// arquivados (1, 2, 3... o mais antigo com o maior número), via
    /// sys.xp_enumerrorlogs. Usado para popular o combo "Arquivo" da tela
    /// "Segurança &gt; Logs" (equivalente à árvore "Select logs" do Log File
    /// Viewer do SSMS) e para montar o texto de <see cref="LogErroDto.FonteLog"/>
    /// em <see cref="ObterLogsErroAsync"/>.
    /// </summary>
    public async Task<List<ArquivoLogErroDto>> ObterArquivosLogErroAsync(CancellationToken ct = default)
    {
        var arquivos = new List<ArquivoLogErroDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = "EXEC master.dbo.xp_enumerrorlogs;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            arquivos.Add(new ArquivoLogErroDto
            {
                Numero = leitor.GetInt32(0),
                DataCriacao = LerDataHora(leitor, 1),
                TamanhoBytes = leitor.IsDBNull(2) ? 0 : Convert.ToInt64(leitor.GetValue(2))
            });
        }

        return arquivos;
    }

    /// <summary>
    /// Lê uma coluna de data/hora de xp_enumerrorlogs/xp_readerrorlog de
    /// forma tolerante ao tipo real devolvido pelo driver — em algumas
    /// versões/edições do SQL Server essas colunas vêm como
    /// <see cref="DateTime"/> e em outras como texto (o que quebrava
    /// <c>SqlDataReader.GetDateTime</c> com "Unable to cast object of type
    /// 'System.String' to type 'System.DateTime'"). Aceita os dois casos.
    /// </summary>
    private static DateTime LerDataHora(SqlDataReader leitor, int indice)
    {
        object valor = leitor.GetValue(indice);
        return valor switch
        {
            DateTime data => data,
            string texto when DateTime.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dataInvariante) => dataInvariante,
            string texto when DateTime.TryParse(texto, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime dataAtual) => dataAtual,
            _ => Convert.ToDateTime(valor, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Lê o log de erros do SQL Server — tela "Segurança &gt; Logs", no
    /// mesmo formato do "Log File Viewer" do SSMS: Data/Origem/Mensagem
    /// (colunas reais de xp_readerrorlog — LogDate/ProcessInfo/Text) e
    /// Tipo de Log/Fonte do Log (metadados de apresentação calculados aqui,
    /// não retornados pelo procedimento — ver <see cref="LogErroDto"/>).
    /// <paramref name="numeroArquivo"/> == 0 é o log atual; 1, 2, 3... são
    /// os arquivos arquivados (ver <see cref="ObterArquivosLogErroAsync"/>).
    /// <paramref name="filtroTexto"/>/<paramref name="dataInicio"/>/
    /// <paramref name="dataFim"/> são opcionais — equivalentes ao
    /// "Filter..." do Log File Viewer (procurar um texto e/ou restringir um
    /// período). Sempre devolve mais recente primeiro (xp_readerrorlog
    /// devolve mais antigo primeiro; a ordenação é feita aqui no C#, sem
    /// depender do parâmetro de ordenação do procedimento, que só existe em
    /// versões mais novas do SQL Server).
    /// </summary>
    public async Task<List<LogErroDto>> ObterLogsErroAsync(
        int numeroArquivo = 0, string? filtroTexto = null, DateTime? dataInicio = null, DateTime? dataFim = null,
        CancellationToken ct = default)
    {
        // O procedimento em si não devolve de qual arquivo cada linha veio
        // (sempre lê um arquivo por chamada) — busca o rótulo amigável
        // ("Current - ..."/"Archive #N - ...") à parte, só pra exibição.
        // Se não conseguir listar os arquivos (ex.: falta de permissão em
        // xp_enumerrorlogs), usa um rótulo simples em vez de travar a tela.
        var rotuloFonte = numeroArquivo == 0 ? "Current" : $"Archive #{numeroArquivo}";
        try
        {
            var arquivo = (await ObterArquivosLogErroAsync(ct)).FirstOrDefault(a => a.Numero == numeroArquivo);
            if (arquivo is not null)
            {
                rotuloFonte = arquivo.Rotulo;
            }
        }
        catch
        {
            // Ver comentário acima — segue com o rótulo simples.
        }

        var linhas = new List<LogErroDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        // xp_readerrorlog é um procedimento estendido do sistema — os
        // parâmetros são só posicionais (sem nomes oficiais):
        // (log_number, log_type [1 = log de erro do SQL Server, 2 = SQL
        // Server Agent], search_string_1, search_string_2, start_time,
        // end_time). search_string_2 não é usado aqui (NULL literal).
        comando.CommandText = "EXEC master.dbo.xp_readerrorlog @numeroArquivo, 1, @filtroTexto, NULL, @dataInicio, @dataFim;";
        comando.CommandTimeout = 60;
        comando.Parameters.Add("@numeroArquivo", SqlDbType.Int).Value = numeroArquivo;
        comando.Parameters.Add("@filtroTexto", SqlDbType.NVarChar, 255).Value = (object?)filtroTexto ?? DBNull.Value;
        comando.Parameters.Add("@dataInicio", SqlDbType.DateTime).Value = (object?)dataInicio ?? DBNull.Value;
        comando.Parameters.Add("@dataFim", SqlDbType.DateTime).Value = (object?)dataFim ?? DBNull.Value;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            linhas.Add(new LogErroDto
            {
                DataHora = LerDataHora(leitor, 0),
                ProcessInfo = leitor.IsDBNull(1) ? null : leitor.GetString(1),
                Texto = leitor.IsDBNull(2) ? string.Empty : leitor.GetString(2),
                TipoLog = "SQL Server",
                FonteLog = rotuloFonte
            });
        }

        return linhas.OrderByDescending(l => l.DataHora).ToList();
    }

    // ------------------------------------------------------------------
    // Segurança > Usuários — criação de login/usuário e gestão de
    // permissões (servidor e banco de dados, incluindo tabelas/views
    // separadamente). Pedido do usuário, sem script pronto fornecido
    // (T-SQL próprio — mesma autorização já dada para "Segurança > Logs"):
    // "criar o novo usuário no SQL, inserir o usuário no banco de dados,
    // alterar as permissões do usuário do SQL Server, alterar as
    // permissões do usuário do banco de dados, dentro do banco de dados
    // permissão em tabelas e views separados".
    //
    // Os papéis fixos (server roles/database roles) e as listas de
    // permissões granulares das seções "avançado" são conjuntos FIXOS
    // definidos aqui — cobrem os casos mais comuns de administração, não
    // é uma listagem exaustiva de tudo que o SQL Server suporta.
    // ------------------------------------------------------------------

    /// <summary>
    /// Papéis fixos de SERVIDOR (server roles), com descrição amigável —
    /// usado para montar a grade "Papéis do Servidor" da aba "Permissões
    /// de Servidor". "public" é o único do qual nenhum login pode ser
    /// removido (todo login sempre pertence a ele); mantido na lista só
    /// para exibição — <see cref="AtualizarPapeisServidorAsync"/> ignora
    /// silenciosamente qualquer tentativa de alterá-lo.
    /// </summary>
    private static readonly (string Papel, string Descricao)[] PapeisServidorFixos =
    {
        ("sysadmin", "Pode executar qualquer atividade no servidor."),
        ("serveradmin", "Pode alterar as configurações do servidor e desligá-lo."),
        ("securityadmin", "Gerencia logins e suas propriedades (GRANT/DENY/REVOKE em nível de servidor)."),
        ("processadmin", "Pode encerrar processos em execução na instância."),
        ("setupadmin", "Pode adicionar e remover servidores vinculados (linked servers)."),
        ("bulkadmin", "Pode executar a instrução BULK INSERT."),
        ("diskadmin", "Gerencia os arquivos de disco da instância."),
        ("dbcreator", "Pode criar, alterar, descartar e restaurar qualquer banco de dados."),
        ("public", "Papel padrão ao qual todo login pertence (não pode ser removido).")
    };

    /// <summary>
    /// Permissões granulares de SERVIDOR disponíveis na seção "avançado"
    /// da aba "Permissões de Servidor" — GRANT/DENY/REVOKE individual,
    /// para quando um dos papéis fixos concede mais do que o necessário.
    /// </summary>
    private static readonly (string Permissao, string Descricao)[] PermissoesServidorGranulares =
    {
        ("VIEW SERVER STATE", "Ver DMVs de servidor (sessões, processos, espera, etc.)."),
        ("VIEW ANY DEFINITION", "Ver a definição de qualquer objeto em qualquer banco."),
        ("VIEW ANY DATABASE", "Ver todos os bancos de dados na lista de bancos."),
        ("ALTER ANY LOGIN", "Criar, alterar e excluir logins."),
        ("ALTER ANY CREDENTIAL", "Gerenciar credenciais do servidor."),
        ("ALTER ANY CONNECTION", "Encerrar conexões de outros usuários (KILL)."),
        ("ALTER ANY DATABASE", "Criar e alterar qualquer banco de dados."),
        ("ALTER TRACE", "Criar e gerenciar rastreamentos (SQL Trace/Extended Events)."),
        ("ALTER SETTINGS", "Executar sp_configure e RECONFIGURE."),
        ("CREATE ANY DATABASE", "Criar novos bancos de dados."),
        ("CONTROL SERVER", "Controle total sobre a instância (equivalente a sysadmin)."),
        ("SHUTDOWN", "Desligar a instância do SQL Server.")
    };

    /// <summary>
    /// Papéis fixos de BANCO DE DADOS (database roles), com descrição
    /// amigável — usado para montar a grade "Papéis do Banco de Dados" da
    /// aba "Permissões de Banco/Tabelas e Views".
    /// </summary>
    private static readonly (string Papel, string Descricao)[] PapeisBancoFixos =
    {
        ("db_owner", "Pode executar qualquer atividade de configuração e manutenção no banco."),
        ("db_securityadmin", "Gerencia papéis e permissões de membros do banco."),
        ("db_accessadmin", "Gerencia o acesso ao banco para logins e grupos do Windows/SQL Server."),
        ("db_backupoperator", "Pode fazer backup do banco de dados."),
        ("db_ddladmin", "Pode executar qualquer comando DDL (CREATE/ALTER/DROP) no banco."),
        ("db_datawriter", "Pode inserir, alterar e excluir dados de todas as tabelas de usuário."),
        ("db_datareader", "Pode ler todos os dados de todas as tabelas de usuário."),
        ("db_denydatawriter", "Não pode inserir, alterar ou excluir dados de nenhuma tabela de usuário."),
        ("db_denydatareader", "Não pode ler dados de nenhuma tabela de usuário."),
        ("public", "Papel padrão ao qual todo usuário do banco pertence (não pode ser removido).")
    };

    /// <summary>
    /// Permissões granulares de BANCO DE DADOS disponíveis na seção
    /// "avançado" da aba "Permissões de Banco/Tabelas e Views" — escopo
    /// do banco inteiro (permissões específicas de uma tabela/view ficam
    /// na grade de objetos, ver <see cref="ObterPermissoesObjetosAsync"/>).
    /// </summary>
    private static readonly (string Permissao, string Descricao)[] PermissoesBancoGranulares =
    {
        ("CREATE TABLE", "Criar novas tabelas no banco."),
        ("CREATE VIEW", "Criar novas views no banco."),
        ("CREATE PROCEDURE", "Criar novas stored procedures no banco."),
        ("CREATE FUNCTION", "Criar novas functions no banco."),
        ("ALTER ANY SCHEMA", "Alterar qualquer schema do banco."),
        ("BACKUP DATABASE", "Fazer backup completo/diferencial do banco."),
        ("BACKUP LOG", "Fazer backup do log de transações do banco."),
        ("VIEW DEFINITION", "Ver a definição (script) dos objetos do banco.")
    };

    /// <summary>
    /// Lista os logins do servidor (sys.server_principals) — SQL Server
    /// Authentication (type 'S') e Windows (usuário 'U' ou grupo 'G') —
    /// usado para popular a grade da aba "Criar Login" e os combos de
    /// login das abas "Criar Usuário no Banco" e "Permissões de
    /// Servidor". Contas de sistema (##...##) são excluídas.
    /// </summary>
    public async Task<List<LoginDto>> ObterLoginsAsync(CancellationToken ct = default)
    {
        var logins = new List<LoginDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT name, type_desc, is_disabled, create_date
            FROM sys.server_principals
            WHERE type IN ('S', 'U', 'G') AND name NOT LIKE '##%'
            ORDER BY name;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            logins.Add(new LoginDto
            {
                Nome = leitor.GetString(0),
                TipoAutenticacao = leitor.GetString(1) == "SQL_LOGIN" ? "SQL Server" : "Windows",
                Desabilitado = leitor.GetBoolean(2),
                DataCriacao = LerDataHora(leitor, 3)
            });
        }

        return logins;
    }

    /// <summary>
    /// Cria um novo login de SQL Server Authentication (usuário e senha
    /// gerenciados pelo próprio SQL Server).
    /// <paramref name="exigirTrocaSenha"/> equivale a MUST_CHANGE (exige
    /// CHECK_EXPIRATION = ON, forçado automaticamente aqui quando
    /// marcado); <paramref name="aplicarPoliticaSenha"/>/
    /// <paramref name="aplicarExpiracaoSenha"/> equivalem a
    /// CHECK_POLICY/CHECK_EXPIRATION.
    /// </summary>
    public async Task CriarLoginSqlAsync(
        string nomeLogin, string senha, bool exigirTrocaSenha, bool aplicarPoliticaSenha, bool aplicarExpiracaoSenha,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomeLogin))
        {
            throw new ArgumentException("Informe o nome do login.", nameof(nomeLogin));
        }
        if (string.IsNullOrEmpty(senha))
        {
            throw new ArgumentException("Informe a senha do login.", nameof(senha));
        }

        var checkExpiration = aplicarExpiracaoSenha || exigirTrocaSenha; // MUST_CHANGE exige CHECK_EXPIRATION = ON
        var opcoes = $"CHECK_POLICY = {(aplicarPoliticaSenha ? "ON" : "OFF")}, CHECK_EXPIRATION = {(checkExpiration ? "ON" : "OFF")}";
        if (exigirTrocaSenha)
        {
            opcoes += ", MUST_CHANGE";
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        // Prefixo N no literal da senha: sem ele o literal é VARCHAR e o
        // servidor converte a senha para a code page padrão DELE. Uma senha com
        // acento (frequente em pt-BR: "Senh@Segur4ção") era gravada com
        // caracteres diferentes dos que o usuário digitou, e o login criado
        // simplesmente nunca aceitava a senha informada — sem nenhum erro na
        // criação. O escape de aspa simples já estava correto e continua igual.
        comando.CommandText = $"CREATE LOGIN [{IdentificadorSql.EscaparColchetes(nomeLogin)}] WITH PASSWORD = N'{IdentificadorSql.EscaparAspaSimples(senha)}', {opcoes};";
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Cria um novo login de Windows Authentication a partir de uma conta
    /// de domínio/local já existente (ex.: "DOMINIO\usuario" ou
    /// "MAQUINA\usuario") — o SQL Server não gerencia senha nesse caso, a
    /// autenticação é feita pelo Windows.
    /// </summary>
    public async Task CriarLoginWindowsAsync(string nomeLogin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomeLogin))
        {
            throw new ArgumentException(@"Informe o nome do login (ex.: DOMINIO\usuario).", nameof(nomeLogin));
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = $"CREATE LOGIN [{IdentificadorSql.EscaparColchetes(nomeLogin)}] FROM WINDOWS;";
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Lista os usuários do banco de dados informado
    /// (sys.database_principals) — exclui os principais internos do
    /// sistema (dbo, guest, sys, INFORMATION_SCHEMA — principal_id &lt;= 4)
    /// e contas de sistema (##...##). Usado para popular a grade da aba
    /// "Criar Usuário no Banco" e o combo de usuário da aba "Permissões
    /// de Banco/Tabelas e Views".
    /// </summary>
    public async Task<List<UsuarioBancoDto>> ObterUsuariosBancoAsync(string nomeBanco, CancellationToken ct = default)
    {
        var usuarios = new List<UsuarioBancoDto>();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT dp.name, sp.name AS login_name, dp.type_desc
            FROM sys.database_principals dp
            LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid AND dp.sid <> 0x
            WHERE dp.type IN ('S', 'U', 'G') AND dp.principal_id > 4 AND dp.name NOT LIKE '##%'
            ORDER BY dp.name;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            usuarios.Add(new UsuarioBancoDto
            {
                Nome = leitor.GetString(0),
                LoginAssociado = leitor.IsDBNull(1) ? null : leitor.GetString(1),
                TipoUsuario = leitor.GetString(2)
            });
        }

        return usuarios;
    }

    /// <summary>
    /// Cria um usuário no banco de dados informado, vinculado a um login
    /// já existente no servidor (CREATE USER ... FOR LOGIN ...). Se
    /// <paramref name="nomeUsuario"/> vier em branco, usa o próprio nome
    /// do login (removendo o prefixo "DOMINIO\" de logins do Windows, já
    /// que o SQL Server não aceita "\" em nome de usuário do banco).
    /// </summary>
    public async Task CriarUsuarioBancoAsync(string nomeBanco, string nomeLogin, string? nomeUsuario, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomeBanco))
        {
            throw new ArgumentException("Selecione o banco de dados.", nameof(nomeBanco));
        }
        if (string.IsNullOrWhiteSpace(nomeLogin))
        {
            throw new ArgumentException("Selecione o login.", nameof(nomeLogin));
        }

        var usuarioFinal = string.IsNullOrWhiteSpace(nomeUsuario)
            ? nomeLogin[(nomeLogin.LastIndexOf('\\') + 1)..]
            : nomeUsuario.Trim();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = $"CREATE USER [{IdentificadorSql.EscaparColchetes(usuarioFinal)}] FOR LOGIN [{IdentificadorSql.EscaparColchetes(nomeLogin)}];";
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Monta a grade de papéis fixos de SERVIDOR
    /// (<see cref="PapeisServidorFixos"/>) para o login informado, com a
    /// situação atual de cada um (IS_SRVROLEMEMBER) — usado pela aba
    /// "Permissões de Servidor".
    /// </summary>
    public async Task<List<PapelDto>> ObterPapeisServidorAsync(string nomeLogin, CancellationToken ct = default)
    {
        var papeis = PapeisServidorFixos.Select(p => new PapelDto { Nome = p.Papel, Descricao = p.Descricao }).ToList();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        foreach (var papel in papeis)
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = "SELECT IS_SRVROLEMEMBER(@papel, @login);";
            comando.Parameters.Add("@papel", SqlDbType.NVarChar, 128).Value = papel.Nome;
            comando.Parameters.Add("@login", SqlDbType.NVarChar, 128).Value = nomeLogin;
            var resultado = await comando.ExecuteScalarAsync(ct);
            papel.Membro = resultado is int valor && valor == 1;
        }

        return papeis;
    }

    /// <summary>
    /// Aplica as alterações de papéis fixos de SERVIDOR — compara com a
    /// situação atual (evita erro do SQL Server ao tentar ADD MEMBER de
    /// quem já é membro, ou DROP MEMBER de quem não é) e só executa
    /// ALTER SERVER ROLE para os papéis que realmente mudaram. Ignora
    /// silenciosamente qualquer tentativa de alterar "public".
    /// </summary>
    public async Task AtualizarPapeisServidorAsync(string nomeLogin, IEnumerable<PapelDto> papeisDesejados, CancellationToken ct = default)
    {
        var atuais = (await ObterPapeisServidorAsync(nomeLogin, ct)).ToDictionary(p => p.Nome, p => p.Membro);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        foreach (var desejado in papeisDesejados)
        {
            if (desejado.Nome == "public")
            {
                continue;
            }
            if (!atuais.TryGetValue(desejado.Nome, out var jaEhMembro) || jaEhMembro == desejado.Membro)
            {
                continue;
            }

            await using SqlCommand comando = conexao.CreateCommand();
            var acao = desejado.Membro ? "ADD" : "DROP";
            comando.CommandText = $"ALTER SERVER ROLE [{IdentificadorSql.EscaparColchetes(desejado.Nome)}] {acao} MEMBER [{IdentificadorSql.EscaparColchetes(nomeLogin)}];";
            await comando.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Monta a grade de permissões granulares de SERVIDOR
    /// (<see cref="PermissoesServidorGranulares"/>) para o login
    /// informado — <see cref="PermissaoDto.Estado"/> vem `true` (GRANT),
    /// `false` (DENY) ou `null` (não definida) a partir de
    /// sys.server_permissions.
    /// </summary>
    public async Task<List<PermissaoDto>> ObterPermissoesServidorGranularesAsync(string nomeLogin, CancellationToken ct = default)
    {
        var permissoes = PermissoesServidorGranulares
            .Select(p => new PermissaoDto { Nome = p.Permissao, Descricao = p.Descricao })
            .ToDictionary(p => p.Nome);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT perm.permission_name, perm.state
            FROM sys.server_permissions perm
            WHERE perm.grantee_principal_id = SUSER_ID(@login);";
        comando.Parameters.Add("@login", SqlDbType.NVarChar, 128).Value = nomeLogin;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            if (permissoes.TryGetValue(leitor.GetString(0), out var permissao))
            {
                permissao.Estado = leitor.GetString(1) == "D" ? false : true;
            }
        }

        return permissoes.Values.ToList();
    }

    /// <summary>
    /// Aplica GRANT ("Estado" true), DENY ("Estado" false) ou REVOKE
    /// ("Estado" null, remove qualquer GRANT/DENY existente) para cada
    /// permissão granular de SERVIDOR da lista, no login informado.
    /// </summary>
    public async Task AtualizarPermissoesServidorGranularesAsync(string nomeLogin, IEnumerable<PermissaoDto> permissoes, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        foreach (var permissao in permissoes)
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = permissao.Estado switch
            {
                true => $"GRANT {permissao.Nome} TO [{IdentificadorSql.EscaparColchetes(nomeLogin)}];",
                false => $"DENY {permissao.Nome} TO [{IdentificadorSql.EscaparColchetes(nomeLogin)}];",
                null => $"REVOKE {permissao.Nome} FROM [{IdentificadorSql.EscaparColchetes(nomeLogin)}];"
            };
            await comando.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Monta a grade de papéis fixos de BANCO DE DADOS
    /// (<see cref="PapeisBancoFixos"/>) para o usuário e banco informados
    /// — mesma lógica de <see cref="ObterPapeisServidorAsync"/>, em nível
    /// de banco (IS_ROLEMEMBER).
    /// </summary>
    public async Task<List<PapelDto>> ObterPapeisBancoAsync(string nomeBanco, string nomeUsuario, CancellationToken ct = default)
    {
        var papeis = PapeisBancoFixos.Select(p => new PapelDto { Nome = p.Papel, Descricao = p.Descricao }).ToList();

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        foreach (var papel in papeis)
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = "SELECT IS_ROLEMEMBER(@papel, @usuario);";
            comando.Parameters.Add("@papel", SqlDbType.NVarChar, 128).Value = papel.Nome;
            comando.Parameters.Add("@usuario", SqlDbType.NVarChar, 128).Value = nomeUsuario;
            var resultado = await comando.ExecuteScalarAsync(ct);
            papel.Membro = resultado is int valor && valor == 1;
        }

        return papeis;
    }

    /// <summary>
    /// Aplica as alterações de papéis fixos de BANCO DE DADOS — mesma
    /// lógica de <see cref="AtualizarPapeisServidorAsync"/>, em nível de
    /// banco (ALTER ROLE ... ADD/DROP MEMBER).
    /// </summary>
    public async Task AtualizarPapeisBancoAsync(string nomeBanco, string nomeUsuario, IEnumerable<PapelDto> papeisDesejados, CancellationToken ct = default)
    {
        var atuais = (await ObterPapeisBancoAsync(nomeBanco, nomeUsuario, ct)).ToDictionary(p => p.Nome, p => p.Membro);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        foreach (var desejado in papeisDesejados)
        {
            if (desejado.Nome == "public")
            {
                continue;
            }
            if (!atuais.TryGetValue(desejado.Nome, out var jaEhMembro) || jaEhMembro == desejado.Membro)
            {
                continue;
            }

            await using SqlCommand comando = conexao.CreateCommand();
            var acao = desejado.Membro ? "ADD" : "DROP";
            comando.CommandText = $"ALTER ROLE [{IdentificadorSql.EscaparColchetes(desejado.Nome)}] {acao} MEMBER [{IdentificadorSql.EscaparColchetes(nomeUsuario)}];";
            await comando.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Monta a grade de permissões granulares de BANCO DE DADOS
    /// (<see cref="PermissoesBancoGranulares"/>) para o usuário e banco
    /// informados — mesma lógica de
    /// <see cref="ObterPermissoesServidorGranularesAsync"/>, em nível de
    /// banco (sys.database_permissions, class = 0 = permissão de escopo
    /// do banco inteiro).
    /// </summary>
    public async Task<List<PermissaoDto>> ObterPermissoesBancoGranularesAsync(string nomeBanco, string nomeUsuario, CancellationToken ct = default)
    {
        var permissoes = PermissoesBancoGranulares
            .Select(p => new PermissaoDto { Nome = p.Permissao, Descricao = p.Descricao })
            .ToDictionary(p => p.Nome);

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT perm.permission_name, perm.state
            FROM sys.database_permissions perm
            WHERE perm.class = 0 AND perm.grantee_principal_id = DATABASE_PRINCIPAL_ID(@usuario);";
        comando.Parameters.Add("@usuario", SqlDbType.NVarChar, 128).Value = nomeUsuario;

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            if (permissoes.TryGetValue(leitor.GetString(0), out var permissao))
            {
                permissao.Estado = leitor.GetString(1) == "D" ? false : true;
            }
        }

        return permissoes.Values.ToList();
    }

    /// <summary>
    /// Aplica GRANT/DENY/REVOKE das permissões granulares de BANCO DE
    /// DADOS — mesma lógica de
    /// <see cref="AtualizarPermissoesServidorGranularesAsync"/>, em nível
    /// de banco.
    /// </summary>
    public async Task AtualizarPermissoesBancoGranularesAsync(string nomeBanco, string nomeUsuario, IEnumerable<PermissaoDto> permissoes, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        foreach (var permissao in permissoes)
        {
            await using SqlCommand comando = conexao.CreateCommand();
            comando.CommandText = permissao.Estado switch
            {
                true => $"GRANT {permissao.Nome} TO [{IdentificadorSql.EscaparColchetes(nomeUsuario)}];",
                false => $"DENY {permissao.Nome} TO [{IdentificadorSql.EscaparColchetes(nomeUsuario)}];",
                null => $"REVOKE {permissao.Nome} FROM [{IdentificadorSql.EscaparColchetes(nomeUsuario)}];"
            };
            await comando.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Monta a grade de permissões de TABELAS e VIEWS (SELECT/INSERT/
    /// UPDATE/DELETE) do banco informado, para o usuário informado — uma
    /// linha por tabela/view do banco (sys.objects, tipos 'U'/'V'), com o
    /// estado atual de cada permissão via sys.database_permissions
    /// (class = 1 = permissão em objeto, minor_id = 0 → permissão no
    /// OBJETO inteiro, não numa coluna específica).
    /// </summary>
    public async Task<List<PermissaoObjetoDto>> ObterPermissoesObjetosAsync(string nomeBanco, string nomeUsuario, CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        var objetosPorId = new Dictionary<int, PermissaoObjetoDto>();

        await using (SqlCommand comandoObjetos = conexao.CreateCommand())
        {
            comandoObjetos.CommandText = @"
                SELECT o.object_id, s.name AS esquema, o.name AS objeto, o.type
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('U', 'V')
                ORDER BY s.name, o.name;";

            await using SqlDataReader leitorObjetos = await comandoObjetos.ExecuteReaderAsync(ct);
            while (await leitorObjetos.ReadAsync(ct))
            {
                objetosPorId[leitorObjetos.GetInt32(0)] = new PermissaoObjetoDto
                {
                    Esquema = leitorObjetos.GetString(1),
                    NomeObjeto = leitorObjetos.GetString(2),
                    TipoObjeto = leitorObjetos.GetString(3).Trim() == "U" ? "Tabela" : "View"
                };
            }
        }

        await using (SqlCommand comandoPermissoes = conexao.CreateCommand())
        {
            comandoPermissoes.CommandText = @"
                SELECT major_id, permission_name, state
                FROM sys.database_permissions
                WHERE class = 1 AND minor_id = 0 AND grantee_principal_id = DATABASE_PRINCIPAL_ID(@usuario);";
            comandoPermissoes.Parameters.Add("@usuario", SqlDbType.NVarChar, 128).Value = nomeUsuario;

            await using SqlDataReader leitorPermissoes = await comandoPermissoes.ExecuteReaderAsync(ct);
            while (await leitorPermissoes.ReadAsync(ct))
            {
                if (!objetosPorId.TryGetValue(leitorPermissoes.GetInt32(0), out var objeto))
                {
                    continue;
                }

                bool estado = leitorPermissoes.GetString(2) != "D";
                switch (leitorPermissoes.GetString(1))
                {
                    case "SELECT": objeto.Select = estado; break;
                    case "INSERT": objeto.Insert = estado; break;
                    case "UPDATE": objeto.Update = estado; break;
                    case "DELETE": objeto.Delete = estado; break;
                }
            }
        }

        return objetosPorId.Values.ToList();
    }

    /// <summary>
    /// Aplica GRANT/DENY/REVOKE de SELECT/INSERT/UPDATE/DELETE em cada
    /// tabela/view da grade, para o usuário informado — construído como
    /// um único lote (todas as instruções concatenadas numa só
    /// execução) em vez de uma chamada por permissão, já que um banco
    /// pode ter centenas de tabelas/views.
    /// </summary>
    public async Task AtualizarPermissoesObjetosAsync(string nomeBanco, string nomeUsuario, IEnumerable<PermissaoObjetoDto> objetos, CancellationToken ct = default)
    {
        var lote = new StringBuilder();
        var usuarioEscapado = IdentificadorSql.EscaparColchetes(nomeUsuario);

        foreach (var objeto in objetos)
        {
            var objetoQualificado = $"[{IdentificadorSql.EscaparColchetes(objeto.Esquema)}].[{IdentificadorSql.EscaparColchetes(objeto.NomeObjeto)}]";
            AdicionarComandoPermissaoObjeto(lote, "SELECT", objeto.Select, objetoQualificado, usuarioEscapado);
            AdicionarComandoPermissaoObjeto(lote, "INSERT", objeto.Insert, objetoQualificado, usuarioEscapado);
            AdicionarComandoPermissaoObjeto(lote, "UPDATE", objeto.Update, objetoQualificado, usuarioEscapado);
            AdicionarComandoPermissaoObjeto(lote, "DELETE", objeto.Delete, objetoQualificado, usuarioEscapado);
        }

        if (lote.Length == 0)
        {
            return;
        }

        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        MudarBancoSeInformado(conexao, nomeBanco);

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandTimeout = 120;
        comando.CommandText = lote.ToString();
        await comando.ExecuteNonQueryAsync(ct);
    }

    private static void AdicionarComandoPermissaoObjeto(StringBuilder lote, string permissao, bool? estado, string objetoQualificado, string usuarioEscapado)
    {
        var comando = estado switch
        {
            true => $"GRANT {permissao} ON {objetoQualificado} TO [{usuarioEscapado}];",
            false => $"DENY {permissao} ON {objetoQualificado} TO [{usuarioEscapado}];",
            null => $"REVOKE {permissao} ON {objetoQualificado} FROM [{usuarioEscapado}];"
        };
        lote.AppendLine(comando);
    }
}
