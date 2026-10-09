# CLAUDE.md — Portal Base (template)

Guia obrigatório para qualquer desenvolvimento neste repositório (humano ou
assistido por IA) e em todo portal criado a partir dele. Este é um sistema
**multi-tenant** no qual cada contribuinte (tenant) possui seu próprio banco de
dados. Erros de segurança aqui podem vazar dados entre contribuintes. Trate cada
regra abaixo como **bloqueante**: não conclua uma tarefa que viole qualquer uma
delas.

Como criar um portal novo a partir deste template e onde acrescentar o domínio:
[`docs/COMO-USAR-O-TEMPLATE.md`](docs/COMO-USAR-O-TEMPLATE.md).

O detalhamento completo (intenção, padrão correto, antipadrão e como é
verificado) está em [`docs/REGRAS-TRANSVERSAIS-OBRIGATORIAS.md`](docs/REGRAS-TRANSVERSAIS-OBRIGATORIAS.md).
Leia-o antes de escrever código.

## Leis técnicas (normas obrigatórias)

Além das regras abaixo, valem:

- **Lei 01 — [`docs/LEI-TECNICA-UNIVERSAL.md`](docs/LEI-TECNICA-UNIVERSAL.md)**
  (desenvolver corretamente: performance, volumetria, paginação, N+1, banco, filas,
  observabilidade, backup/DR, capacidade, deploy e Definition of Done);
- **Lei 02 — [`docs/LEI-02-AUDITORIA-RECUPERACAO-MODERNIZACAO.md`](docs/LEI-02-AUDITORIA-RECUPERACAO-MODERNIZACAO.md)**
  (recuperar e modernizar: auditar → mapear → medir → classificar → causa raiz →
  planejar → corrigir → testar → medir de novo; **nenhuma correção de auditoria
  antes de apresentar o relatório e obter aprovação**; correções incrementais);
- **Lei 03 — [`docs/LEI-03-AUDITORIA-CONTINUA.md`](docs/LEI-03-AUDITORIA-CONTINUA.md)**
  (manter saudável: baseline, orçamento de performance, monitoramento, dívida
  técnica, auditoria periódica, relatório e score de saúde, prevenção de regressão).

Em caso de conflito, vale a exigência mais rígida. Em toda implementação ou manutenção:

- siga as 8 etapas da regra 51 (entender → analisar → riscos → planejar →
  implementar → validar → auditar → concluir);
- responda às 10 perguntas de impacto da regra 42 no relatório da entrega
  (o que muda, quem depende, banco, API, frontend, performance, segurança,
  escalabilidade, backup, validação);
- antes de concluir, faça a auditoria da etapa 7 (sem gargalo, sem N+1, sem
  perder paginação, sem consultas a mais, sem estado local novo, sem
  comprometer segurança/backup/escalabilidade, sem regressão);
- só declare concluído com a Definition of Done (regra 50) atendida no que for
  aplicável — e diga explicitamente o que não foi possível validar;
- pela Lei 03 (regra 59): antes, verifique código, arquitetura, dependências,
  banco, consultas, performance, segurança, observabilidade, escalabilidade e
  testes existentes; depois, além dos testes, registre os riscos residuais e
  transforme todo bug importante corrigido em teste permanente (regra 37).

## Regras transversais obrigatórias (resumo)

1. **Não implementar apenas o caminho feliz.** Trate erros, nulos, listas
   vazias, concorrência, entradas inválidas e falhas de dependências.
2. **Não assumir que dados recebidos do cliente são confiáveis.** IDs,
   quantidades, flags e nomes vindos do cliente são hostis até validação.
3. **Toda autorização é validada no servidor.** A interface nunca é a fonte de
   verdade de autorização.
4. **Nenhum identificador de tenant recebido do cliente pode definir
   diretamente o banco utilizado.** O tenant é resolvido no servidor, a partir
   do principal autenticado, através de um mapa confiável.
5. **Nenhuma operação de dado do tenant ocorre sem `TenantContext` válido e
   autorizado.** Sem tenant autorizado, a operação falha (fail-closed).
6. **Nunca usar conexão padrão para banco de contribuinte.** A connection
   string é sempre resolvida a partir do tenant autorizado.
7. **Nunca armazenar segredos em código, `appsettings`, banco ou logs.**
   (senha, certificado, token, `client_secret`, chaves). Use cofre de segredos.
8. **Nunca registrar dados sensíveis em logs.** Redija/oculte PII e segredos.
9. **Toda operação crítica possui auditoria** (quem, o quê, quando, tenant,
   antes/depois quando aplicável).
10. **Toda entrada externa é validada** nas fronteiras, com falha segura.
11. **Toda integração externa possui timeout, retry controlado,
    `CancellationToken` e tratamento de falhas.**
12. **Toda operação que possa ser repetida é idempotente.**
13. **Não usar SQL concatenado.** Use consultas parametrizadas / LINQ /
    `FromSqlInterpolated`.
14. **Não usar `IgnoreQueryFilters()` para contornar regras de segurança**
    (em especial o filtro de tenant).
15. **Não confiar em autorização feita somente pela interface Blazor.** A UI
    apenas melhora a experiência; a decisão é do servidor.
16. **Não expor stack trace, connection string, token ou segredo ao usuário.**
17. **Não criar endpoint administrativo sem autorização explícita.**
18. **Não introduzir dependência arquitetural que viole as regras da solution.**
19. **Todo código novo possui testes compatíveis com seu risco.**
20. **Ao finalizar, executar build + testes e reportar exatamente o resultado.**

## Fluxo de conclusão de tarefa (regra 20)

Antes de declarar uma tarefa concluída:

```bash
dotnet build -warnaserror
dotnet test
```

Reporte o resultado **exatamente** como obtido — número de erros/avisos e de
testes passados/falhados. Não afirme sucesso sem ter executado; se não for
possível executar (ex.: SDK ausente no ambiente), diga isso explicitamente em
vez de presumir.

## Guardrails já configurados no repositório

- `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`,
  analisadores de segurança do .NET habilitados (`AnalysisModeSecurity=All`),
  `EnforceCodeStyleInBuild=true`. Aplica-se a todos os projetos criados.
- `.editorconfig`: estilo de código e severidades de analisadores mapeadas às
  regras (ex.: `CA2100` — SQL injection — como **erro**).
- `BannedSymbols.txt` (**ativo** via `Microsoft.CodeAnalysis.BannedApiAnalyzers`,
  referenciado em `Directory.Build.props`): proíbe APIs perigosas como
  `IgnoreQueryFilters` (regra 14) e `Console.Write*` (regra 8/16) — o uso falha o build.
- `Directory.Packages.props`: Central Package Management (versões centralizadas).
- `global.json`: fixa o SDK .NET 10.

## Convenções

- Idioma do domínio e da documentação: **português**.
- Git: desenvolva na branch designada; commits descritivos; nunca faça push de
  segredos. Verifique o diff antes de commitar.
- Ao criar o primeiro projeto/solution, mantenha a direção de dependências das
  camadas (Domínio ← Aplicação ← Infraestrutura/Apresentação) e não a inverta.
