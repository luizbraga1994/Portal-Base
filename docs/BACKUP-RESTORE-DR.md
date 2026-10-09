# Backup / Restore / Disaster Recovery — Portal Base v2.0 (Prompt 50)

Estratégia, implementação e prova de **backup, restore e DR** sobre a arquitetura real do sistema
(**CATALOG DB + DATABASE-PER-TENANT** + storage privado de arquivos + cofre de segredos). Regra
fundamental que atravessa todo o documento:

> **BACKUP ≠ RESTORE.** Ter o arquivo de backup não é ser capaz de restaurar o sistema. Nenhum backup é
> considerado confiável sem **integridade** (hash), **teste de restauração** e **validação de conteúdo**.

## 1. Arquitetura

```
                         ┌── CATALOG DB (portalbase_catalogo)     ── backup CatalogoCompleto
TENANT (server-side) ────┤
                         ├── TENANT DB (portalbase_tenant_<nome>) ── backup TenantCompleto  (A→Backup A)
                         └── STORAGE privado (arquivos)           ── backup Storage          (por tenant)

SECRETS (cofre) ── NÃO entram nos backups: só a REFERÊNCIA (nome no cofre) é preservada. O cofre tem cópia
                   própria: pacote exportado e protegido por senha (cofre-windows.ps1 -Acao Exportar — BKP-001).
```

- **Catálogo de backups** (`registro_backup`, no CATALOG DB): metadados auditáveis de cada artefato.
- **Store de artefatos** (`ArmazenamentoBackupFilesystem`): failure domain separado do banco (Fase 25).
- **Cifra em repouso** (`CriptografiaBackupAesGcm`): AES-256-GCM, chave do cofre por referência.
- **Executor físico** (`IExecutorBackupBanco`): dois provedores atrás do MESMO porto — `ExecutorBackupBancoMySql`
  (real, `mysqldump`/`mysql`, **opt-in** por `Backup:ExecutorMySql:Habilitado`) e `ExecutorBackupIndisponivel`
  (default **fail-closed**, BLOCKED sem MySQL/ferramenta). O real usa `ArgumentList` (nunca shell), credencial do
  cofre por `--defaults-extra-file` 0600 (**nunca** senha na linha de comando), timeout/cancelamento, exit-code e
  stderr sanitizado. Restore SEMPRE em base temporária. **Provado fisicamente** (P50) contra MySQL real.
- **Orquestração**: `ServicoBackup` (gerar) e `ServicoRestauracao` (restaurar/testar), fail-closed.

## 2. Catalog DB (o que entra)

Backup completo do banco de catálogo: tenants, ciclo de vida, usuários/papéis/permissões/vínculos e
perfis personalizados, códigos/tokens de recuperação (só hash), trilha de auditoria, incidentes de
segurança, configuração do e-mail de disparo (sem a senha), marca da plataforma, saúde persistida dos
workers, tarefas em segundo plano e o próprio catálogo de backups. **Sem segredos.**

## 3. Tenant DB (o que entra)

Backup completo da base física de cada contribuinte: no template, os metadados dos arquivos importados
(`arquivo_importacao`), a marca/logo do contribuinte (`marca_visual`), a marca de proprietário
(`portalbase_proprietario`) e o histórico de migrations — e, quando você acrescentar tabelas do seu
domínio, elas também (o dump é da base inteira). Configurações de integração entram **só com campos
não-secretos**. **Dados de A jamais no backup de B** (Fase 5/29).

## 4. Storage (arquivos do contribuinte)

**Implementado na Onda 2 (BKP-005, 08/10/2026).** Um artefato do tipo `Storage` por contribuinte pronto, todo dia:
`EmpacotadorArquivosTenantZip` gera um ZIP só com a pasta DELE — os arquivos enviados pela tela Dados → Arquivos
(`<Armazenamento:DiretorioRaiz>/<tenant>/` → entradas `arquivos/…`; se o seu portal guardar arquivos do contribuinte
em outra pasta, acrescente-a no empacotador) — e o ZIP segue o mesmo pipeline (cifra, hash, store, registro, cópia externa, retenção). Links
simbólicos/junções são pulados (nada fora da pasta do contribuinte entra). **Nunca** faz backup de arquivo secreto
(chave privada/PFX) como arquivo comum (Fase 24) — o material sensível vive no cofre.

## 5. Backup (mecanismo)

