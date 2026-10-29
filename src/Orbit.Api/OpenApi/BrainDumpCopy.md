# Brain dump copy review

Tracks thomasluizon/orbit-tickets#1856. This inventory reports every added model-facing string in English and native pt-BR for coordinator approval. The English instructions ship in the model prompt and tool descriptions; the Portuguese entries are review translations. Existing description prefixes are unchanged. No new user-facing runtime sentence is added.

## HabitModelInstructions.BrainDump

**en**

When the person asks to organize one unstructured message into tasks and habits, prepare one combined approval preview with bulk_create_habits. The combined bulk_create_habits preview is the only write of a brain dump turn. Do not make emoji, update, tag, log, skip or reschedule writes for reused or new items unless the message explicitly requests that change. Put any requested change to a reused habit in the same preview item with its habit_id and the requested field; never split that change into a separate write. Keep every actionable item from the message, with one-time tasks and recurring habits distinct. Build this preview from the message and query_habits for reuse; call suggest_habits only when ideas were requested. Do not read calendars, habit months, profile or analytics to organize the message. Finish these reads before calling bulk_create_habits; never put reads and that preview in the same round or prepare a partial preview. Resolve existing activities from the account index or query_habits, and pass habit_id on reused items rather than creating duplicates. Every reused item keeps the stored title verbatim, including its language, accents and casing. For reuse without a requested change, supply only title and habit_id; leave other fields null. Preserve an explicit first date in due_date as well as a supplied start_date anchor. Copy suggestion clock times as HH:mm, dropping only zero seconds. Preserve existing fields unless a change was requested. Do not create task or habit group headings as account records. For additional ideas, pass every already planned activity title in suggest_habits.excluded_activities, using stored titles for reused habits. These ideas must be new activities outside that list and the account, including synonyms, rather than adjustments. Reuse suggest_habits when additional habit ideas are requested; copy each chosen draft unchanged into the combined preview: title, emoji, frequencyUnit to frequency_unit, frequencyQuantity to frequency_quantity, days, dueDate to due_date, dueTime to due_time, dueEndTime to due_end_time and checklistItems to checklist_items with is_checked=false. Map existingHabitId to habit_id. Map reminderMinutesBefore to reminder_enabled and reminder_times, using false and [] when null. Keep empty days and checklists as []. Do not invent quantities, rewrite titles, change times or replace the returned activity. Keep explicit tasks alongside those suggestions. A request to organize is permission to prepare a preview only. The person can approve, edit or reject the entire preview; nothing is saved before approval. Use at most one short sentence without figures, then the combined preview. All figures belong in the preview. Reads used to build it do not render separate habit lists, calendars, profile rows or suggestion blocks. Apply this equally to typed and transcribed messages in the current message's language.

**pt-BR**

Quando a pessoa pedir para organizar uma mensagem livre em tarefas e hábitos, prepare uma única prévia de aprovação com bulk_create_habits. A prévia conjunta de bulk_create_habits é a única operação de escrita de um turno de organização de mensagem livre. Não faça operações de escrita para alterar emojis, atualizar, atribuir tags, registrar, pular ou reagendar itens reaproveitados ou novos, a menos que a mensagem peça explicitamente essa alteração. Inclua qualquer alteração solicitada em um hábito reaproveitado no mesmo item da prévia, com seu habit_id e o campo solicitado; nunca separe essa alteração em outra operação de escrita. Mantenha todos os itens que possam virar ações, distinguindo tarefas pontuais de hábitos recorrentes. Monte essa prévia a partir da mensagem e use query_habits para reaproveitar hábitos; chame suggest_habits apenas quando a pessoa pedir ideias. Não consulte calendários, o histórico mensal de hábitos, o perfil nem análises para organizar a mensagem. Termine essas consultas antes de chamar bulk_create_habits; não coloque consultas e essa prévia na mesma rodada nem prepare uma prévia incompleta. Identifique as atividades existentes pelo índice da conta ou por query_habits e informe habit_id nos itens reaproveitados, em vez de criar duplicatas. Cada item reaproveitado mantém o título exatamente como está salvo, incluindo idioma, acentos e uso de maiúsculas. Para reaproveitar um hábito sem alterações solicitadas, informe apenas title e habit_id; deixe os outros campos como null. Preserve a data explícita de início em due_date, além da âncora de recorrência informada em start_date. Copie os horários das sugestões no formato HH:mm, removendo os segundos apenas quando forem zero. Preserve os campos existentes, a menos que a pessoa tenha pedido uma alteração. Não crie registros na conta para os títulos dos grupos de tarefas ou hábitos. Para pedir ideias adicionais, informe em suggest_habits.excluded_activities o título de cada atividade já planejada, usando os títulos salvos dos hábitos reaproveitados. Essas ideias devem ser atividades novas, diferentes das que estão na lista e na conta, incluindo sinônimos, em vez de ajustes. Reutilize suggest_habits quando a pessoa pedir novas ideias de hábitos; copie cada rascunho escolhido sem alterações para a prévia conjunta: title, emoji, frequencyUnit para frequency_unit, frequencyQuantity para frequency_quantity, days, dueDate para due_date, dueTime para due_time, dueEndTime para due_end_time e checklistItems para checklist_items com is_checked=false. Use habit_id no lugar de existingHabitId. Converta reminderMinutesBefore em reminder_enabled e reminder_times, usando false e [] quando for null. Mantenha days e checklists vazios como []. Não invente quantidades, reformule títulos, altere horários nem substitua a atividade retornada. Mantenha as tarefas explícitas junto dessas sugestões. Um pedido para organizar autoriza apenas a preparação da prévia. A pessoa pode aprovar, editar ou rejeitar a prévia inteira; nada é salvo antes da aprovação. Use no máximo uma frase curta sem números e, em seguida, a prévia conjunta. Todos os números ficam na prévia. As consultas usadas para prepará-la não geram listas de hábitos, calendários, dados de perfil nem blocos de sugestões separados. Aplique essas regras tanto a mensagens digitadas quanto a transcrições, no idioma da mensagem atual.

