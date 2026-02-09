# Orbit - Documento de Features para Design

> Este documento descreve todas as funcionalidades do Orbit, um app de rastreamento de habitos com inteligencia artificial.
> Destinado ao designer para criacao do Figma.

---

## 1. Autenticacao

### 1.1 Tela de Registro
- Campos: Nome, Email, Senha
- Validacao de senha: minimo 8 caracteres, 1 maiuscula, 1 minuscula, 1 numero
- Validacao de email unico (mostra erro se ja existe)
- Apos registro, redireciona para login

### 1.2 Tela de Login
- Campos: Email, Senha
- Botao de login
- Link para tela de registro
- Apos login, recebe token JWT e acessa o app

---

## 2. Habitos

### 2.1 Lista de Habitos (Tela Principal)
- Exibe apenas habitos ativos de nivel superior (top-level)
- Cada habito mostra:
  - Titulo
  - Descricao (opcional)
  - Tags associadas (com cor e nome)
  - Status de conclusao do dia (logado ou nao)
  - Indicador de habito recorrente vs tarefa unica
  - Indicador de habito negativo (mau habito)
  - Data de vencimento (DueDate)
  - Sub-habitos expandiveis (filhos)
- Filtragem por tags (selecionar uma ou mais tags para filtrar)
- Botao de criar novo habito

### 2.2 Criacao de Habito
- Campos:
  - **Titulo** (obrigatorio)
  - **Descricao** (opcional)
  - **Tipo**: Recorrente ou Tarefa Unica
  - **Frequencia** (apenas para recorrentes):
    - Unidade: Diario, Semanal, Mensal, Anual
    - Quantidade: A cada X dias/semanas/meses/anos
  - **Dias da semana** (opcional, apenas quando frequencia = a cada 1 dia):
    - Checkboxes: Segunda, Terca, Quarta, Quinta, Sexta, Sabado, Domingo
    - Exemplo: "Academia toda segunda, quarta e sexta"
  - **Data de vencimento** (DueDate): Seletor de data
  - **Mau Habito** (toggle): Marca como habito negativo (ex: fumar, roer unha)
  - **Sub-habitos**: Lista dinamica para adicionar sub-habitos por titulo
    - Exemplo: "Rotina matinal" com sub-habitos "Meditar", "Journaling", "Alongar"
- Caminho simples: Apenas titulo + tipo
- Caminho avancado: Todos os campos

### 2.3 Edicao de Habito
- Mesmos campos da criacao, pre-preenchidos
- Permite alterar titulo, descricao, frequencia, dias, data de vencimento, flag de mau habito

### 2.4 Registro de Habito (Log)
- Botao de check/toggle no habito
- Comportamento de toggle:
  - Se nao logado hoje: marca como concluido (cria log)
  - Se ja logado hoje: desmarca (remove log)
- Campo opcional de **nota** ao registrar (ex: "Me senti bem", "Foi dificil hoje")
- Para habitos recorrentes: avanca automaticamente o DueDate para a proxima data
- Para tarefas unicas: marca como concluida (IsCompleted)
- Para maus habitos: permite multiplos registros por dia (cada recaida)

### 2.5 Exclusao de Habito
- Exclusao suave (soft delete) - habito fica inativo mas dados sao preservados
- Confirmacao antes de excluir

### 2.6 Sub-habitos (Hierarquia Pai-Filho)
- Qualquer habito pode ser pai de outros habitos
- Sub-habitos aparecem aninhados dentro do habito pai
- Expansao/colapso dos sub-habitos na lista
- Cada sub-habito e rastreado independentemente
- Sub-habitos herdam a frequencia do pai

### 2.7 Maus Habitos (Habitos Negativos)
- Flag visual distinto (icone ou cor diferente) para diferenciar de habitos positivos
- Logica invertida:
  - O streak conta dias SEM registrar (dias sem recair)
  - Taxa de conclusao = dias NAO logados
  - Cada log representa uma recaida
- Permite multiplos logs por dia (multiplas recaidas)

---

## 3. Metricas e Estatisticas