`ServicoBackup` autoriza a operação de **plataforma** (nunca usuário comum), lista os tenants **backup-ready**
(Pronto/Ativo), e por alvo executa o pipeline: **dump → cifra → hash SHA-256 → grava no store → registra
no catálogo → audita**. Nomenclatura versionada/timestamped: `{tipo}/{tenant|catalogo}/{UTC}-{backupId}.bak[.enc]`.
Falha de um tenant é registrada e **não aborta** os demais (isolamento). Backup físico sem executor habilitado
(ou sem MySQL/ferramenta): **BLOCKED** (fail-closed). Com `Backup:ExecutorMySql:Habilitado=true` + MySQL, o dump
físico real (`mysqldump --single-transaction …`) é gerado e cifrado.

## 6. Retenção

`PoliticaRetencaoBackup` — **dirigida por configuração** (`Backup:Retencao{Diaria,Semanal,Mensal}Dias`),
sem períodos arbitrários no código. Só descarta o que está **fora da janela**; e **nunca apaga o único
backup conhecido** de um alvo (mesmo expirado) — a última linha de defesa. Cada artefato grava
`RetencaoAteUtc` na conclusão.

**Aplicada de verdade desde a Onda 2 (BKP-004):** ao fim de cada janela do backup agendado,
`ServicoBackup.AplicarRetencaoAsync` apaga o artefato vencido do store **e** da cópia externa e marca o registro
como `Expirado` (auditoria `BackupPruned`). Do mais velho ao mais novo por alvo: se todos venceram, o mais recente
fica. Falha ao apagar (arquivo em uso, compartilhamento fora) ⇒ o registro continua `Concluido` e a próxima execução
tenta de novo — nunca um registro "expirado" com o arquivo ainda no disco.

## 7. Criptografia

AES-256-GCM (AEAD do framework — **nenhum algoritmo próprio**), streaming por blocos com nonce aleatório e
tag por bloco. A **chave (32 bytes)** é resolvida do **cofre** por `Backup:ChaveCriptografiaRef` e **nunca**
acompanha o backup (regra 7/11). O catálogo guarda apenas a **referência** (nome da chave), jamais o valor.
Rotação de chave é responsabilidade do cofre (P06). A tag detecta adulteração ⇒ corrupção falha o restore.

## 8. Integridade

Cada artefato tem **SHA-256** (algoritmo aprovado) calculado **sobre o conteúdo** (nunca sobre o filename),
mais tamanho, timestamp, origem, tenant, tipo, versão e status no catálogo. A verificação recalcula o hash
e compara em tempo constante; divergência ⇒ **CORROMPIDO** (nunca restaurar) e auditoria `BackupCorrupted`.

## 9. Restore

`ServicoRestauracao` — operação de **plataforma altamente privilegiada** (autorização + MFA quando
configurado; nunca usuário comum do tenant — Fase 30). Sempre restaura em uma **base TEMPORÁRIA** (nunca
sobrescreve produção — Fase 14/15). Antes de restaurar: **verifica hash** (Fase 26) e **rejeita cross-tenant**
(Fase 29). Um restore bem-sucedido é registrado como **teste de restauração** no catálogo (BACKUP ≠ RESTORE).

**Restauração aplicada pela tela** (`RestauracaoPlataforma`, Operações → Backups): o restore continua sendo feito
numa base temporária (a de **preparo**); só depois de conferidos o hash e a marca de proprietário as tabelas trocam
de lugar com a produção num único `RENAME TABLE` atômico, e as tabelas anteriores ficam numa base **preservada**
(nunca apagada pelo portal). Contribuinte em manutenção durante a troca; falha na troca ⇒ nada muda; falha nas
migrations ⇒ contribuinte desabilitado (fail-closed). Justificativa obrigatória e auditoria `RestoreAppliedToTenant`/
`RestoreApplyFailed`. O catálogo só é **testado** pela tela. Procedimento em
[`runbooks/DISASTER-RECOVERY.md`](runbooks/DISASTER-RECOVERY.md).

## 10. Tenant isolation

O `RegistroBackup` carrega o tenant do artefato; a restauração exige que o backup seja do **mesmo** tenant
de destino. Backup de A → Restore A: permitido; Backup de A → Restore B: **rejeitado** (`RestauracaoCrossTenantException`,
auditado). Metadados incompatíveis ⇒ fail-closed. Backup de catálogo não tem tenant e não restaura como tenant.

## 10.1 Cópia externa (offsite) — Onda 2 (BKP-003)

