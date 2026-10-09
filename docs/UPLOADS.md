# Upload Seguro e Ingestão de Arquivos (Prompt 08)

Fluxo de recepção de arquivos do Portal Base (tela **Dados → Arquivos**, `/dados/arquivos`).
Princípio: **fail-closed** — todo controle obrigatório ausente **rejeita** o upload; nunca há
aceitação silenciosa. Este documento descreve o que está **implementado**. O processamento do
conteúdo é específico de cada domínio e **não** faz parte do template — aqui só se **recebe, valida
e guarda com segurança** o arquivo para processamento posterior.

## Fluxo (ordem obrigatória)

O caso de uso [`IngerirArquivo`](../src/PortalBase.Application/Ingestao/IngerirArquivo.cs) executa,
NESTA ordem, reutilizando os controles já existentes:

1. **TenantContext autorizado** (regra 5): sem `ITenantContext.EstaAutorizado`, lança
   `TenantContextAusenteException` — nenhuma operação de dado do tenant ocorre.
2. **Autorização server-side** (regras 3, 15): exige a permissão `dados.importar` **no tenant**
   (`IServicoAutorizacao.ExigirAsync`), deny-by-default e auditada.
3. **Nome/extensão derivados no servidor** (regra 2): o nome do cliente é apenas um rótulo
   sanitizado (`GuardaCaminho.SanitizarNomeExibicao`); a extensão é extraída no servidor
   (`GuardaCaminho.ExtrairExtensao`) — nunca se confia em `Content-Type`/nome/extensão do cliente.
4. **Allowlist de extensão** (`Upload:ExtensoesPermitidas`) — nunca denylist.
5. **Streaming para área temporária privada** com **hash SHA-256** e **limite de tamanho**
   aplicado incrementalmente (`IncrementalHash` + contagem de bytes; corta acima de
   `Upload:MaxTamanhoBytes`). Não carrega o arquivo em memória.
6. **Validação do CONTEÚDO real** (magic bytes / estrutura), independente da extensão — ver abaixo.
7. **Idempotência tenant-scoped por hash** (regra 12): `ExisteComHashAsync` no banco do tenant;
   segunda barreira é o índice **único** `ux_arquivo_sha256`.
8. **Antivírus fail-closed** (seção 14): se obrigatório e indisponível/erro, **rejeita** — nunca
   "considera limpo"; se infectado, rejeita e registra o metadado bloqueando reenvio.
9. **Armazenamento em área privada tenant-scoped** (nome **gerado** pelo servidor, fora de
   `wwwroot`), caminho `{tenantId:N}/{nomeSeguro}`.
10. **Metadados no banco do TENANT** (nunca no catálogo; nunca o conteúdo).
11. **Auditoria** de cada desfecho (`UploadStarted/Stored/Rejected/Duplicated/Infected/Failed`).

Resultado: `ResultadoIngestao { Aceito(id) | Duplicado | Rejeitado(motivo genérico) }`.

## Validação de conteúdo real

[`ValidadorConteudoArquivo`](../src/PortalBase.Infrastructure/Ingestao/ValidadorConteudoArquivo.cs)
confere a **assinatura** (magic bytes) coerente com a extensão e, para contêineres ZIP/OpenXML:

- **xlsx** — precisa ser ZIP (`PK\x03\x04`) e conter `[Content_Types].xml`; aplica defesas de
  **ZIP bomb** (tamanho descompactado e **razão de compressão** por entrada), **excesso de
  entradas** e **Zip Slip** (caminhos com `..`/absolutos/`:` são rejeitados). Nunca extrai o
  conteúdo — inspeciona apenas o diretório central.
- **csv/txt** — rejeita binários disfarçados (assinaturas ZIP/OLE/PDF/ELF/gzip/MZ) e **byte NUL**.
- **xml** — precisa ser **bem-formado**, lido com o parser endurecido do Prompt 07 (`XmlSeguro`:
  DTD proibido, sem resolver externo, sem expansão de entidades) — sem XXE.

## Armazenamento privado

[`ArmazenamentoArquivosFilesystem`](../src/PortalBase.Infrastructure/Ingestao/ArmazenamentoArquivosFilesystem.cs)
grava em **diretório privado** (`Armazenamento:DiretorioRaiz`), **nunca** em `wwwroot` e **nunca**
servido estaticamente. Cada segmento do caminho é validado contra **path traversal**
(`GuardaCaminho.CombinarDentroDe`) e o resultado é garantido **dentro** da raiz; caminhos
absolutos/`..` são rejeitados cedo. O nome físico é **gerado** pelo servidor
(`GuardaCaminho.GerarNome`), nunca o do usuário. **Não há endpoint de download público** do binário.

A área temporária ([`AreaTrabalhoTemporaria`](../src/PortalBase.Infrastructure/Ingestao/AreaTrabalhoTemporaria.cs))
usa `FileOptions.DeleteOnClose` — o arquivo temporário é apagado ao descartar o stream, inclusive
em falha.

## Antivírus (fail-closed)

O contrato é `IScannerAntivirus`. O motor é escolhido em `Antivirus:Motor`
(`Automatico` — padrão —, `WindowsDefender` ou `Nenhum`; valor inválido falha a inicialização):

