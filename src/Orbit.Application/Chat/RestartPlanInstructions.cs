namespace Orbit.Application.Chat;

public static class RestartPlanInstructions
{
    public const string English = "When the person asks for help restarting after missed habits, read the remaining habits due today with query_habits, including overdue entries. "
        + "Read further pages when needed before proposing the complete plan. Use the returned habits and the person's preferences to propose a smaller set for today and later dates for the rest. "
        + "Call bulk_reschedule_habits once with revised_items containing every selected habit and its proposed due_date, including today's choices. "
        + "Preserve titles, cadence, times, reminders, checklists and past records. Never mark missed habits done or skipped. "
        + "Use the existing preview for approval, editing or rejection; nothing changes before approval. "
        + "Set orbit_read_complete to false while gathering the plan and orbit_write_complete to true when the complete plan is ready. "
        + "Return the preview alone, with no extra read blocks or repeated facts. Advice without a request to change the plan remains coaching.";

    public const string Portuguese = "Quando a pessoa pedir ajuda para retomar os hábitos depois de deixar alguns para trás, consulte os hábitos pendentes de hoje com query_habits, incluindo os atrasados. "
        + "Consulte as próximas páginas quando necessário antes de propor o plano completo. Use os hábitos retornados e as preferências da pessoa para propor menos atividades para hoje e datas posteriores para as demais. "
        + "Chame bulk_reschedule_habits uma vez com revised_items contendo cada hábito selecionado e a due_date proposta, incluindo os escolhidos para hoje. "
        + "Preserve títulos, frequência, horários, lembretes, checklists e registros anteriores. Não marque os hábitos atrasados como concluídos nem pulados. "
        + "Use a prévia existente para aprovar, editar ou rejeitar; nada muda antes da aprovação. "
        + "Defina orbit_read_complete como false durante a consulta e orbit_write_complete como true quando o plano completo estiver pronto. "
        + "Retorne apenas a prévia, sem blocos extras de consulta nem fatos repetidos. Orientações sem pedido para mudar o plano continuam sendo apenas orientações.";
}
