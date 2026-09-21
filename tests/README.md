# Testes de unidade — SQL Rocket

Testes automatizados da camada de lógica (`FerramentasDBA.Classes`), escritos
em [xUnit](https://xunit.net/).

## Como rodar

Na raiz do repositório:

```
dotnet test
```

Para rodar só este projeto:

```
dotnet test tests/FerramentasDBA.Classes.Tests/FerramentasDBA.Classes.Tests.csproj
```

No Visual Studio, os testes aparecem no **Gerenciador de Testes**
(Test Explorer) depois de compilar a solução.

Estes mesmos testes rodam sozinhos a cada push e a cada pull request, pelo
workflow `.github/workflows/build.yml` — ver o comentário no topo daquele
arquivo.

## O que é coberto

Só lógica **pura e offline**, que dá para verificar sem servidor nenhum:

- `Infraestrutura/IdentificadorSql` — montagem e escape de identificadores
  T-SQL (é o que decide em qual tabela um DELETE vai bater).
- `Modulos/Modulo_PlanoExecucao.AnalisarXmlBruto` — análise do `.sqlplan`,
  inclusive a guarda de profundidade da árvore de operadores.
- `Modulos/Modulo_Deadlock.AnalisarArquivoXmlAsync` — leitura de deadlock
  graph e de blocked process report (cada teste escreve seu próprio XML num
  arquivo temporário e o apaga em seguida).
- `Modulos/Modulo_Profiler` — `MontarDdlCriarSessao` e `FiltrarEventos`.
- `Modulos/Modulo_Admin.SuportaCompressaoDeBackup`.
- `Modulos/Modulo_Manutencao.EhTipoNumericoInteiro`.
- `Models/PlanoIndiceFaltanteDto.ScriptCriacao` — o texto que a ferramenta
  executa ao criar um índice sugerido.

## O que NÃO é coberto (de propósito)

- **Qualquer coisa que precise de um SQL Server vivo**: backup, restore,
  shrink, criação de índice, leitura de DMVs, alteração de collation, logins
  e permissões. Testar isso exigiria uma instância real (ou um banco de
  testes dedicado), o que está fora do alcance deste projeto de testes.
- **Leitura de arquivos `.xel`** (Extended Events, via `XELite`): depende de
  um arquivo binário de exemplo, que teria de ser versionado no repositório.
- **Toda a camada de interface** (`FerramentasDBA.UI`): Forms, controles e
  desenho de tela não são testados aqui. A regra do projeto é que nenhuma
  lógica de negócio viva na UI — quando algo da tela merecer teste, o
  caminho é mover essa lógica para `FerramentasDBA.Classes` e testá-la aqui.

## Convenções

- Nomes de teste em português, no formato
  `Metodo_Situacao_ComportamentoEsperado`
  (ex.: `Qualificar_ComEsquema_DevolveNomeEmDuasPartes`).
- Um comentário `// SUSPEITA:` marca um teste que registra o comportamento
  ATUAL do código embora ele pareça errado. O teste não é uma aprovação
  daquele comportamento: é um lembrete de uma decisão pendente. Ao corrigir
  o código, o teste correspondente muda junto e o comentário sai.