- **Windows Defender**
  ([`ScannerAntivirusWindowsDefender`](../src/PortalBase.Infrastructure/Ingestao/ScannerAntivirusWindowsDefender.cs)) —
  usado no servidor Windows (ou quando configurado). Varre uma cópia privada com
  `MpCmdRun.exe -Scan -ScanType 3 -File <arquivo> -DisableRemediation`, com tempo máximo
  (`Antivirus:TimeoutSegundos`), nova tentativa controlada (`Antivirus:Tentativas`), limite de
  varreduras simultâneas (`Antivirus:MaxVarredurasSimultaneas`) e `CancellationToken`. Só o código
  de saída 0 é "limpo"; qualquer outro resultado, tempo esgotado ou falha ao iniciar é erro.
- **Nenhum motor**
  ([`ScannerAntivirusIndisponivel`](../src/PortalBase.Infrastructure/Ingestao/ScannerAntivirusIndisponivel.cs)) —
  reporta `Disponivel = false` (ex.: Linux/dev sem motor).

Como `Upload:AntivirusObrigatorio` é **true** por padrão, o upload é **rejeitado** quando o scanner
está indisponível ou falha. Integrar outro motor (ex.: ICAP/ClamAV) é trocar a implementação por
DI, sem alterar o caso de uso. O antivírus **nunca** registra o conteúdo nem o nome do arquivo.

## Metadados no banco do tenant

[`ArquivoImportacao`](../src/PortalBase.Domain/Arquivos/ArquivoImportacao.cs) é um agregado
encapsulado no **banco do próprio tenant** (nunca no catálogo). Guarda apenas metadados —
**nunca** o conteúdo. Máquina de estados server-side: `Recebido → Armazenado`/`Rejeitado`;
`PodeProcessar` só quando `Armazenado` **e** scan `Limpo`. O `Sha256` é **único** no banco do
tenant. O repositório
([`RepositorioArquivoImportacao`](../src/PortalBase.Infrastructure/Ingestao/RepositorioArquivoImportacao.cs))
obtém o `DbContext` **somente** pela fábrica fail-closed a partir do `ITenantContext` autorizado, e
traduz o erro 1062 (unicidade) do MySQL em `ViolacaoUnicidadeException`.

## Endpoint Web

`POST /arquivos/importar`
([`EndpointsUpload`](../src/PortalBase.Web/Seguranca/EndpointsUpload.cs)): **autenticado**,
**antiforgery** validado, **rate limiting** (`rate-upload`). O tenant é **resolvido no servidor**
(`SelecaoTenant` → `IResolvedorTenant`) a partir do principal autenticado — o `tenantId` do
formulário é apenas o alvo pretendido, validado contra o vínculo (nunca define a base
diretamente). O corpo da requisição é limitado pelo Kestrel (`Seguranca:MaxTamanhoRequisicaoBytes`,
padrão 20 MB) — baseline antes mesmo do caso de uso. Respostas são **genéricas** (sem detalhes
internos): `202` aceito, `200` duplicado, `422` rejeitado, `403` sem autorização.

## Configuração

| Chave | Uso |
|---|---|
| `Upload:MaxTamanhoBytes` | Tamanho máximo do arquivo (bytes). Padrão 20 MB. |
| `Upload:ExtensoesPermitidas` | Allowlist de extensões (ex.: `xlsx,csv,xml,txt`). |
| `Upload:AntivirusObrigatorio` | `true` (padrão) = rejeita se o scanner estiver indisponível. |
| `Antivirus:Motor` | `Automatico` (padrão: Windows Defender no servidor Windows, nenhum motor nos demais), `WindowsDefender` ou `Nenhum`. |
| `Upload:MaxEntradasZip` | Máx. de entradas em contêiner ZIP/OpenXML. |
| `Upload:MaxTamanhoDescompactadoBytes` | Tamanho descompactado total máximo. |
| `Upload:RazaoMaximaCompressao` | Razão máxima de compressão por entrada (anti ZIP bomb). |
| `Armazenamento:DiretorioRaiz` | Diretório privado dos uploads (fora de wwwroot). **Obrigatório em Produção** (fail-closed). |
| `Armazenamento:DiretorioTemporario` | Diretório de trabalho temporário (padrão: temp do SO). |

Nenhum valor de upload é segredo. Nenhum segredo, connection string ou conteúdo de arquivo é
gravado em log (regras 7, 8).

## Retenção

Arquivos de importação: **5 anos** (`CategoriaRetencao.ArquivosImportacao`), calculado por
`IPoliticaRetencao` e persistido em `ExpiraEmUtc`. Retenção **não** implica exclusão automática ao
expirar — identifica o fim da obrigação legal.

## O que NÃO faz parte do template

O processamento do conteúdo dos arquivos (interpretação, regras de negócio, integrações) é
específico de cada domínio. O arquivo aceito fica `Armazenado`/`Limpo`, **pronto** para o
processamento que o seu portal implementar (por exemplo, como tarefa de segundo plano — ver
[OBSERVABILIDADE-OPERACOES.md](OBSERVABILIDADE-OPERACOES.md)).