## bulk_update_habit_emojis description addition

**en**

Use this tool only when the person requests an emoji change. Never infer emojis as a follow-up to a brain dump preview; put a requested emoji change to a reused habit in its bulk_create_habits item with habit_id instead.

**pt-BR**

Use esta ferramenta apenas quando a pessoa pedir uma alteração de emoji. Nunca infira emojis como complemento de uma prévia de organização de mensagem livre; inclua a alteração de emoji solicitada para um hábito reaproveitado no próprio item de bulk_create_habits, com habit_id.

## bulk_create_habits description addition

**en**

Organize an unstructured message into one combined approval preview of tasks and habits. Include every proposed item in habits; reuse an existing activity with habit_id and only its requested changes. Use query_habits for reuse and suggest_habits for requested ideas; calendars, habit months, profile and analytics are not needed. Finish those reads in earlier rounds before this call. Never submit a partial preview alongside a read. Reused items keep the stored title verbatim. Copy selected suggestion draft fields unchanged, including empty arrays and reminder settings; never invent quantities or activities. The preview groups items by habitType and is the entire answer, without separate read blocks. Nothing is saved before approval.

**pt-BR**

Organize uma mensagem livre em uma única prévia de aprovação com tarefas e hábitos. Inclua todos os itens propostos em habits; reaproveite uma atividade existente com habit_id e apenas as alterações solicitadas. Use query_habits para reaproveitar hábitos e suggest_habits para as ideias solicitadas; não é necessário consultar calendários, o histórico mensal de hábitos, o perfil nem análises. Termine essas consultas nas rodadas anteriores a esta chamada. Não envie uma prévia incompleta junto de uma consulta. Os itens reaproveitados mantêm o título exatamente como está salvo. Copie os campos dos rascunhos escolhidos sem alterações, incluindo listas vazias e configurações de lembretes; não invente quantidades nem atividades. A prévia agrupa os itens por habitType e contém a resposta inteira, sem blocos de consulta separados. Nada é salvo antes da aprovação.

## suggest_habits description addition

**en**

For additional ideas in a combined preview, supply excluded_activities with every activity already planned, using stored titles for reused habits. An empty list still requests new activities only. Omit it for ordinary suggestions that may adjust existing habits.

For an unstructured message that also requests habit ideas, keep its explicit tasks and put the chosen draft fields in one bulk_create_habits approval preview; map existingHabitId to habit_id to reuse the habit. Wait for these drafts before preparing that preview, copy their fields unchanged and keep stored titles verbatim. Never invent a quantity, rewrite a draft title or replace its activity. The combined preview replaces the separate suggestion block.

**pt-BR**

Para pedir ideias adicionais em uma prévia conjunta, informe em excluded_activities cada atividade já planejada, usando os títulos salvos dos hábitos reaproveitados. Uma lista vazia também pede apenas atividades novas. Omita esse campo nas sugestões comuns que podem ajustar hábitos existentes.

Para uma mensagem livre que também peça ideias de hábitos, mantenha as tarefas explícitas e coloque os campos dos rascunhos escolhidos em uma única prévia de aprovação de bulk_create_habits; use habit_id no lugar de existingHabitId para reaproveitar o hábito. Aguarde esses rascunhos antes de preparar a prévia, copie seus campos sem alterações e mantenha os títulos exatamente como estão salvos. Não invente quantidades, reformule o título de um rascunho nem substitua sua atividade. A prévia conjunta substitui o bloco separado de sugestões.

## Paired consumer labels

The API emits machine values only. The paired UI owns these proposed localized labels and their approval:

| Machine value | en | pt-BR |
| --- | --- | --- |
| Task | Tasks | Tarefas |
| Habit | Habits | Hábitos |
| General | General | Geral |

The existing approve, edit and reject labels remain unchanged.

## Creation read dependency

The typed `awaiting_reads` result supplies this model instruction when a bulk
creation arrives alongside reads. It adds no fixed user-facing sentence.

**en**

Nothing was previewed or saved. Read the results from this round first, then call bulk_create_habits once with the complete set. Reuse each existing habit's ID and stored title verbatim. Copy selected suggestion draft fields unchanged, without inventing quantities, times or activities.

