# Additional idea captures

The g9d projections retain `caseId`, `repeat`, `pass`, `reason`, `toolCalls` and
the `suggest_habits` result from the coordinator's real-call attempts on staging
API `1535efea`. The producer is the `eval-g9d/evals/astra/reports/attempts`
output from `brain-dump-ideas-en` and `brain-dump-ideas-pt-BR`. No person IDs or
account credentials are included. Generator draft IDs omitted by the evidence
projection are rebound locally; all other captured draft fields are unchanged.

The three failing attempts returned stretching, an already planned activity.
The passing r1 attempts returned reading or writing thoughts. The replay at
`IAiIntentService` retains each failed suggestion call and adds only the new
`excluded_activities` argument, supplied explicitly as model structure. It does
not infer exclusions from the message. The initial `IHabitDraftGenerator` output
is the failed capture's duplicate; its correction response is the passing r1
draft in the same language. The subsequent bulk call is the passing capture's
complete proposal. Both composition modes must hold one preview containing the
explicit task, the planned habit and the additional idea, without saving.

Supporting account reads are omitted from this focused suggestion replay; the
other brain dump fixtures cover them. The unnecessary pt-BR analytics call is
retained in the source capture for diagnosis. It was model-selected, and no
handler structure requires it. Real-call pass^3 and paired renderer acceptance
remain owned by thomasluizon/orbit-tickets#1863 and #1864.