Com `ArmazenamentoBackup:DiretorioOffsite` (outro disco ou `\\servidor\compartilhamento`), cada artefato concluído é
copiado com nome temporário, **relido do destino e conferido pelo SHA-256** e só então recebe o nome definitivo
(`CopiaExternaBackupFilesystem`). O registro guarda `CopiaExterna` (`Concluida`/`Falhou`) e a data; a tela
Operações → Backups mostra a coluna **Cópia externa** e o Worker registra erro `CopiaExternaFalhou` em Operações →
Workers. A falha da cópia **não** desfaz o backup primário. O destino externo só vê o artefato **cifrado**.

## 10.2 Interrompidos e cancelados — Onda 2 (JOB-001)

Um backup cancelado ou com **tempo esgotado** do `mysqldump` é marcado `Falhou` (`Cancelado`/`TempoEsgotado`) na hora,
mesmo com o pedido já cancelado. Um registro que ficou `Iniciado` por mais de 6 h (processo que caiu) é marcado
`Falhou` (`Interrompido`) no início da janela seguinte, e um `Iniciado` antigo **não** conta como "janela atendida" —
antes ele fazia o dia passar sem backup.

## 11. RPO (Recovery Point Objective)

Separar **alvo** de **comprovado**:
- **RPO alvo (proposto)**: igual à periodicidade do backup completo (ex.: **24 h** com backup diário).
  Reduzível com backup incremental/binlog (Fase 17) quando a infraestrutura oferecer.
- **RPO comprovado**: **NÃO MEDIDO em produção** — depende do agendamento ligado no servidor (o
  `configurar-iis.ps1` liga desde a Onda 2) e de conferir os backups diários. Não se escolhe um número apenas para
  "fechar" o documento.

## 12. RTO (Recovery Time Objective)

Separar **objetivo** de **medido**:
- **RTO objetivo (estimativa)**: restore de catálogo + restore de tenants + recuperação de serviço/worker.
- **RTO medido**: **NÃO MEDIDO em produção.** O ensaio local da Onda 2 (MySQL 8.4 real, ~2,2 MB entre catálogo e 3
  bases) provou o caminho completo — backup cifrado → decifrar fora do portal (1,6 s) → restaurar em bases novas (3,9 s)
  → conferir (3 × 51 tabelas e os arquivos idênticos) — e o caminho "outra máquina" (só cópia externa + pacote do cofre
  + senha), mas o tempo de produção depende do volume e da instalação do servidor: meça no ensaio trimestral
  ([`runbooks/DISASTER-RECOVERY.md`](runbooks/DISASTER-RECOVERY.md), seção 8.9). Estimativa **não é** medição.

## 13. Disaster Recovery

Cenário: falha do site/instância principal → restaurar em ambiente de recovery. Dependências a validar:
banco, storage, **cofre/segredos** (recuperados do cofre, não do backup), configuração, certificado TLS do
site, DNS e serviços. **A existência de backups não implica DR**: o DR só existe quando o recovery é
**testado**. Nunca destruir produção para testar DR (Fase 34).

**Servidor perdido (Onda 2, BKP-001):** antes, as chaves ficavam só no cofre DPAPI da máquina — perdido o servidor,
nenhum backup decifrava. Agora: (1) `cofre-windows.ps1 -Acao Exportar` gera um pacote `.portalbasecofre` (PBKDF2-SHA256
600 mil iterações, AES-256-CBC + HMAC-SHA256, HMAC conferido antes de decifrar) que o `-Acao Restaurar` regrava no
cofre de outra máquina; (2) `PortalBase.Web.exe decifrar-backup` decifra qualquer artefato **sem banco**, com o cofre
do servidor, uma pasta de cofre restaurada ou direto do pacote (`--pacote-cofre`). Passo a passo:
[`runbooks/DISASTER-RECOVERY.md`](runbooks/DISASTER-RECOVERY.md), seção 8.

## 14. Testes

- **Executados (sem MySQL)**: invariantes do catálogo de backups; orquestração (isolamento A→A, pula não
  provisionado, paralelismo, bloqueio em migration, autorização, cifra+hash+registro); restore (cross-tenant
  rejeitado, nunca-produção, corrupção detectada, incompleto rejeitado, autorização/BLOCKED); storage
  roundtrip + path traversal; cifra roundtrip + chave errada + adulteração; **executor físico fail-closed**
  (desabilitado/sem credencial ⇒ indisponível) e **prova §38 de que os argumentos nunca carregam senha/usuário
  na linha de comando** (só `--defaults-extra-file`), com nome de base hostil rejeitado antes de qualquer processo.
