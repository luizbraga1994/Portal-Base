# Runbook — DISASTER RECOVERY (recuperação de desastre)

**Objetivo:** recuperar o Portal Base após perda de um componente, preservando a integridade dos dados e o
isolamento multi-tenant (§20/§21). Estrutura por cenário: **Detect → Contain → Recover → Reconcile →
Validate**.

## Princípios

- Fail-closed: sem componente crítico, os fluxos param em estado seguro (não corrompem).
- Idempotência: reprocessar não duplica; **nunca repetir uma operação externa já confirmada** (§21).
- Evidências preservadas (trilha de auditoria, arquivos); segredos sempre no cofre.

## Cenários

### 1. API/Web perdida
- **Detect:** health externo falha. **Contain:** remover instância do balanceador.
- **Recover:** subir nova instância (imagem imutável ou pacote publicado); `/health/ready`.
- **Reconcile:** tarefas em segundo plano (`TarefaSegundoPlano`) interrompidas são retomadas quando o lease vence
  (até `MaximoTentativas`), sem duplicar.
- **Validate:** login/health por tenant piloto; **Operações → Saúde** sem erro.

### 2. Worker perdido
- **Detect:** heartbeat ausente/estagnação (§31 P31) — alerta `WORKER-FORA-DO-AR`, **Operações → Workers**.
- **Contain:** nada a drenar (o Worker não atende usuários); um backup interrompido é marcado `Falhou`
  (`Interrompido`) na janela seguinte.
- **Recover:** subir/reiniciar o serviço do Worker. **Reconcile:** o backup agendado recupera a janela perdida ao
  subir.
- **Validate:** batida recente em Operações → Workers; próximo backup concluído em Operações → Backups.

### 3. Banco perdido
- **Detect:** readiness/DB down. **Contain:** parar escritas (fail-closed).
- **Recover:** restaurar (seção 8 e [`BACKUP-RESTORE-DR.md`](../BACKUP-RESTORE-DR.md)) respeitando o RPO.
- **Reconcile:** conferir tarefas em segundo plano e integrações do seu domínio; não repetir operações externas já
  confirmadas (§21).
- **Validate:** integridade de tenants/auditoria (§67).

### 4. Storage perdido
- **Detect:** falha de escrita/leitura de arquivos. **Contain:** fluxos que dependem de storage falham fechado.
- **Recover:** restaurar storage por tenant (privado, artefato `Storage`). **Reconcile:** conferir os metadados
  (`arquivo_importacao`) contra os arquivos restaurados (SHA-256).
- **Validate:** uploads/evidências acessíveis e isolados (§58).

### 5. Host perdido
- **Detect:** nó indisponível. **Contain:** drenar. **Recover:** recriar em nó saudável.
- **Reconcile/Validate:** conforme os componentes afetados acima.

### 6. Cofre indisponível (Key Vault ou cofre do Windows)
- **Detect:** falha ao resolver segredo. **Contain:** operações que precisam de segredo **falham fechado** (nunca fallback inseguro).
- **Recover:** restaurar acesso ao cofre. **Validate:** segredos resolvem; nenhum segredo em log (§KEYVAULT-DOWN).

### 7. Integração externa indisponível (do seu domínio)
- **Detect:** **Operações → Integrações** (frescor do último sucesso), falhas/rate limit. **Contain:** timeout/backoff;
  não perder pedidos (fila durável) (§31/§32).
- **Recover:** retomar quando o serviço voltar. **Reconcile:** resolver resultados desconhecidos consultando o
  sistema externo antes de reenviar (§21).
- **Validate:** estados consistentes entre o portal e o sistema externo.

## Ações proibidas

- **NÃO** repetir operação externa já confirmada. **NÃO** sobrescrever resultado oficial de um sistema externo com
  cálculo local.
- **NÃO** usar fallback inseguro de segredo/cert. **NÃO** apagar evidência (auditoria, arquivos). **NÃO** inventar
  RTO/RPO (§78).

## Pós-DR

- Registrar RTO/RPO **observados**; abrir incidente com causa-raiz; verificar consistência das tarefas em segundo
  plano, da trilha de auditoria, dos backups e das integrações do seu domínio (§21).

## 8. Servidor perdido (Windows/IIS + MySQL + cofre do Windows) — passo a passo

Cenário: o servidor de produção (ou o disco dele) se perdeu. Recupera-se em **outra máquina** a partir de três coisas
que **não** estavam só nele (auditoria de 08/10/2026, Onda 2):