### 3.1 Metricas por Habito
- **Streak atual**: Dias consecutivos cumprindo o habito (ou sem recair, para maus habitos)
- **Maior streak**: Recorde historico de dias consecutivos
- **Taxa de conclusao semanal**: % dos ultimos 7 dias
- **Taxa de conclusao mensal**: % dos ultimos 30 dias
- **Total de conclusoes**: Contagem total de registros
- **Ultima conclusao**: Data do ultimo registro

### 3.2 Historico de Logs
- Lista de todos os registros do habito
- Ordenados por data (mais recente primeiro)
- Cada log mostra: data, nota (se houver), horario de criacao

---

## 4. Visualizacao de Calendario

### 4.1 Calendario de Habitos
- Visualizacao mensal com todos os dias
- Cada dia mostra quais habitos foram concluidos e quais estao pendentes
- Indicadores visuais:
  - Dia completo (todos os habitos do dia concluidos)
  - Dia parcial (alguns concluidos)
  - Dia sem atividade
  - Dia futuro com habitos agendados
- Navegacao entre meses (anterior/proximo)
- Ao clicar em um dia: exibe detalhes dos habitos daquele dia

### 4.2 Calendario - Visao por Habito
- Selecionar um habito especifico para ver seu historico no calendario
- Dias marcados/desmarcados para aquele habito
- Streaks visualizados como sequencias continuas
- Maus habitos: dias marcados aparecem como negativos (recaidas)

### 4.3 Calendario - Tarefas Futuras
- Habitos recorrentes mostram proximas datas esperadas
- Tarefas unicas mostram data de vencimento
- Habitos com dias especificos (ex: seg, qua, sex) aparecem apenas nos dias corretos
- Indicador visual para habitos atrasados (DueDate passou)

---

## 5. Sistema de Tags

