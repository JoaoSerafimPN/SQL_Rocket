# SQL Rocket

Ferramenta desktop de apoio à manutenção de instâncias SQL Server — reúne em um
só lugar tarefas de monitoramento, desempenho, índices, collation, segurança
(logins/usuários/permissões), backup/restore, comparação de bancos e utilitários de
manutenção (limpeza de arquivos, análise offline de deadlock/blocked process, análise de
plano de execução, Profiler via Extended Events).

**Versão 1.0**  — Todas as funcionalidades abaixo já estão implementadas de ponta a ponta.

## Documentação
- [`docs/Manual_do_Usuario_SQL_Rocket.pdf`]— para quem for usar o programa: como conectar e
  como usar cada tela, em linguagem simples.

## Funcionalidades

- **Informações** — SQL (versão/edição/collation/instância) e Servidor (nome, IP,
  memória, processador, discos, versão do Windows, domínio).
- **Índice** — Fragmentação (rebuild/reorganize individual ou em lote por limiar,
  atualização de estatísticas); nunca utilizados, sugeridos e mais utilizados.
- **Desempenho** — Top N consultas mais custosas (15/25/50/100), com plano de execução por
  consulta (texto ou aberto graficamente no SSMS/Visual Studio).
- **Monitoramento** — Active Monitor SQL Local e Azure SQL, com processos, bloqueios e
  métricas atualizados automaticamente.
- **Collation** — Collation Manager: consulta a collation do banco, do servidor e de cada
  coluna, e altera a collation do banco de dados ou de colunas de texto, com recriação
  automática de índice quando necessário.
- **Segurança** — log de erros do SQL Server; criação de login (SQL Server ou Windows
  Authentication), criação de usuário no banco, e permissões de servidor e de banco de
  dados (papéis fixos e granulares, com tabelas/views geridas separadamente).
- **Backup** — Full e Restore, com mensagens do SQL Server em tempo real.
- **Comparador** — Índices e estrutura de banco de dados entre dois bancos (mesma
  instância ou servidores diferentes), com aplicação das diferenças em qualquer sentido.
- **Manutenção** — Limpeza de Arquivos (shrink, maiores tabelas, limpeza em lote por data
  ou chave primária); Headblock e DeadLock (análise offline de `.xel`/`.xml`); Análise do
  plano de execução (análise offline de `.sqlplan`, com diagrama visual); Profiler (captura
  ao vivo via Extended Events e análise offline de `.xel`).

## Estrutura do repositório

Solução com dois projetos .NET 8, separando estritamente UI de regra de negócio:

```
FerramentasDBA.sln
src/
├── FerramentasDBA.Classes/   (net8.0 — class library, SEM referência a WinForms)
│   ├── Infraestrutura/       (conexão SQL, carregamento de scripts embutidos)
│   ├── Modulos/              (uma classe por área de negócio — Admin, Índices, etc.)
│   ├── Models/                (DTOs de retorno)
│   └── Scripts/               (convenção para .sql embutidos, ver README interno)
└── FerramentasDBA.UI/         (net8.0-windows — Windows Forms)
    ├── Assets/                 (ícone, logo, imagem de fundo)
    ├── Controls/                (menu lateral, gráficos, diagrama de plano de execução)
    └── Forms/                   (tela de login e janela principal)
docs/
└── Manual_do_Usuario_SQL_Rocket.pdf
```

## Como compilar

Requer .NET 8 SDK e (para rodar) Windows, já que a UI é WinForms.

```powershell
dotnet build FerramentasDBA.sln -c Release
```

O projeto `FerramentasDBA.UI` é o executável (`OutputType=WinExe`); `FerramentasDBA.Classes`
é a biblioteca de regra de negócio referenciada por ele.

## Instaladores

Pode ser utilizado os instalador [`installer/SQLRocket-Setup.msi`] caso não tenha o visual studio
