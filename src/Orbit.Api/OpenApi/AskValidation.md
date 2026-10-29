# Ask validation and copy

Tracks thomasluizon/orbit-tickets#1716. The tool schema describes conditional
requirements as well as field bounds. The strict provider schema retains these
descriptions; optional null placeholders are removed before deserialization.
Domain guards remain the source of the constraints and now identify the failing
field. FluentValidation returns that reason through the tool result. The chat
loop sends it back to the model, permits one corrected ask, then asks for prose.

A question form's top-level `allowFreeText` is ignored; each question's setting
controls its answer. The optional heading comes from `title`, then `question`.
A validated single `singleChoice` becomes `choice` when the client supports it
and the heading, option count and labels fit that contract. Forms with an intro,
progress, recommendations, several fields or longer labels retain `question`.
If the client cannot render the resulting form, the model receives a capability
error rather than a stored block. Every path leaves writes behind the existing
approval flow.

The following copy is submitted for coordinator approval. Runtime tool
instructions and validation diagnostics are English, following the existing
model-facing convention. The pt-BR column is their review translation. No fixed
end-user answer or client-visible field is added. Captured model-authored
questions remain unchanged.

## Tool description

| en | pt-BR |
| --- | --- |
| Ask for missing input with a choice, a pick list for a bulk preview, or a multi-field question form. Read account data first. Ends the reply. Never request approval or repeat a preview question. | Peça os dados que faltam com uma escolha, uma lista de seleção para uma prévia em lote ou um formulário com vários campos. Consulte os dados da conta primeiro. Encerra a resposta. Nunca peça aprovação nem repita uma pergunta já feita pela prévia. |
| A request to choose habits requires ask, even with one candidate. Use pickList for selecting habits to log or skip, with action and items; it prepares no write. | Um pedido para escolher hábitos exige ask, mesmo com apenas uma opção. Use pickList para selecionar hábitos que serão registrados ou pulados, com action e items; isso não prepara nenhuma alteração. |
| For a single choice among existing records, use form choice with top-level question and options, even when form question is supported. Reserve form question for multiple fields. If the change is also missing, ask for both target and change in the choice question with allowFreeText=true. Use only a form the client supports. | Para uma única escolha entre registros existentes, use form choice com question e options no nível superior, mesmo quando form question estiver disponível. Reserve form question para vários campos. Se a alteração também não estiver definida, pergunte qual registro e o que mudar na pergunta de choice, com allowFreeText=true. Use apenas um formato aceito pelo cliente. |

## Schema descriptions