### 5.1 Gerenciamento de Tags
- Lista de todas as tags do usuario
- Criar tag: nome + cor (seletor de cor hexadecimal, ex: #FF5733)
- Excluir tag (remove associacoes com habitos, nao exclui habitos)
- Cada tag tem nome unico por usuario

### 5.2 Tags nos Habitos
- Atribuir tag a um habito
- Remover tag de um habito
- Habito pode ter multiplas tags
- Tags exibidas como chips/badges coloridos no habito
- Filtrar lista de habitos por tags selecionadas (multi-selecao)

---

## 6. Chat com IA

### 6.1 Interface de Chat
- Tela de conversa com a IA
- Campo de texto para mensagem do usuario
- Respostas da IA exibidas como mensagens de chat
- A IA entende linguagem natural para gerenciar habitos

### 6.2 Acoes da IA via Chat
- **Criar habito**: "Quero correr todo dia", "Parar de fumar", "Comprar leite amanha"
- **Registrar habito**: "Corri hoje", "Medite e me senti calmo"
- **Atribuir tag**: "Marcar meditacao com a tag bem-estar"
- A IA pode executar multiplas acoes em uma mensagem
- A IA conhece os habitos e tags ativos do usuario (contexto)
- Respostas amigaveis alem das acoes executadas

### 6.3 Criacao Assistida por IA
- Caminho simples: Usuario descreve o que quer e a IA cria o habito completo
  - Detecta automaticamente se e recorrente ou unica
  - Detecta se e mau habito
  - Sugere sub-habitos quando apropriado
  - Define frequencia e dias da semana
- Caminho avancado: Usuario preenche formulario manualmente
- A IA sugere habitos com base no contexto da conversa

---

## 7. Perfil do Usuario

### 7.1 Tela de Perfil
- Exibe: Nome, Email
- Configuracao de fuso horario (timezone)
  - Seletor com fusos IANA (ex: "America/Sao_Paulo", "America/New_York")
  - O fuso horario afeta a resolucao de datas (qual dia e "hoje" para o usuario)
  - Importante para usuarios em diferentes regioes

---

## 8. Inteligencia Adaptativa e Aprendizado de Perfil (Futuro)

### 8.1 Aprendizado do Usuario
- A IA aprende sobre o usuario ao longo do tempo
- Persiste contexto e preferencias no banco de dados
- Adapta sugestoes com base no historico do usuario
- Reconhece padroes de comportamento (horarios, frequencia de falhas)

### 8.2 Sugestoes Inteligentes
- Sugere novos habitos com base no perfil
- Sugere ajustes de frequencia quando o usuario falha consistentemente
- Sugere horarios ideais baseados no padrao de uso
- Encorajamento personalizado em momentos de queda de streak

---

## 9. Inputs Multimodais (Futuro)

### 9.1 Entrada por Visao
- Registrar habito por foto (ex: foto da refeicao saudavel)
- A IA interpreta a imagem e registra o habito correspondente

### 9.2 Entrada por Audio
- Registrar habito por mensagem de voz
- A IA transcreve e interpreta o audio
- Mesmo fluxo do chat por texto, mas por voz

---

## 10. Ecossistema de Alertas Personalizados (Futuro)

### 10.1 Notificacoes
- Lembretes para habitos pendentes do dia
- Alertas de streak em risco ("Voce tem um streak de 15 dias, nao esqueca hoje!")
- Notificacao de habitos atrasados (DueDate passou)
- Celebracao de marcos (streak de 7, 30, 100 dias)

### 10.2 Personalizacao de Alertas
- Configurar horarios de lembrete por habito
- Escolher tipos de notificacao (push, email)
- Tom das mensagens personalizado pela IA
- Frequencia de lembretes configuravel

---

## 11. Internacionalizacao - i18n (Futuro)

### 11.1 Suporte Multi-idioma
- Interface traduzida para multiplos idiomas
- Portugues brasileiro como idioma principal
- Ingles como segundo idioma
- A IA responde no idioma do usuario
- Formatos de data e hora localizados

---

## 12. Navegacao e Layout Geral

### 12.1 Estrutura de Navegacao
- **Tela principal**: Lista de habitos do dia
- **Chat IA**: Interface de conversa com a IA
- **Calendario**: Visualizacao mensal de habitos
- **Tags**: Gerenciamento de tags
- **Perfil**: Configuracoes do usuario
- Navegacao inferior (mobile) ou lateral (desktop)

### 12.2 Elementos Globais
- Header com nome do app e avatar/perfil do usuario
- Indicador de dia atual
- Botao de acao rapida (criar habito ou abrir chat)
- Estado vazio para quando nao ha habitos criados
- Loading states para acoes assincronas
- Mensagens de erro amigaveis (validacao, falhas)
- Confirmacoes para acoes destrutivas (excluir)

---

## Resumo das Telas para o Figma

| # | Tela | Prioridade |
|---|------|------------|
| 1 | Login | Alta |
| 2 | Registro | Alta |
| 3 | Lista de Habitos (Home) | Alta |
| 4 | Criar Habito (simples + avancado) | Alta |
| 5 | Editar Habito | Alta |
| 6 | Detalhe do Habito (metricas + logs) | Alta |
| 7 | Chat com IA | Alta |
| 8 | Calendario (mensal) | Alta |
| 9 | Calendario - Detalhe do dia | Media |
| 10 | Tags (lista + criacao) | Media |
| 11 | Perfil | Media |
| 12 | Alertas/Notificacoes | Futura |
| 13 | Onboarding | Futura |

---

## Notas para o Designer

- **Cores das tags** sao definidas pelo usuario em formato hexadecimal (#FF5733) - o seletor de cor precisa suportar isso
- **Maus habitos** precisam de diferenciacao visual clara dos habitos positivos (cor, icone ou indicador)
- **Sub-habitos** aparecem aninhados - considerar indentacao ou expansao/colapso
- **Toggle de log** precisa de feedback visual claro (animacao de check/uncheck)
- **Streaks** sao um elemento motivacional importante - destaca-los visualmente
- **A IA** responde com texto amigavel + executa acoes - mostrar as acoes executadas junto com a resposta
- **Fuso horario** afeta quando o dia "vira" - importante para usuarios internacionais
- **Estado vazio** necessario para: sem habitos, sem tags, sem logs, primeiro acesso
- **Calendario** deve suportar visualizacao rapida de quais dias foram cumpridos
