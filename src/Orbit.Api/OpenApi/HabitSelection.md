# Habit selection

Tracks thomasluizon/orbit-tickets#1716.

`query_habits` accepts optional boolean `for_selection`. True means the lookup supplies
candidates for `ask`, with `orbit_read_complete=false`; false means the read itself
answers the request. The provider receives this distinction in the tool description,
argument description and ask instructions. Choosing records requires `ask` even when
there is a single candidate. `choice` resolves a singular target, and `pickList` holds
items for a supported bulk action. The existing answer and approval paths prepare
the write after selection, without treating the selection as approval.

The read producer keeps typed selection candidates on its server-side card. A searched
lookup with multiple matches also keeps candidates when the model declares a list read.
The latter can finish as a normal list, but a subsequent write cannot select one of those
IDs without the person's answer. Writes to unrelated records still reach their existing
execution path. Read results run before writes from the same model round.
The write guard consumes only these typed facts. It rejects the call before operation
dispatch or preview creation and reports the missing selection back to the model.

Selection results expose candidates and an instruction to call `ask`, without a read
directive. The composition suppresses the candidate list while a selection is unanswered.
If the model still fails to ask, the answer is one localized clarification question,
without a write or a list. An issued ask block takes precedence over that fallback.
No message keyword triggers a tool, changes a tool name or supplies a replacement answer.
Clients advertising ask forms require an explicit false `for_selection` to finish a
declared-complete habit read without continuation. Older callers may omit the argument;
searched duplicate matches still protect writes. No HTTP DTO or strict response enum changes.

Gate Seven fixtures preserve the recorded arguments, visible answer and grader reason.
Tests split recorded rounds at the model-service port and bind redacted identifiers
to real domain habits. The pick-list continuation comes from the existing captured
ask fixture. Explicit-purpose variants change only the new model argument; the single
candidate variant removes the second item. No live model success is inferred from replay.

## Copy for approval

An invalid or unsupported ask form can recover through one model-emitted ask retry.
Only the ask call is accepted on that continuation; a write or another tool cannot
use the clarification retry. Another invalid ask falls back to the model's text.
Client capability, feature-flag and credential restrictions still disable issuing blocks.

The English column contains each added runtime string or added fragment. The Portuguese
column is its review translation. Model instructions remain English at runtime; fallback
questions select the account or client language through the existing locale resolver.

| Location | English | pt-BR |
| --- | --- | --- |
| Ask validation retry | Retry ask once with valid arguments and a supported form: {forms}. choice requires question and options; pickList requires title, action and items; question requires questions. | Tente ask mais uma vez com argumentos válidos e um formato compatível: {forms}. choice exige question e options; pickList exige title, action e items; question exige questions. |
| QueryHabitsTool description addition | For choosing records or resolving a write target, set for_selection=true and orbit_read_complete=false, then call ask. A selection lookup never finishes the answer or authorizes a write, even with one candidate. Multiple matches require the person's choice. | Para escolher registros ou identificar o alvo de uma alteração, use for_selection=true e orbit_read_complete=false e depois chame ask. Uma consulta para seleção nunca encerra a resposta nem autoriza alterações, mesmo com um único candidato. Se houver várias correspondências, a pessoa precisa escolher. |
| for_selection argument | True for a choice or pick list, or to resolve an unclear write target. False only for a read answer. Selection requires ask before any write. | True para uma escolha ou lista de seleção, ou para identificar o alvo de uma alteração. False somente quando a consulta já responde ao pedido. A seleção exige ask antes de qualquer alteração. |
| Selection result instruction | Ask the person to select from these candidates using a supported ask form. Do not choose an ID or prepare any write before the answer. | Peça à pessoa que escolha entre estes candidatos usando um formato de ask compatível. Não escolha um ID nem prepare alterações antes da resposta. |
| Rejected write | Selection is unanswered. Call ask with the returned candidates and prepare no write until the person answers. | A seleção ainda não foi respondida. Chame ask com os candidatos retornados e não prepare alterações até a pessoa responder. |
| Fallback question | Which habit would you like to choose? | Qual hábito você quer escolher? |
| Fallback for a reference | Which habit would you like to use for “{reference}”? | Qual hábito você quer usar para “{reference}”? |
| Terminal read instruction addition | Choosing records requires ask, so query_habits uses for_selection=true and orbit_read_complete=false even for one candidate. | Escolher registros exige ask, então query_habits usa for_selection=true e orbit_read_complete=false mesmo com um único candidato. |
| AskTool description addition | A request to choose habits requires ask, even with one candidate. Use pickList for selecting habits to log or skip, with action and items; it prepares no write. For one unclear existing target, use choice with question and options. If the change is also missing, ask for both target and change with allowFreeText=true. Use only a form the client supports. | Um pedido para escolher hábitos exige ask, mesmo com um único candidato. Use pickList para selecionar hábitos a concluir ou pular, com action e items; isso não prepara alterações. Para um alvo existente que não esteja claro, use choice com question e options. Se a alteração também não foi informada, pergunte pelo hábito e pela alteração com allowFreeText=true. Use somente um formato compatível com o cliente. |
| Ask form argument | choice needs question and options; pickList needs title, action and items; question needs a nonempty questions array. Use only a supported form. | choice exige question e options; pickList exige title, action e items; question exige um array questions com pelo menos um item. Use somente um formato compatível. |
| Ask context addition | A request to choose records needs ask even when only one candidate is available. Use query_habits with for_selection=true and orbit_read_complete=false, then ask with form pickList for a supported bulk action. Choosing is separate from approving: issue the pickList now and prepare its preview only after the selection answer. A habitList cannot replace a choice or pickList. For a singular ambiguous target, use form choice with top-level question and options, never form question with questions=null. If the change is also unspecified, ask which habit and what change in the same question with allowFreeText=true. | Um pedido para escolher registros exige ask mesmo com um único candidato. Use query_habits com for_selection=true e orbit_read_complete=false e depois ask com form pickList para uma ação em lote compatível. Escolher e aprovar são etapas separadas: emita pickList agora e prepare a prévia somente depois da resposta à seleção. habitList não substitui choice ou pickList. Para um alvo singular ambíguo, use form choice com question e options no nível principal, nunca form question com questions=null. Se a alteração também não foi informada, pergunte qual hábito e o que mudar na mesma pergunta com allowFreeText=true. |
| Global target selection rule | When more than one existing record matches and the intended target is unclear, ask one short question and prepare no write or preview until the person answers. Never select all matches or guess an ID for a singular request. Use ask with form choice for existing-record selection, with short distinct labels and descriptions identifying the candidates. Use form pickList for its supported bulk actions when the person wants to choose records, even with one candidate; selection produces a preview only after the answer. A lookup for selection is not a completed read answer and a habit list cannot replace ask. If parts are unsupported, ask the same question in plain text. Do not repeat a question already shown by ask. | Quando mais de um registro corresponder e o alvo pretendido não estiver claro, faça uma pergunta curta e não prepare alterações nem prévias até a pessoa responder. Nunca selecione todas as correspondências nem adivinhe um ID em um pedido singular. Use ask com form choice para escolher registros existentes, com rótulos curtos e distintos e descrições que identifiquem os candidatos. Use form pickList para ações em lote compatíveis quando a pessoa quiser escolher registros, mesmo com um único candidato; a seleção gera uma prévia somente após a resposta. Uma consulta para seleção ainda não responde ao pedido e uma lista de hábitos não substitui ask. Se parts não for compatível, faça a mesma pergunta em texto simples. Não repita uma pergunta já exibida por ask. |