| en | pt-BR |
| --- | --- |
| choice requires a nonempty question and 2 to 4 options; omit items, action, questions and progress. pickList requires nonempty title, action and 1 to 10 items; omit options, questions and progress, and set allowFreeText=false. question requires 1 to 10 questions; omit top-level options, items and action and set multiSelect=false. A single record selection uses choice; reserve question for multiple fields. Use only a supported form. | choice exige question preenchido e de 2 a 4 opções; omita items, action, questions e progress. pickList exige title e action preenchidos e de 1 a 10 itens; omita options, questions e progress e defina allowFreeText=false. question exige de 1 a 10 perguntas; omita options, items e action no nível superior e defina multiSelect=false. Para escolher um único registro, use choice; reserve question para vários campos. Use apenas um formato aceito. |
| Question, at most 120 characters. | Pergunta com até 120 caracteres. |
| Required nonempty heading for pickList; optional heading of at most 120 characters for question. | Título obrigatório e preenchido para pickList; título opcional com até 120 caracteres para question. |
| For choice only. pickList always selects multiple items; question requires false or null. | Somente para choice. pickList sempre permite selecionar vários itens; question exige false ou null. |
| For choice only; false for pickList. In form question, each questions entry controls its own allowFreeText; this top-level value is ignored. | Somente para choice; false para pickList. Em form question, cada entrada de questions define seu próprio allowFreeText; o valor no nível superior é ignorado. |
| Optional nonempty introduction of at most 500 characters for question only. Omit for choice and pickList. | Introdução opcional, preenchida e com até 500 caracteres, somente para question. Omita para choice e pickList. |
| For question only. Requires 0 <= current <= total and total >= 1. | Somente para question. Exige 0 <= current <= total e total >= 1. |
| Required for question only. Use unique nonempty question ids. singleChoice and multipleChoice require 2 to 10 options even with allowFreeText=true. scale requires lowLabel and highLabel. slider requires min, max, step and unit. yesNo has no options. Omit fields belonging to another question type. | Obrigatório somente para question. Use identificadores de pergunta únicos e preenchidos. singleChoice e multipleChoice exigem de 2 a 10 opções, mesmo com allowFreeText=true. scale exige lowLabel e highLabel. slider exige min, max, step e unit. yesNo não tem opções. Omita os campos de outros tipos de pergunta. |
| Nonempty unique question id, at most 80 characters. | Identificador de pergunta único, preenchido e com até 80 caracteres. |
| Nonempty prompt, at most 500 characters. | Pergunta preenchida, com até 500 caracteres. |
| Required for singleChoice and multipleChoice. Omit or null for scale, slider and yesNo. Option ids must be unique within the question. | Obrigatório para singleChoice e multipleChoice. Omita ou use null para scale, slider e yesNo. Os identificadores das opções devem ser únicos dentro da pergunta. |
| Nonempty option id, at most 80 characters. | Identificador de opção preenchido, com até 80 caracteres. |
| Nonempty label, at most 120 characters. | Rótulo preenchido, com até 120 caracteres. |
| At most one recommended option, only for singleChoice. | No máximo uma opção recomendada, somente para singleChoice. |
| Allow a custom answer for singleChoice, multipleChoice, or yesNo. Must be false or null for scale and slider. | Permita uma resposta livre para singleChoice, multipleChoice ou yesNo. Deve ser false ou null para scale e slider. |
| Required nonempty label for 1 on a scale, at most 120 characters. Omit for other types. | Rótulo obrigatório e preenchido para o valor 1 de uma escala, com até 120 caracteres. Omita para outros tipos. |
| Required nonempty label for 5 on a scale, at most 120 characters. Omit for other types. | Rótulo obrigatório e preenchido para o valor 5 de uma escala, com até 120 caracteres. Omita para outros tipos. |
| Required slider lower bound. Omit for other types. | Limite inferior obrigatório para slider. Omita para outros tipos. |
| Required slider upper bound, greater than min. Omit for other types. | Limite superior obrigatório para slider, maior que min. Omita para outros tipos. |
| Required positive slider step dividing max minus min exactly. Omit for other types. | Passo positivo obrigatório para slider, que divida exatamente a diferença entre max e min. Omita para outros tipos. |
| Required nonempty slider unit in the person's language, at most 24 characters. Omit for other types. | Unidade obrigatória e preenchida para slider, no idioma da pessoa, com até 24 caracteres. Omita para outros tipos. |
| Nonempty unique option id. | Identificador de opção único e preenchido. |
| Option label, at most 20 characters. | Rótulo da opção, com até 20 caracteres. |
| Supporting text, at most 24 characters. | Texto de apoio, com até 24 caracteres. |
| Nonempty unique item id. | Identificador de item único e preenchido. |
| Nonempty item label. | Rótulo do item preenchido. |
| Supporting text, at most 24 characters. | Texto de apoio, com até 24 caracteres. |
| One bulk item: a habit definition for create_habits, or {habit_id, date} for log_habits/skip_habits. Dates use YYYY-MM-DD. | Um item do lote: a definição de um hábito para create_habits, ou {habit_id, date} para log_habits/skip_habits. As datas usam YYYY-MM-DD. |

## Validation diagnostics

Question-specific failures carry the prefix `questions[{index}].`, where the
index is zero based. The existing exception wrapper may append the parameter
name `definition`. Neither becomes a fixed user-facing answer.