- **Executado com MySQL real (P50, gated `PORTALBASE_BACKUP_MYSQL`)**: **roundtrip físico PROVADO** —
  `mysqldump` real → cifra AES-256-GCM → hash SHA-256 → decifra → `mysql` restaura em base **temporária** →
  consulta na base restaurada valida a **sentinela** (valor idêntico). Nunca mockado (§41); validação DEPOIS do
  restore (§40).
- **Onda 2 (08/10/2026)**: interrupção/cancelamento/tempo esgotado viram `Falhou` e não "atendem" a janela;
  retenção (vencidos saem do primário e da cópia externa, nunca o último); cópia externa conferida e falha registrada;
  backup de arquivos por contribuinte (isolado, sem seguir links); pacote do cofre (senha certa abre; errada/alterado
  falha sem revelar nada; interoperável com o PowerShell — conferido nos dois sentidos); `decifrar-backup` (dump e ZIP,
  nunca sobrescreve, artefato adulterado não deixa saída).
- **Ainda BLOCKED (infra externa)**: smoke test de produção, ensaio de DR no servidor real, medição de RTO/RPO em
  produção, PITR.

## 15. Limitações (honestas)

- **Backup/restore FÍSICO de MySQL**: executor real **implementado e provado** (P50) — em produção requer
  MySQL + `mysqldump`/`mysql` no host e `Backup:ExecutorMySql:Habilitado=true`; caso contrário **BLOCKED**
  (fail-closed via `ExecutorBackupIndisponivel`). O executor real nunca é o default.
- **PITR / binlog / incremental**: **NÃO IMPLEMENTADO** — depende da infraestrutura MySQL; não se inventa
  PITR a partir de full backups (Fase 17).
- **Imutabilidade (WORM/object lock)**: **NÃO DISPONÍVEL** no store filesystem — risco documentado (Fase 10).
- **Offsite**: implementado (Onda 2); só funciona com `ArmazenamentoBackup:DiretorioOffsite` apontando para **outro**
  disco/servidor (o `configurar-iis.ps1 -DiretorioOffsite` configura e avisa se for o mesmo disco). Sem ele, a tela
  mostra ⚠️ e os backups dividem o destino de falha com o servidor.
- **Pacote do cofre**: a segurança dele é a da **senha** (mín. 16 caracteres) e do lugar onde fica guardado; precisa
  ser refeito sempre que um segredo muda (chaves, senhas, segredos de integração) — senão a recuperação traz o
  segredo antigo.
- **RTO/RPO**: apenas **alvos**; nada **medido/comprovado** neste ambiente.
- **Migration do próprio catálogo**: detector conservador (só detecta provisionamento de tenant) — Fase 8.

## 16. Dependências externas

- **MySQL** + ferramentas (`mysqldump`/`mysql`) para backup/restore físico.
- **Cofre de segredos** (P06): chave de cifra dos backups + credenciais de acesso ao banco. Se o cofre
  precisar de DR, é uma **dependência externa** própria (o segredo é recuperado do cofre, nunca do backup).
- **Storage/offsite** em failure domain separado (idealmente outro host/volume/região).

## 17. Runbooks

- [`runbooks/DISASTER-RECOVERY.md`](runbooks/DISASTER-RECOVERY.md) — recuperação completa, ordem de restauração,
  restauração de um contribuinte pela tela e verificação de integridade dos backups.

## 18. Configuração

```jsonc
// appsettings (nunca segredos aqui — só referências)
"Backup": {
  "RetencaoDiariaDias": 7, "RetencaoSemanalDias": 35, "RetencaoMensalDias": 365,
  "ParalelismoMaximo": 2, "CriptografarEmRepouso": true,
  "ChaveCriptografiaRef": "portalbase-prod-backup-key",   // NOME no cofre, nunca o valor
  "LocalPrimario": "primario", "LocalOffsite": "",         // obsoleto: o destino externo é ArmazenamentoBackup:DiretorioOffsite
  "BloquearDuranteMigracao": true,
  "ExecutorMySql": {                        // executor FÍSICO real (opt-in; default fail-closed)
    "Habilitado": false,                    // true só onde há MySQL + ferramentas no host
    "CaminhoMysqldump": "mysqldump",        // nome no PATH ou caminho absoluto
    "CaminhoMysql": "mysql",
    "TimeoutSegundos": 600
  }
},
"ArmazenamentoBackup": { "DiretorioRaiz": "/mnt/backup-store", "DiretorioOffsite": "" }   // vazio ⇒ sem cópia externa
// Credenciais de acesso ao banco (usuário admin + senha) vêm de BancoTenant + cofre (nunca aqui).
```