| O que | Onde deve estar | Quem gera |
|---|---|---|
| Backups cifrados (`*.bak.enc`) — catálogo, base de cada contribuinte e arquivos de cada contribuinte | cópia externa (`ArmazenamentoBackup:DiretorioOffsite`, outro disco/servidor) | Worker, todo dia (`configurar-iis.ps1` liga) |
| Pacote do cofre (`*.portalbasecofre`) — chaves (backup, MFA, hash, auditoria), senhas do MySQL, senha do SMTP e segredos de integração | fora do servidor (cofre físico/pendrive) | `cofre-windows.ps1 -Acao Exportar` |
| Senha do pacote do cofre | gerenciador de senhas da empresa, **separada** do pacote | quem exportou |

Sem o pacote do cofre (ou sem a senha dele) **nenhum backup decifra** — por isso a exportação é refeita a cada
mudança de segredo e conferida no ensaio trimestral.

**Objetivos:** RPO alvo **24 h** (backup diário; o que entrou depois do último backup se perde). RTO: medir no
ensaio (seção 8.9) — é dominado pelo tamanho das bases e pela instalação do servidor.

### 8.1 Preparar a máquina nova

1. Windows Server com IIS, **.NET 10 Hosting Bundle** e **MySQL Server 8.4** (com `mysqldump.exe`/`mysql.exe`).
2. Copie da cópia externa para a máquina nova a pasta inteira dos backups (ex.: `D:\Recuperacao\backups`) e, de
   onde estiver guardado, o pacote do cofre (ex.: `D:\Recuperacao\cofre-portalbase.portalbasecofre`).
3. Copie os pacotes publicados da Web e do Worker (mesma versão do servidor perdido, ou mais nova).

### 8.2 Restaurar o cofre

Num PowerShell **de administrador** (Windows PowerShell 5.1), na pasta `scripts`:

```powershell
.\cofre-windows.ps1 -Acao Preparar  -Diretorio D:\PortalBase\cofre
.\cofre-windows.ps1 -Acao Restaurar -Diretorio D:\PortalBase\cofre -Arquivo D:\Recuperacao\cofre-portalbase.portalbasecofre
.\cofre-windows.ps1 -Acao Testar    -Diretorio D:\PortalBase\cofre -Nome portalbase-prod-chave-backup
```

A senha do pacote é pedida sem eco; senha errada ou pacote alterado não gravam nada. Os segredos são regravados
com o DPAPI **desta** máquina. (A ACL definitiva da pasta é aplicada pelo `configurar-iis.ps1` no passo 8.6.)

### 8.3 Escolher os artefatos

Na pasta dos backups, o mais **recente** de cada alvo (o nome começa pelo carimbo UTC `aaaammddThhmmssZ`):

| Alvo | Pasta |
|---|---|
| Catálogo | `catalogocompleto\catalogo\` |
| Base de um contribuinte | `tenantcompleto\<id do contribuinte sem hífens>\` |
| Arquivos de um contribuinte | `storage\<id do contribuinte sem hífens>\` |

### 8.4 Decifrar

Para cada artefato escolhido (na pasta publicada da Web):

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
.\PortalBase.Web.exe decifrar-backup --arquivo D:\Recuperacao\backups\catalogocompleto\catalogo\<arquivo>.bak.enc `
    --saida D:\Recuperacao\claro\catalogo.sql --chave portalbase-prod-chave-backup --cofre D:\PortalBase\cofre
```

- Em qualquer outra máquina (até sem o cofre restaurado), troque `--cofre <pasta>` por `--pacote-cofre <arquivo>`
  (a senha do pacote é pedida sem eco).
- O comando não usa banco, nunca sobrescreve a saída e não deixa arquivo parcial: chave errada ou artefato
  alterado/truncado falham sem gravar nada. Ele mostra o **SHA-256 do artefato** (compare com o código `#…` sob o Alvo em
  Operações → Backups, se ainda tiver uma cópia do catálogo) e o **tipo do conteúdo** (dump SQL ou ZIP).
- Os arquivos decifrados são dados do contribuinte **em claro**: mantenha-os numa pasta só de administradores e apague-os
  ao terminar.

### 8.5 Restaurar o MySQL