| en | pt-BR |
| --- | --- |
| question for choice or title for pickList must be nonempty. | Preencha question para choice ou title para pickList. |
| options and items must be arrays. | options e items devem ser listas. |
| questions, intro and progress are only allowed for form question. | questions, intro e progress só são aceitos em form question. |
| form must be choice, pickList or question. | form deve ser choice, pickList ou question. |
| question must have at most 120 characters. | question deve ter até 120 caracteres. |
| options must contain 2 to 4 options for choice. | options deve conter de 2 a 4 opções para choice. |
| items and action must be omitted or null for choice. | Omita items e action ou use null para choice. |
| options require nonempty ids and labels, labels up to 20 characters and descriptions up to 24 characters. | As opções exigem identificadores e rótulos preenchidos, rótulos com até 20 caracteres e descrições com até 24 caracteres. |
| options must have unique ids. | Os identificadores de options devem ser únicos. |
| items must contain 1 to 10 items for pickList. | items deve conter de 1 a 10 itens para pickList. |
| options must be omitted or null for pickList. | Omita options ou use null para pickList. |
| action must be create_habits, log_habits or skip_habits for pickList. | action deve ser create_habits, log_habits ou skip_habits para pickList. |
| pickList requires multiSelect=true and allowFreeText=false. | pickList exige multiSelect=true e allowFreeText=false. |
| items require nonempty ids and labels, object arguments and details up to 24 characters. | Os itens exigem identificadores e rótulos preenchidos, arguments como objeto e detalhes com até 24 caracteres. |
| items must have unique ids. | Os identificadores de items devem ser únicos. |
| title must have at most 120 characters. | title deve ter até 120 caracteres. |
| intro must be nonempty and have at most 500 characters. | intro deve estar preenchido e ter até 500 caracteres. |
| question requires options, items and action to be omitted or null at the top level. | Em question, omita options, items e action no nível superior ou use null. |
| question requires multiSelect=false and per-question allowFreeText. | question exige multiSelect=false e allowFreeText definido em cada pergunta. |
| progress requires 0 <= current <= total and total >= 1. | progress exige 0 <= current <= total e total >= 1. |
| questions must contain 1 to 10 questions for form question. | questions deve conter de 1 a 10 perguntas para form question. |
| questions must have unique ids. | Os identificadores de questions devem ser únicos. |
| question must be an object. | A pergunta deve ser um objeto. |
| id must be nonempty and have at most 80 characters. | id deve estar preenchido e ter até 80 caracteres. |
| prompt must be nonempty and have at most 500 characters. | prompt deve estar preenchido e ter até 500 caracteres. |
| options must be omitted or null for scale, slider or yesNo. | Omita options ou use null para scale, slider ou yesNo. |
| allowFreeText must be false or null for scale and slider. | allowFreeText deve ser false ou null para scale e slider. |
| lowLabel and highLabel are only allowed for scale. | lowLabel e highLabel só são aceitos para scale. |
| min, max, step and unit are only allowed for slider. | min, max, step e unit só são aceitos para slider. |
| lowLabel and highLabel must be nonempty and have at most 120 characters for scale. | Para scale, lowLabel e highLabel devem estar preenchidos e ter até 120 caracteres. |
| type must be singleChoice, multipleChoice, scale, slider or yesNo. | type deve ser singleChoice, multipleChoice, scale, slider ou yesNo. |
| options must contain 2 to 10 options for singleChoice or multipleChoice, even with allowFreeText=true. | options deve conter de 2 a 10 opções para singleChoice ou multipleChoice, mesmo com allowFreeText=true. |
| options require nonempty ids up to 80 characters and labels up to 120 characters. | As opções exigem identificadores preenchidos com até 80 caracteres e rótulos preenchidos com até 120 caracteres. |
| options allow at most one recommended option for singleChoice and none for multipleChoice. | options permite no máximo uma opção recomendada para singleChoice e nenhuma para multipleChoice. |
| min and max are required for slider, between -1000000000 and 1000000000, with max > min. | min e max são obrigatórios para slider, entre -1000000000 e 1000000000, com max > min. |
| step must be positive and divide max minus min exactly for slider. | Para slider, step deve ser positivo e dividir exatamente a diferença entre max e min. |
| unit must be nonempty and have at most 24 characters for slider. | Para slider, unit deve estar preenchido e ter até 24 caracteres. |
| Invalid ask arguments. | Argumentos inválidos para ask. |
| ask text is too long: question max 120, option label max 20, option description and item detail max 24 characters. Shorten the text and retry. | O texto de ask ultrapassa o limite: question aceita até 120 caracteres; o rótulo da opção, até 20; a descrição da opção e o detalhe do item, até 24. Encurte o texto e tente novamente. |
| Interactive questions are unavailable. | As perguntas interativas estão indisponíveis. |
| The client does not support this block kind. | O cliente não aceita este tipo de bloco. |
| The ask tool is unavailable. | A ferramenta ask está indisponível. |
| Retry ask once with valid arguments and a supported form: {forms}. choice requires question and options; pickList requires title, action and items; question requires questions. | Tente ask mais uma vez com argumentos válidos e um formato aceito: {forms}. choice exige question e options; pickList exige title, action e items; question exige questions. |
| Ask in plain text instead. | Faça a pergunta em texto simples. |