**pt-BR**

Nenhuma prévia foi preparada e nada foi salvo. Leia primeiro os resultados desta rodada e depois chame bulk_create_habits uma única vez com todos os itens. Reaproveite o ID de cada hábito existente e mantenha o título exatamente como está salvo. Copie os campos dos rascunhos escolhidos sem alterações, sem inventar quantidades, horários ou atividades.

## Bulk item schema descriptions

| Field | en | pt-BR |
| --- | --- | --- |
| habit_id | The stored habit ID from query_habits or existingHabitId from suggest_habits. Required for reuse. Keep its stored title verbatim; supply only requested changes and leave other fields null. Null means a new activity. | O ID do hábito salvo, retornado por query_habits ou em existingHabitId por suggest_habits. Obrigatório para reaproveitar o hábito. Mantenha o título exatamente como está salvo; informe apenas as alterações solicitadas e deixe os outros campos como null. Null indica uma atividade nova. |
| start_date | YYYY-MM-DD recurrence anchor. Also set due_date to the requested first date so it is visible in the approval preview. | Âncora da recorrência no formato YYYY-MM-DD. Informe também a primeira data solicitada em due_date para que ela apareça na prévia de aprovação. |
| due_time | HH:mm. Suggestion times with zero seconds are accepted as HH:mm:ss. | HH:mm. Horários das sugestões com segundos iguais a zero também são aceitos no formato HH:mm:ss. |
| due_end_time | HH:mm end time or deadline. Suggestion times with zero seconds are accepted as HH:mm:ss. | Horário de término ou limite no formato HH:mm. Horários das sugestões com segundos iguais a zero também são aceitos no formato HH:mm:ss. |

## query_habits description addition

**en**

For an unstructured task and habit preview, use this lookup for existing IDs and stored titles; no calendar, habit month, profile or analytics read is needed.

**pt-BR**

Para preparar uma prévia de tarefas e hábitos a partir de uma mensagem livre, use esta consulta para obter os IDs e os títulos dos hábitos salvos; não é necessário consultar calendários, o histórico mensal de hábitos, o perfil nem análises.

## get_habit_month description addition

**en**

Organizing an unstructured message into a task and habit preview needs query_habits for reuse, not a habit month read.

**pt-BR**

Para organizar uma mensagem livre em uma prévia de tarefas e hábitos, use query_habits para reaproveitar hábitos existentes. Não é necessário consultar o histórico mensal de hábitos.

## get_calendar_overview description addition

**en**

Organizing an unstructured message into a task and habit preview needs query_habits for reuse, not a calendar read.

**pt-BR**

Para organizar uma mensagem livre em uma prévia de tarefas e hábitos, use query_habits para reaproveitar hábitos existentes. Não é necessário consultar o calendário.

## Additional idea exclusions

The optional `excluded_activities` argument is a list of up to 100 activity titles,
each containing letters or digits and at most 200 characters. Omission or null
preserves ordinary adjustment suggestions; a supplied list, including `[]`, asks
for new activities only. Invalid arguments reuse `HABIT_SUGGESTION_INVALID` and its
existing localized sentence. Exhausted generation retries reuse
`HABIT_SUGGESTION_UNAVAILABLE` and its existing localized sentence.

| Source | en | pt-BR |
| --- | --- | --- |
| excluded_activities description | Activities already planned for the combined preview. Return new activities outside this list and the account's existing habits, including synonyms; never offer those activities as adjustments. | Atividades já planejadas para a prévia conjunta. Retorne atividades novas, diferentes das que estão nesta lista e dos hábitos existentes na conta, incluindo sinônimos; nunca ofereça essas atividades como ajustes. |
| Generator instructions | When excludedActivities is not null, propose only new activities outside that list and every existing account habit, including synonyms and renamed variants. Never return those activities as adjustments; existingHabitId must be null. An empty excludedActivities list still excludes existing account habits. A null list keeps the ordinary adjustment rules below. | Quando excludedActivities não for null, proponha apenas atividades novas, diferentes das que estão nessa lista e de todos os hábitos existentes na conta, incluindo sinônimos e variações de nome. Nunca retorne essas atividades como ajustes; existingHabitId deve ser null. Uma lista excludedActivities vazia também exclui os hábitos existentes na conta. Uma lista null mantém as regras comuns de ajuste abaixo. |
| Generator correction | Return a new activity outside excludedActivities and the account's existing habits, including synonyms; do not propose an adjustment. | Retorne uma atividade nova, diferente das que estão em excludedActivities e dos hábitos existentes na conta, incluindo sinônimos; não proponha um ajuste. |
| Ordinary adjustment condition | For ordinary suggestions with excludedActivities null, never duplicate an existing activity, including synonyms: offer an adjustment using existingHabitId instead. | Nas sugestões comuns com excludedActivities null, nunca duplique uma atividade existente, incluindo sinônimos: ofereça um ajuste usando existingHabitId. |

The runtime strings above are model-facing English. The Portuguese entries are
review translations. No new person-visible sentence, block, or response enum is
introduced. The existing preview copy and reuse rules above remain in force.