1. Recrie as contas e os grants (ver [Contas MySQL](../AMBIENTE-LOCAL.md#contas-mysql-menor-privilégio)) com as
   **mesmas senhas** que estão no cofre restaurado (ou com senhas novas e, depois, `cofre-windows.ps1 -Acao Definir`
   para cada uma).
2. Catálogo (base vazia, mesma collation):

   ```powershell
   mysql -u root -p -e "CREATE DATABASE portalbase_catalogo CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs"
   cmd /c "mysql --default-character-set=utf8mb4 -u root -p portalbase_catalogo < D:\Recuperacao\claro\catalogo.sql"
   ```

3. Liste os contribuintes e as bases deles a partir do catálogo restaurado:

   ```sql
   SELECT REPLACE(Id, '-', '') AS pasta, NomeBaseFisica, RazaoSocial FROM portalbase_catalogo.tenant;
   ```

4. Para cada contribuinte: decifre o artefato de `tenantcompleto\<pasta>\` (passo 8.4), crie a base com o
   `NomeBaseFisica` e restaure:

   ```powershell
   mysql -u root -p -e "CREATE DATABASE portalbase_tenant_x CHARACTER SET utf8mb4"
   cmd /c "mysql --default-character-set=utf8mb4 -u root -p portalbase_tenant_x < D:\Recuperacao\claro\tenant_x.sql"
   ```

5. Devolva à conta de runtime o acesso às bases (o provisionamento fazia isso base a base):

   ```sql
   SELECT CONCAT('GRANT SELECT, INSERT, UPDATE, DELETE ON `', NomeBaseFisica, '`.* TO ''portalbase_app''@''%'';')
     FROM portalbase_catalogo.tenant;   -- execute as linhas geradas
   ```

### 8.6 Restaurar os arquivos e publicar

1. Para cada contribuinte, decifre o artefato de `storage\<pasta>\` (sai um ZIP) e extraia `arquivos\*` em
   `<DiretorioBase>\dados\<pasta>\`.
2. Publique a Web e o Worker e rode o `configurar-iis.ps1` com o **mesmo** `-AmbienteCofre`, o mesmo `-DiretorioBase`
   e o `-DiretorioOffsite` (ver [DEPLOY.md](../DEPLOY.md), seção 1.1). Os segredos restaurados são **mantidos** (o script
   só cria o que falta); ele aplica as ACLs, grava a configuração e liga o backup automático.

### 8.7 Conferir

- Login de um administrador (o MFA cadastrado continua valendo: a chave de MFA veio no pacote).
- **Operações → Saúde** e **Operações → Workers** sem erro; **Auditoria** abre (a chave de integridade é a mesma).
- Cada contribuinte abre, com os arquivos esperados (Dados → Arquivos) e os dados do seu domínio.
- **Operações → Backups → Fazer backup agora** conclui e a coluna **Cópia externa** mostra *Conferida*.
- Integrações do seu domínio: **não** repita operações externas já confirmadas antes do desastre; consulte o
  sistema externo para as pendentes.

### 8.8 Depois

- Apague a pasta com os arquivos decifrados (`D:\Recuperacao\claro`).
- Exporte o cofre de novo (o pacote antigo continua válido, mas a máquina mudou e segredos podem ter sido trocados).
- Registre o RTO/RPO **observados** e abra o incidente com a causa-raiz.

### 8.9 Ensaio trimestral (obrigatório)

Uma vez por trimestre, numa máquina/VM de teste (nunca no servidor de produção): passos 8.1 a 8.7 com os backups e o
pacote reais. Registre: data, versão, tempo total (RTO medido), idade do backup usado (RPO), problemas e correções
no runbook. Ensaio que falha é incidente: corrija antes do próximo backup.

## Restauração ordenada a partir de backups (Prompt 50)

Quando a recuperação exigir **restaurar de backups** (não apenas recriar componentes stateless), siga a
ordem e as regras de [`BACKUP-RESTORE-DR.md`](../BACKUP-RESTORE-DR.md). Resumo:

1. **Catalog DB** primeiro (metadados de tenants) — seção 8.5.
2. **Tenant DBs** só após catálogo consistente — seção 8.5, ou pela tela **Operações → Backups** para um contribuinte
   (ver [`BACKUP-RESTORE-DR.md`](../BACKUP-RESTORE-DR.md), seção 9) (nunca A→B).
3. **Storage** por tenant (confira hashes; arquivo íntegro ⇒ hash igual).
4. **Configuração** (sem segredos) e **segredos por referência** recuperados do **cofre** (nunca do backup);
   chaves e certificados vêm do cofre, nunca como arquivo comum.
5. **Serviços + Workers** no ambiente de recovery; **validação** ponta a ponta e isolamento por tenant.

Regras inegociáveis (Prompt 50): restaurar sempre em **base temporária** (nunca sobrescrever produção);
**verificar hash antes de restaurar** (corrupção ⇒ recusa); **nunca** restaurar backup de A em B; **nunca**
apagar o único backup; separar **RTO/RPO alvo** de **medido/comprovado**; **nunca destruir produção para
testar DR**. Neste ambiente, backup/restore **FÍSICO** de MySQL e o **drill de DR** estão **BLOCKED**
(sem MySQL/ambiente de recovery) — a lógica é provada por testes; o físico não é declarado como validado.
