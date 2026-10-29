# Brain dump copy review

Tracks thomasluizon/orbit-tickets#1856. This inventory reports every added model-facing string in English and native pt-BR for coordinator approval. The English instructions ship in the model prompt and tool descriptions; the Portuguese entries are review translations. Existing description prefixes are unchanged. No new user-facing runtime sentence is added.

## HabitModelInstructions.BrainDump

**en**

When the person asks to organize one unstructured message into tasks and habits, prepare one combined approval preview with bulk_create_habits. Keep every actionable item from the message, with one-time tasks and recurring habits distinct. Resolve existing activities from the account index or query_habits, and pass habit_id on reused items rather than creating duplicates. Preserve existing fields unless a change was requested. Do not create task or habit group headings as account records. Reuse suggest_habits when additional habit ideas are requested; its editable drafts can supply the proposed habit fields, and existingHabitId becomes habit_id in the combined preview. Keep explicit tasks alongside those suggestions. A request to organize is permission to prepare a preview only. The person can approve, edit or reject the entire preview; nothing is saved before approval. Use at most one short sentence without figures, then the combined preview. All figures belong in the preview. Apply this equally to typed and transcribed messages in the current message's language.

**pt-BR**

Quando a pessoa pedir para organizar uma mensagem livre em tarefas e hábitos, prepare uma única prévia de aprovação com bulk_create_habits. Mantenha todos os itens que possam virar ações, distinguindo tarefas pontuais de hábitos recorrentes. Identifique as atividades existentes pelo índice da conta ou por query_habits e informe habit_id nos itens reaproveitados, em vez de criar duplicatas. Preserve os campos existentes, a menos que a pessoa tenha pedido uma alteração. Não crie registros na conta para os títulos dos grupos de tarefas ou hábitos. Reutilize suggest_habits quando a pessoa pedir novas ideias de hábitos; os rascunhos editáveis podem fornecer os campos propostos, e existingHabitId passa a ser habit_id na prévia conjunta. Mantenha as tarefas explícitas junto dessas sugestões. Um pedido para organizar autoriza apenas a preparação da prévia. A pessoa pode aprovar, editar ou rejeitar a prévia inteira; nada é salvo antes da aprovação. Use no máximo uma frase curta sem números e, em seguida, a prévia conjunta. Todos os números ficam na prévia. Aplique essas regras tanto a mensagens digitadas quanto a transcrições, no idioma da mensagem atual.

## bulk_create_habits description addition

**en**

Organize an unstructured message into one combined approval preview of tasks and habits. Include every proposed item in habits; reuse an existing activity with habit_id and only its requested changes. The preview groups items by habitType. Nothing is saved before approval.

**pt-BR**

Organize uma mensagem livre em uma única prévia de aprovação com tarefas e hábitos. Inclua todos os itens propostos em habits; reaproveite uma atividade existente com habit_id e apenas as alterações solicitadas. A prévia agrupa os itens por habitType. Nada é salvo antes da aprovação.

## suggest_habits description addition

**en**

For an unstructured message that also requests habit ideas, keep its explicit tasks and put the chosen draft fields in one bulk_create_habits approval preview; map existingHabitId to habit_id to reuse the habit.

**pt-BR**

Para uma mensagem livre que também peça ideias de hábitos, mantenha as tarefas explícitas e coloque os campos dos rascunhos escolhidos em uma única prévia de aprovação de bulk_create_habits; use habit_id no lugar de existingHabitId para reaproveitar o hábito.

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
